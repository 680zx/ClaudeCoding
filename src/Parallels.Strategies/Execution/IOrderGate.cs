using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Orders;

namespace Parallels.Strategies.Execution;

/// <summary>
/// The gate's verdict on one proposed portfolio target.
///
/// Approval and quantity are tracked separately, and deliberately so. A target
/// quantity of zero is a perfectly valid instruction — it means "close this
/// position" — so it must not be confused with a refusal. Inferring rejection
/// from a zero quantity silently swallows every exit a strategy ever tries to
/// make, leaving positions open forever; the resulting report shows no losing
/// trades, because a trade that never closes never books a loss.
/// </summary>
/// <param name="Approved">False only when the gate is genuinely refusing the target.</param>
/// <param name="ApprovedQuantity">
/// The absolute position quantity allowed through. A gate may approve a smaller
/// size than requested rather than refusing outright — that partial approval is
/// what shared-exposure throttling looks like from an alpha's side.
/// </param>
/// <param name="Reason">Why the target was reduced or refused; logged, and null when untouched.</param>
public readonly record struct GateDecision(bool Approved, decimal ApprovedQuantity, string? Reason)
{
    public static GateDecision Approve(decimal quantity) => new(true, quantity, null);

    public static GateDecision Reduce(decimal quantity, string reason) => new(true, quantity, reason);

    public static GateDecision Reject(string reason) => new(false, 0m, reason);
}

/// <summary>
/// Sits between every alpha and the exchange.
///
/// Risk controls are independent of alpha logic by construction (spec 5): alphas
/// only ever emit insights, the framework turns insights into portfolio targets,
/// and every target passes through here before an order exists. There is no code
/// path from an alpha to order placement that skips this.
/// </summary>
public interface IOrderGate
{
    /// <summary>
    /// Check-then-reserve for one target. Implementations that track shared
    /// budget must make this atomic — two alphas evaluating concurrently must
    /// not both be told the full remaining budget is theirs.
    /// </summary>
    GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target);

    /// <summary>Releases or settles whatever <see cref="Evaluate"/> reserved.</summary>
    void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent);
}

/// <summary>Approves every target unchanged. Used where no gating applies at all.</summary>
public sealed class NullOrderGate : IOrderGate
{
    public static NullOrderGate Instance { get; } = new();

    public GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target) =>
        GateDecision.Approve(target.Quantity);

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent) { }
}

/// <summary>
/// Runs gates in order, feeding each one the quantity the previous approved.
///
/// Order matters: exchange-filter rounding runs last so that whatever the risk
/// limits allow is still expressed as a quantity the exchange will accept.
/// </summary>
public sealed class CompositeOrderGate(params IOrderGate[] gates) : IOrderGate
{
    private readonly IOrderGate[] _gates = gates;

    public GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target)
    {
        var current = target;
        string? reason = null;

        foreach (var gate in _gates)
        {
            var decision = gate.Evaluate(algorithm, current);

            if (decision.Reason is not null)
                reason = reason is null ? decision.Reason : $"{reason}; {decision.Reason}";

            // Only an actual refusal short-circuits. An approved quantity of zero
            // is a flatten instruction and must keep flowing to the next gate.
            if (!decision.Approved) return new GateDecision(false, 0m, reason);

            current = new PortfolioTarget(target.Symbol, decision.ApprovedQuantity, target.Tag);
        }

        return new GateDecision(true, current.Quantity, reason);
    }

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent)
    {
        foreach (var gate in _gates) gate.OnOrderEvent(algorithm, orderEvent);
    }
}
