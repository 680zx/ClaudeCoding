using Parallels.Strategies.Execution;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Orders;

namespace Parallels.Worker.Live.Binance.Risk;

/// <summary>
/// A single order-rate budget shared by every alpha in this process.
///
/// The budget belongs to the API key, not to a strategy: Binance counts requests
/// per account, so N alphas each politely staying under the limit individually
/// will still get the account rate-limited together. Because they now share one
/// process (spec 3.2), enforcing this needs nothing more than a lock and a
/// timestamp window.
///
/// Refusing here is the right behaviour rather than queueing: a trading signal
/// that has to wait out a rate-limit window is usually stale by the time it
/// would be sent, and the next bar will re-derive a fresh view anyway.
/// </summary>
public sealed class RateLimitGate(int maxOrdersPerWindow, TimeSpan window, Action<string>? log = null)
    : IOrderGate
{
    private readonly Lock _gate = new();
    private readonly Queue<DateTime> _recent = new();

    public GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target)
    {
        // A no-op target consumes no budget: nothing will be sent.
        if (target.Quantity == algorithm.Portfolio[target.Symbol].Quantity)
            return GateDecision.Approve(target.Quantity);

        var now = algorithm.UtcTime;

        lock (_gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > window) _recent.Dequeue();

            if (_recent.Count >= maxOrdersPerWindow)
            {
                var reason =
                    $"order rate budget exhausted: {_recent.Count} orders in the last " +
                    $"{window.TotalSeconds:F0}s across all alphas (limit {maxOrdersPerWindow})";
                log?.Invoke($"[rate] {reason}");
                return GateDecision.Reject(reason);
            }

            _recent.Enqueue(now);
            return GateDecision.Approve(target.Quantity);
        }
    }

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent) { }

    /// <summary>Orders counted in the current window, for logging and tests.</summary>
    public int UsedInWindow(DateTime now)
    {
        lock (_gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > window) _recent.Dequeue();
            return _recent.Count;
        }
    }
}
