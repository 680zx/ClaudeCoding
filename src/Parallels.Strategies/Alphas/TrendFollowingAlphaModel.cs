using Parallels.Contracts;
using Parallels.Strategies.Execution;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;

namespace Parallels.Strategies.Alphas;

/// <summary>
/// EMA-cross trend following, sized off ATR.
///
/// This model emits insights and nothing else — it never places an order, never
/// reads the gate, and never touches the portfolio. That separation is the point
/// (spec 5): signal generation is one layer, portfolio construction and
/// execution are others, which is also what lets the risk gateway be
/// unbypassable rather than merely conventional.
///
/// Point-in-time correctness: every decision is taken from indicator values that
/// were updated with bars at or before the bar being acted on, and the emitted
/// insight is dated at the algorithm's current time. Nothing reads ahead.
/// </summary>
public sealed class TrendFollowingAlphaModel : AlphaModel, IProtectiveLevelSource
{
    private readonly TrendFollowingParameters _parameters;
    private readonly Dictionary<Symbol, SymbolState> _state = [];
    private readonly TimeSpan _insightPeriod;

    public TrendFollowingAlphaModel(string name, TrendFollowingParameters parameters, Resolution resolution)
    {
        Name = name;
        _parameters = parameters;

        // An insight stays active until this model explicitly cancels it with a
        // Flat, so the period is a long backstop rather than a holding period.
        // A short period would expire the view mid-trade and make the portfolio
        // model unwind a position the model still holds.
        _insightPeriod = TimeSpan.FromDays(365);
    }

    /// <summary>
    /// Logs every entry and exit decision with the levels behind it. Off by
    /// default — on a two-year hourly run this is a lot of output — but the only
    /// practical way to check that what the report says happened is what the
    /// model actually decided.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>Indicator values for the chart tooltip, read by the recorder after each Update.</summary>
    public IReadOnlyDictionary<string, decimal> LastIndicatorValues { get; private set; } =
        new Dictionary<string, decimal>();

    public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
    {
        foreach (var (symbol, state) in _state)
        {
            if (!data.Bars.TryGetValue(symbol, out var bar)) continue;

            state.Update(bar);
            if (!state.IsReady) continue;

            LastIndicatorValues = state.Snapshot();

            // Warm-up bars exist to prime indicators, not to trade on.
            if (algorithm.IsWarmingUp) continue;

            var fast = state.Fast.Current.Value;
            var slow = state.Slow.Current.Value;
            var atr = state.Atr.Current.Value;
            var price = bar.Close;

            if (price <= 0m || atr <= 0m) continue;

            // Require the EMAs to be meaningfully apart before treating the
            // relationship as a trend. Without this, price oscillating around a
            // single level produces a long tail of round trips whose expected
            // value is strictly negative once fees are paid.
            var separation = Math.Abs(fast - slow) / price;
            if (separation < _parameters.MinCrossSeparationFraction) continue;

            var trendIsUp = fast > slow;

            if (state.IsLong)
            {
                // Exit checks run before anything else, and in this order: an
                // open position's protective levels take precedence over what the
                // trend is currently saying.
                //
                // Levels are evaluated against the bar close, not the bar's high
                // and low. That is the conservative reading: claiming an intrabar
                // stop filled at exactly the stop price would be assuming a
                // resting order that this backtest never placed. Live trading
                // does place resting protective orders, so live can stop out
                // intrabar where this cannot — a difference documented rather
                // than papered over.
                var exit = price <= state.StopPrice ? "atr-stop"
                         : price >= state.TargetPrice ? "atr-target"
                         : !trendIsUp ? "ema-cross-down"
                         : null;

                if (exit is not null)
                {
                    state.Close();

                    // Cancelling the entry insight is what actually closes the
                    // position, and emitting a Flat alongside it is not optional
                    // decoration.
                    //
                    // A Flat insight does not supersede an active Up insight: the
                    // entry insight stays live for its whole period, so the
                    // portfolio model keeps seeing a long view and holds the
                    // position. Left uncancelled, every "exit" degrades into a
                    // small re-weighting of a position that never closes — which
                    // reads on the report as a near-perfect win rate, because a
                    // trade that never closes never books a loss.
                    algorithm.Insights.Cancel([symbol]);

                    if (Verbose)
                        algorithm.Log($"[signal] {algorithm.Time:u} EXIT {exit} px={price:F2} " +
                                      $"stop={state.StopPrice:F2} target={state.TargetPrice:F2} " +
                                      $"held={algorithm.Portfolio[symbol].Quantity}");

                    yield return Insight.Price(symbol, _insightPeriod, InsightDirection.Flat,
                        weight: 0d, sourceModel: Name, tag: exit);
                    continue;
                }

                // Still in the trade and nothing has changed: emit nothing. The
                // entry insight is still active, so the position is already held
                // at its entry weight. Re-emitting each bar would restate the same
                // view and make the portfolio model resize the position as equity
                // drifts, paying a fee per bar for no change of opinion.
                continue;
            }

            // Flat rather than short when the trend is down: a cash crypto
            // account cannot borrow, and a backtest that shorted anyway would
            // report edge the live account could never capture.
            if (!trendIsUp) continue;

            // Size so that being stopped out costs RiskPerTradeFraction of equity.
            // Wider ATR means a wider stop means a smaller position for the same
            // risk budget — this is the "sizing tied to volatility" requirement,
            // and it is why ATR feeds sizing and not just the stop level.
            var stopDistance = atr * _parameters.AtrStopMultiple;
            if (stopDistance <= 0m) continue;

            var targetFraction = Math.Min(
                _parameters.RiskPerTradeFraction * price / stopDistance,
                _parameters.MaxPositionFraction);

            if (targetFraction <= 0m) continue;

            state.Open(
                entryWeight: targetFraction,
                stop: price - stopDistance,
                target: price + atr * _parameters.AtrTakeProfitMultiple);

            if (Verbose)
                algorithm.Log($"[signal] {algorithm.Time:u} ENTER px={price:F2} atr={atr:F2} " +
                              $"weight={targetFraction:F4} stop={state.StopPrice:F2} target={state.TargetPrice:F2} " +
                              $"held={algorithm.Portfolio[symbol].Quantity}");

            yield return Insight.Price(symbol, _insightPeriod, InsightDirection.Up,
                magnitude: null,
                confidence: null,
                sourceModel: Name,
                weight: (double)targetFraction,
                tag: $"ema-cross-up stop={state.StopPrice:F2} target={state.TargetPrice:F2}");
        }
    }

    public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
    {
        foreach (var security in changes.AddedSecurities)
        {
            if (_state.ContainsKey(security.Symbol)) continue;
            _state[security.Symbol] = new SymbolState(
                new ExponentialMovingAverage(_parameters.FastPeriod),
                new ExponentialMovingAverage(_parameters.SlowPeriod),
                new AverageTrueRange(_parameters.AtrPeriod));
        }

        foreach (var security in changes.RemovedSecurities)
            _state.Remove(security.Symbol);
    }

    /// <summary>Longest lookback the model needs before its first real decision.</summary>
    public int WarmUpBars => Math.Max(_parameters.SlowPeriod, _parameters.AtrPeriod) + 1;

    /// <inheritdoc />
    public bool TryGetProtectiveLevels(Symbol symbol, out decimal stopPrice, out decimal targetPrice)
    {
        if (_state.TryGetValue(symbol, out var state) && state.IsLong)
        {
            stopPrice = state.StopPrice;
            targetPrice = state.TargetPrice;
            return true;
        }

        stopPrice = 0m;
        targetPrice = 0m;
        return false;
    }

    private sealed class SymbolState(
        ExponentialMovingAverage fast,
        ExponentialMovingAverage slow,
        AverageTrueRange atr)
    {
        public ExponentialMovingAverage Fast { get; } = fast;
        public ExponentialMovingAverage Slow { get; } = slow;
        public AverageTrueRange Atr { get; } = atr;

        public bool IsLong { get; private set; }
        public decimal StopPrice { get; private set; }
        public decimal TargetPrice { get; private set; }

        /// <summary>Weight fixed at entry and re-asserted while the position is held.</summary>
        public decimal EntryWeight { get; private set; }

        public bool IsReady => Fast.IsReady && Slow.IsReady && Atr.IsReady;

        public void Open(decimal entryWeight, decimal stop, decimal target)
        {
            IsLong = true;
            EntryWeight = entryWeight;
            StopPrice = stop;
            TargetPrice = target;
        }

        public void Close()
        {
            IsLong = false;
            EntryWeight = 0m;
            StopPrice = 0m;
            TargetPrice = 0m;
        }

        public void Update(TradeBar bar)
        {
            Fast.Update(bar.EndTime, bar.Close);
            Slow.Update(bar.EndTime, bar.Close);
            Atr.Update(bar);
        }

        public Dictionary<string, decimal> Snapshot() => new()
        {
            ["emaFast"] = Fast.Current.Value,
            ["emaSlow"] = Slow.Current.Value,
            ["atr"] = Atr.Current.Value,
        };
    }
}
