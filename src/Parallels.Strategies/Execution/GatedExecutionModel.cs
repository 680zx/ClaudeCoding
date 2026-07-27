using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Orders;

namespace Parallels.Strategies.Execution;

/// <summary>
/// Wraps LEAN's own execution model and runs every portfolio target through the
/// gate on the way past.
///
/// Spec 3.2 is explicit that the gateway sits in front of order placement rather
/// than inside a custom portfolio construction model — so portfolio construction
/// stays LEAN's built-in implementation, and this decorator is the single choke
/// point where targets become orders.
/// </summary>
public sealed class GatedExecutionModel(IExecutionModel inner, IOrderGate gate, Action<string>? log = null)
    : IExecutionModel
{
    private readonly IExecutionModel _inner = inner;
    private readonly IOrderGate _gate = gate;
    private readonly Action<string>? _log = log;

    public void Execute(QCAlgorithm algorithm, IPortfolioTarget[] targets)
    {
        if (targets.Length == 0)
        {
            _inner.Execute(algorithm, targets);
            return;
        }

        var approved = new List<IPortfolioTarget>(targets.Length);

        foreach (var target in targets)
        {
            GateDecision decision;
            try
            {
                decision = _gate.Evaluate(algorithm, target);
            }
            catch (Exception ex)
            {
                // A gate that cannot decide must not be treated as approval.
                _log?.Invoke($"[gate] {target.Symbol.Value}: evaluation failed, dropping target: {ex.Message}");
                continue;
            }

            if (decision.Reason is not null)
                _log?.Invoke($"[gate] {target.Symbol.Value}: requested {target.Quantity}, " +
                             $"{(decision.Approved ? $"approved {decision.ApprovedQuantity}" : "refused")} " +
                             $"({decision.Reason})");

            if (!decision.Approved) continue;

            approved.Add(decision.ApprovedQuantity == target.Quantity
                ? target
                : new PortfolioTarget(target.Symbol, decision.ApprovedQuantity, target.Tag));
        }

        if (approved.Count > 0)
            _inner.Execute(algorithm, [.. approved]);
    }

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent)
    {
        _gate.OnOrderEvent(algorithm, orderEvent);
        _inner.OnOrderEvent(algorithm, orderEvent);
    }

    public void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes) =>
        _inner.OnSecuritiesChanged(algorithm, changes);
}
