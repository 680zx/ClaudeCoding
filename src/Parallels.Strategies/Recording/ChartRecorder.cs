using System.Text.Json;
using Parallels.Contracts;
using QuantConnect;
using QuantConnect.Data.Market;
using QuantConnect.Orders;

namespace Parallels.Strategies.Recording;

/// <summary>
/// Accumulates the chart companion file for a run: candles, indicator series,
/// fill markers and the equity curve.
///
/// Written separately from <see cref="BacktestResult"/> (spec 3.4) so the
/// headline stats the UI polls stay small no matter how long the run is.
///
/// Markers are recorded from real <c>OnOrderEvent</c> fills rather than from the
/// signals that requested them. Those are not the same thing — a signal that the
/// gate rejected or the exchange filter rounded away never became a trade, and a
/// chart that drew it would be showing trades that did not happen.
/// </summary>
public sealed class ChartRecorder(string runId, string symbol)
{
    private readonly List<Candle> _candles = [];
    private readonly Dictionary<string, List<IndicatorPoint>> _indicators = [];
    private readonly List<TradeMarker> _markers = [];
    private readonly List<IndicatorPoint> _equity = [];

    private long _lastCandleTime = -1;

    public string RunId { get; } = runId;
    public string Symbol { get; } = symbol;

    public int CandleCount => _candles.Count;
    public int MarkerCount => _markers.Count;

    /// <summary>Most recent indicator snapshot, used to tag a fill with what the model saw.</summary>
    public IReadOnlyDictionary<string, decimal> LastIndicatorValues { get; private set; } =
        new Dictionary<string, decimal>();

    public void RecordBar(TradeBar bar, IReadOnlyDictionary<string, decimal> indicatorValues, decimal equity)
    {
        var time = ToUnixSeconds(bar.EndTime);

        // lightweight-charts requires strictly ascending, de-duplicated times.
        // Warm-up replays and multi-symbol slices can both produce a repeat.
        if (time <= _lastCandleTime) return;
        _lastCandleTime = time;

        _candles.Add(new Candle(time, bar.Open, bar.High, bar.Low, bar.Close));

        foreach (var (name, value) in indicatorValues)
        {
            if (!_indicators.TryGetValue(name, out var series))
                _indicators[name] = series = [];
            series.Add(new IndicatorPoint(time, value));
        }

        _equity.Add(new IndicatorPoint(time, equity));
        LastIndicatorValues = indicatorValues;
    }

    public void RecordFill(OrderEvent orderEvent)
    {
        if (orderEvent.Status != OrderStatus.Filled && orderEvent.Status != OrderStatus.PartiallyFilled)
            return;

        _markers.Add(new TradeMarker(
            ToUnixSeconds(orderEvent.UtcTime),
            orderEvent.Direction == OrderDirection.Buy ? "buy" : "sell",
            orderEvent.FillPrice,
            Math.Abs(orderEvent.FillQuantity),
            new Dictionary<string, decimal>(LastIndicatorValues),
            orderEvent.Message));
    }

    public ChartData Build() => new()
    {
        RunId = RunId,
        Symbol = Symbol,
        Candles = _candles,
        Indicators = _indicators.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IndicatorPoint>)kv.Value),
        Markers = _markers,
        Equity = _equity,
    };

    public void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chart-data.json");
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, Build(), ParallelsJson.Options);
    }

    private static long ToUnixSeconds(DateTime time) =>
        new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
