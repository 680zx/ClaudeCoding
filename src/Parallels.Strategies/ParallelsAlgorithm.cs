using Parallels.Contracts;
using Parallels.Strategies.Alphas;
using Parallels.Strategies.Execution;
using Parallels.Strategies.Recording;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities;

namespace Parallels.Strategies;

/// <summary>
/// The one and only algorithm class, loaded by both the backtest worker and the
/// live worker.
///
/// There is exactly one of these on purpose. LEAN requires exactly one class
/// derived from the algorithm base type per loaded assembly (spec 2), so a
/// subclass per strategy is not an option — strategies are
/// <see cref="IAlphaModel"/>s registered with <c>AddAlpha</c>, and this class is
/// the host that assembles them. Backtest and live share it so a strategy cannot
/// silently behave differently between the two.
///
/// It is constructed by LEAN via reflection with no arguments, which is why all
/// of its configuration arrives through <see cref="SessionContext"/>.
/// </summary>
public class ParallelsAlgorithm : QCAlgorithm
{
    private readonly List<FaultIsolatingAlphaModel> _wrapped = [];
    private readonly Dictionary<string, TrendFollowingAlphaModel> _trendModels = [];
    private ParallelsSession _session = null!;
    private ChartRecorder? _recorder;
    // Fully qualified: QCAlgorithm exposes a Symbol(string) method that
    // otherwise shadows the type name inside this class.
    private QuantConnect.Symbol _primarySymbol = QuantConnect.Symbol.Empty;

    /// <summary>Alphas disabled mid-session by the fault-isolation wrapper.</summary>
    public IReadOnlyList<string> FaultedAlphaIds =>
        [.. _wrapped.Where(w => !w.IsEnabled).Select(w => w.AlphaId)];

    public override void Initialize()
    {
        _session = SessionContext.Current;

        var alphas = _session.Alphas;
        if (alphas.Count == 0)
            throw new InvalidOperationException("No alphas configured for this session.");

        var resolution = ParseResolution(alphas[0].Resolution);

        ConfigureAccountAndDates(resolution);

        // Subscribe once per distinct symbol across all alphas. Two alphas on the
        // same symbol share one subscription — that sharing is precisely why live
        // is one process hosting N alphas rather than N processes (spec 3.2).
        var subscribed = new Dictionary<string, Symbol>(StringComparer.OrdinalIgnoreCase);
        var warmUpBars = 0;

        foreach (var config in alphas)
        {
            foreach (var ticker in config.Symbols)
            {
                if (subscribed.ContainsKey(ticker)) continue;

                var security = AddCrypto(ticker, resolution, config.Market);
                ApplyCostModels(security);
                subscribed[ticker] = security.Symbol;
            }

            var model = AlphaModelFactory.Create(config, resolution, verbose: _session.VerboseSignals);
            if (model is TrendFollowingAlphaModel trend) _trendModels[config.Id] = trend;

            var wrapper = new FaultIsolatingAlphaModel(
                model,
                config.Id,
                onFault: (id, ex) => Error($"Alpha '{id}' disabled after an unhandled exception: {ex}"),
                log: Log);

            _wrapped.Add(wrapper);
            AddAlpha(wrapper);

            warmUpBars = Math.Max(warmUpBars, AlphaModelFactory.WarmUpBarsFor(config));
        }

        _primarySymbol = subscribed.Values.First();

        // Built-in models (spec 3.3). Insight weighting is what carries each
        // alpha's volatility-derived size through to a portfolio target; the
        // aggregate risk limit is applied after this, in the gate.
        // Rebalance on insight changes only. The scheduled interval is set past
        // any plausible run length rather than to "none", because these models
        // treat an absent schedule as "no constraint" and re-size on every data
        // point instead of never.
        //
        // This matters more than it looks. Left on a schedule, the weighting
        // model maintains a *constant* weight: as a winning position appreciates
        // it is trimmed back toward its target percentage, bar after bar. For a
        // trend follower whose stop and target were fixed at entry that is
        // actively wrong — it sells the winner the ATR target exists to ride, and
        // in this run it turned 7 real decisions into 385 fee-paying orders.
        SetPortfolioConstruction(
            new InsightWeightingPortfolioConstructionModel(TimeSpan.FromDays(3650)));
        SetExecution(new GatedExecutionModel(new ImmediateExecutionModel(), _session.OrderGate, Log));

        // Warm up so the first tradeable bar already has ready indicators rather
        // than the model quietly skipping its first N bars of the requested range.
        SetWarmUp(warmUpBars, resolution);

        if (_session.Mode == SessionMode.Backtest && _session.Job is not null)
            _recorder = new ChartRecorder(_session.Job.JobId, _session.Job.Symbol);
    }

    private void ConfigureAccountAndDates(Resolution resolution)
    {
        if (_session.Mode == SessionMode.Backtest)
        {
            var job = _session.Job
                ?? throw new InvalidOperationException("Backtest session has no job.");

            SetStartDate(job.StartDate.Year, job.StartDate.Month, job.StartDate.Day);
            SetEndDate(job.EndDate.Year, job.EndDate.Month, job.EndDate.Day);
            SetAccountCurrency(job.AccountCurrency);
            SetCash(job.StartingCash);
        }
        else
        {
            // Live takes its cash from the brokerage at setup; setting it here
            // would fight reconciliation rather than help it.
            SetAccountCurrency("USDT");
        }

        // Cash account, not margin: a spot Binance account cannot borrow, and a
        // backtest that assumed otherwise would report returns the live account
        // could never reproduce.
        SetBrokerageModel(BrokerageName.Binance, AccountType.Cash);
        SetBenchmark(_ => 0m);
    }

    /// <summary>
    /// Applies the job's fee and slippage assumptions to a security.
    ///
    /// Explicit rather than inherited from the brokerage model so that the number
    /// used is the one recorded in the run's provenance — a result whose costs
    /// came from an undocumented default is not reproducible in any useful sense.
    /// </summary>
    private void ApplyCostModels(Security security)
    {
        if (_session.Mode != SessionMode.Backtest || _session.Job is null) return;

        security.SetFeeModel(new PercentOfNotionalFeeModel(_session.Job.FeeFraction));
        security.SetSlippageModel(new ConstantSlippageModel(_session.Job.SlippageFraction));
    }

    public override void OnData(Slice slice)
    {
        if (_recorder is null || IsWarmingUp) return;
        if (!slice.Bars.TryGetValue(_primarySymbol, out var bar)) return;

        var indicators = _trendModels.Count > 0
            ? _trendModels.Values.First().LastIndicatorValues
            : new Dictionary<string, decimal>();

        // Plot into LEAN's own charting as well as the companion file, so the
        // series are visible in LEAN's result output and not only in our UI.
        Plot("Price", "close", bar.Close);
        foreach (var (name, value) in indicators)
            if (value != 0m) Plot("Indicators", name, value);

        _recorder.RecordBar(bar, indicators, Portfolio.TotalPortfolioValue);
    }

    public override void OnOrderEvent(OrderEvent orderEvent)
    {
        _recorder?.RecordFill(orderEvent);

        if (orderEvent.Status == OrderStatus.Filled)
            Log($"[fill] {orderEvent.Direction} {orderEvent.FillQuantity} {orderEvent.Symbol.Value} " +
                $"@ {orderEvent.FillPrice} fee={orderEvent.OrderFee}");
    }

    public override void OnEndOfAlgorithm()
    {
        if (_recorder is not null)
        {
            _recorder.WriteTo(_session.OutputDirectory);
            Log($"[chart] wrote {_recorder.CandleCount} candles and {_recorder.MarkerCount} markers " +
                $"to {Path.Combine(_session.OutputDirectory, "chart-data.json")}");
        }

        foreach (var alpha in _wrapped)
            Log($"[alpha] '{alpha.AlphaId}' emitted {alpha.EmittedInsightCount} insights.");

        foreach (var faulted in _wrapped.Where(w => !w.IsEnabled))
            Log($"[alpha-fault] '{faulted.AlphaId}' ran disabled from its first exception onward.");
    }

    private static Resolution ParseResolution(string resolution) =>
        Enum.TryParse<Resolution>(resolution, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unknown resolution '{resolution}'.", nameof(resolution));
}

/// <summary>
/// Percentage-of-notional taker fee, the shape crypto venues actually charge.
///
/// LEAN's <c>ConstantFeeModel</c> charges a flat amount per order, which is an
/// equities convention — using it here would understate cost on large orders and
/// overstate it on small ones.
/// </summary>
public sealed class PercentOfNotionalFeeModel(decimal feeFraction) : FeeModel
{
    private readonly decimal _feeFraction = feeFraction;

    public override OrderFee GetOrderFee(OrderFeeParameters parameters)
    {
        var security = parameters.Security;
        var order = parameters.Order;

        var price = order.Direction == OrderDirection.Buy ? security.AskPrice : security.BidPrice;
        if (price == 0m) price = security.Price;

        var notional = Math.Abs(order.AbsoluteQuantity * price);
        var fee = notional * _feeFraction;

        return new OrderFee(new CashAmount(fee, security.QuoteCurrency.Symbol));
    }
}
