using Parallels.Strategies.Execution;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Orders;

namespace Parallels.Worker.Live.Binance.Risk;

/// <summary>
/// Enforces per-alpha and aggregate exposure limits across every alpha sharing
/// this process.
///
/// Plain in-memory state, deliberately. The requirement — atomic
/// check-then-reserve against a shared budget — is unchanged from when live was
/// process-per-strategy, but with N alphas now in one process the implementation
/// no longer needs cross-process SQLite coordination (spec 3.2). Persistence is
/// not needed either: reconciliation on restart re-derives real exposure from the
/// exchange, which is ground truth; this is only the fast path in between.
///
/// <para><b>Attribution is by symbol.</b> A portfolio target arrives at the
/// execution layer after the portfolio construction model has already merged
/// every active insight for that symbol, so when two alphas trade the same
/// symbol their individual contributions are genuinely not recoverable here —
/// the framework combined them upstream. Each symbol is therefore attributed to
/// one alpha, and two alphas sharing a symbol are treated as one exposure
/// against the first alpha configured for it. Per-alpha limits are exact when
/// alphas trade distinct symbols, which is the configuration this is built
/// for.</para>
/// </summary>
public sealed class RiskGateway : IOrderGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, decimal> _committedByAlpha = [];
    private readonly Dictionary<string, string> _symbolToAlpha;
    private readonly IReadOnlyDictionary<string, decimal> _perAlphaLimits;
    private readonly decimal _maxAggregateFraction;
    private readonly Action<string>? _log;

    public RiskGateway(
        IReadOnlyDictionary<string, string> symbolToAlpha,
        IReadOnlyDictionary<string, decimal> perAlphaLimits,
        decimal maxAggregateFraction,
        Action<string>? log = null)
    {
        _symbolToAlpha = new Dictionary<string, string>(symbolToAlpha, StringComparer.OrdinalIgnoreCase);
        _perAlphaLimits = perAlphaLimits;
        _maxAggregateFraction = maxAggregateFraction;
        _log = log;
    }

    /// <summary>Notional currently committed by each alpha, in account currency.</summary>
    public IReadOnlyDictionary<string, decimal> CommittedByAlpha
    {
        get { lock (_gate) return new Dictionary<string, decimal>(_committedByAlpha); }
    }

    public GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target)
    {
        var ticker = target.Symbol.Value;
        if (!_symbolToAlpha.TryGetValue(ticker, out var alphaId))
            return GateDecision.Approve(target.Quantity);

        var security = algorithm.Securities[target.Symbol];
        var price = security.Price;
        var equity = algorithm.Portfolio.TotalPortfolioValue;
        if (price <= 0m || equity <= 0m) return GateDecision.Approve(target.Quantity);

        var holdings = algorithm.Portfolio[target.Symbol].Quantity;

        // Reducing or closing never needs budget — it frees it. Gating an exit on
        // an exposure limit would make a breach unrecoverable.
        if (Math.Abs(target.Quantity) <= Math.Abs(holdings))
            return GateDecision.Approve(target.Quantity);

        var requestedNotional = Math.Abs(target.Quantity) * price;

        // Check and reserve happen under one lock. Two alphas evaluating
        // concurrently must not both be told the whole remaining budget is
        // theirs — which is exactly what a read followed by a separate write
        // would allow.
        lock (_gate)
        {
            var perAlphaCeiling = (_perAlphaLimits.TryGetValue(alphaId, out var fraction) ? fraction : 1m) * equity;
            var aggregateCeiling = _maxAggregateFraction * equity;

            var committedByOthers = _committedByAlpha
                .Where(kv => kv.Key != alphaId)
                .Sum(kv => kv.Value);

            var allowed = Math.Min(perAlphaCeiling, aggregateCeiling - committedByOthers);

            if (allowed <= 0m)
            {
                var reason =
                    $"'{alphaId}' refused: other alphas already commit {committedByOthers:F2} of the " +
                    $"{aggregateCeiling:F2} aggregate limit ({_maxAggregateFraction:P0} of equity)";
                _log?.Invoke($"[risk] {reason}");
                return GateDecision.Reject(reason);
            }

            if (requestedNotional <= allowed)
            {
                _committedByAlpha[alphaId] = requestedNotional;
                return GateDecision.Approve(target.Quantity);
            }

            var scaled = target.Quantity * (allowed / requestedNotional);
            _committedByAlpha[alphaId] = allowed;

            var limitHit = (aggregateCeiling - committedByOthers) < perAlphaCeiling ? "aggregate" : "per-alpha";
            var message =
                $"'{alphaId}' reduced from {requestedNotional:F2} to {allowed:F2} on the {limitHit} limit " +
                $"(other alphas commit {committedByOthers:F2} of {aggregateCeiling:F2})";

            _log?.Invoke($"[risk] {message}");
            return GateDecision.Reduce(scaled, message);
        }
    }

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent)
    {
        if (orderEvent.Status is not (OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Invalid))
            return;

        if (!_symbolToAlpha.TryGetValue(orderEvent.Symbol.Value, out var alphaId)) return;

        // Re-derive from actual holdings rather than decrementing a counter. A
        // counter drifts the first time a fill is partial, rejected, or arrives
        // out of order; holdings are what the account really has.
        var holding = algorithm.Portfolio[orderEvent.Symbol];
        var notional = Math.Abs(holding.Quantity) * holding.Price;

        lock (_gate)
        {
            if (notional == 0m) _committedByAlpha.Remove(alphaId);
            else _committedByAlpha[alphaId] = notional;
        }
    }

    /// <summary>
    /// Recomputes committed exposure from the account's real holdings.
    ///
    /// Called after reconciliation on startup so the in-memory view begins from
    /// the exchange's truth rather than from zero — otherwise a restart would
    /// believe the whole budget is free while positions are still open.
    /// </summary>
    public void ResetFromPortfolio(QCAlgorithm algorithm)
    {
        lock (_gate)
        {
            _committedByAlpha.Clear();

            foreach (var holding in algorithm.Portfolio.Values)
            {
                if (holding.Quantity == 0m) continue;
                if (!_symbolToAlpha.TryGetValue(holding.Symbol.Value, out var alphaId)) continue;

                var notional = Math.Abs(holding.Quantity) * holding.Price;
                _committedByAlpha[alphaId] = _committedByAlpha.GetValueOrDefault(alphaId) + notional;
            }
        }
    }
}
