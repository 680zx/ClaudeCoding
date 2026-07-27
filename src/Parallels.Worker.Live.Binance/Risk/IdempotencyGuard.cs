using Parallels.Strategies.Execution;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Orders;

namespace Parallels.Worker.Live.Binance.Risk;

/// <summary>
/// Refuses a target that repeats one already in flight.
///
/// The failure this exists to prevent is duplicate execution: the same intent
/// submitted twice because a bar was reprocessed, a reconnect replayed a slice,
/// or a retry fired after the original had in fact been accepted. In a backtest
/// that produces a slightly wrong number; live it doubles a real position.
///
/// The key is (symbol, target quantity) rather than a client order id, because
/// the duplicate being guarded against is generated independently upstream — the
/// second submission would carry its own fresh id, and a "have I sent this id"
/// check would wave it straight through.
///
/// A key is released when the resulting order reaches a terminal state, so a
/// genuine later re-entry at the same size is never blocked.
/// </summary>
public sealed class IdempotencyGuard(TimeSpan window, Action<string>? log = null) : IOrderGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTime> _inFlight = [];

    public GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target)
    {
        var holdings = algorithm.Portfolio[target.Symbol].Quantity;
        if (target.Quantity == holdings) return GateDecision.Approve(target.Quantity);

        var key = KeyFor(target.Symbol.Value, target.Quantity);
        var now = algorithm.UtcTime;

        lock (_gate)
        {
            foreach (var stale in _inFlight.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList())
                _inFlight.Remove(stale);

            if (_inFlight.TryGetValue(key, out var sentAt))
            {
                var reason =
                    $"duplicate submission for {target.Symbol.Value} target {target.Quantity} — " +
                    $"an identical order sent {(now - sentAt).TotalSeconds:F1}s ago is still in flight";
                log?.Invoke($"[idempotency] refused: {reason}");
                return GateDecision.Reject(reason);
            }

            _inFlight[key] = now;
            return GateDecision.Approve(target.Quantity);
        }
    }

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent)
    {
        if (orderEvent.Status is not (OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Invalid))
            return;

        // The order is settled one way or another, so the same intent is
        // legitimately allowed again.
        lock (_gate)
        {
            foreach (var key in _inFlight.Keys
                         .Where(k => k.StartsWith(orderEvent.Symbol.Value + "|", StringComparison.Ordinal))
                         .ToList())
            {
                _inFlight.Remove(key);
            }
        }
    }

    /// <summary>Number of intents currently considered in flight; used by tests.</summary>
    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }

    private static string KeyFor(string ticker, decimal quantity) => $"{ticker}|{quantity}";
}
