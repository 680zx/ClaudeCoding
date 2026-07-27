namespace Parallels.Contracts;

/// <summary>One OHLC bar. <c>Time</c> is a Unix timestamp in seconds (UTC).</summary>
public sealed record Candle(long Time, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>One indicator sample, aligned to a candle's timestamp.</summary>
public sealed record IndicatorPoint(long Time, decimal Value);

/// <summary>
/// A fill, as drawn on the chart. The indicator snapshot is captured at the
/// moment of the fill so the tooltip can answer "what did the strategy see when
/// it did this?" without the frontend re-deriving anything.
/// </summary>
public sealed record TradeMarker(
    long Time,
    string Side,
    decimal Price,
    decimal Quantity,
    IReadOnlyDictionary<string, decimal> IndicatorValues,
    string? Tag = null);

/// <summary>
/// The chart companion file for one run — deliberately separate from
/// <see cref="BacktestResult"/>, which stays headline-stats-only (spec 3.4).
/// </summary>
public sealed record ChartData
{
    public required string RunId { get; init; }
    public required string Symbol { get; init; }
    public IReadOnlyList<Candle> Candles { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<IndicatorPoint>> Indicators { get; init; } =
        new Dictionary<string, IReadOnlyList<IndicatorPoint>>();
    public IReadOnlyList<TradeMarker> Markers { get; init; } = [];

    /// <summary>Equity curve, same timestamp basis as the candles.</summary>
    public IReadOnlyList<IndicatorPoint> Equity { get; init; } = [];
}
