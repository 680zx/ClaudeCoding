using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;

namespace Parallels.Strategies.Alphas;

/// <summary>
/// Wraps one alpha so an unhandled exception inside it disables that alpha
/// instead of taking down the session.
///
/// Hosting N alphas in one process is the right shape for live (spec 3.2) but it
/// has one genuine cost: a single alpha's bug can threaten every other alpha
/// sharing the process. This wrapper mitigates that — it does not eliminate it.
/// An alpha that corrupts shared state or exhausts memory can still take the
/// process with it; what this catches is the ordinary case of a model throwing
/// out of <c>Update</c>.
///
/// Once disabled, the alpha stays disabled for the rest of the session. Retrying
/// a model that just threw tends to produce the same exception on every bar,
/// which buries the real failure in log noise.
/// </summary>
public sealed class FaultIsolatingAlphaModel(
    IAlphaModel inner,
    string alphaId,
    Action<string, Exception>? onFault = null,
    Action<string>? log = null) : IAlphaModel, INamedModel
{
    private readonly IAlphaModel _inner = inner;
    private readonly Action<string, Exception>? _onFault = onFault;
    private readonly Action<string>? _log = log;

    public string AlphaId { get; } = alphaId;

    public string Name => (_inner as INamedModel)?.Name ?? AlphaId;

    /// <summary>False once this alpha has thrown; it emits nothing from then on.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>The exception that disabled this alpha, if any.</summary>
    public Exception? FaultedWith { get; private set; }

    /// <summary>
    /// Insights this alpha has emitted. Reported at end of run: comparing it to
    /// the order count is the quickest way to tell a strategy's own decisions
    /// apart from portfolio-level re-sizing on top of them.
    /// </summary>
    public long EmittedInsightCount { get; private set; }

    public IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
    {
        if (!IsEnabled) return [];

        try
        {
            // Enumerated eagerly on purpose: the inner model is an iterator, so
            // deferring would move its exceptions outside this try and into
            // LEAN's own enumeration, which is exactly what must not happen.
            var insights = _inner.Update(algorithm, data)?.ToList() ?? [];
            EmittedInsightCount += insights.Count;
            return insights;
        }
        catch (Exception ex)
        {
            Disable(ex, nameof(Update));
            return [];
        }
    }

    public void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
    {
        if (!IsEnabled) return;

        try
        {
            (_inner as INotifiedSecurityChanges)?.OnSecuritiesChanged(algorithm, changes);
        }
        catch (Exception ex)
        {
            Disable(ex, nameof(OnSecuritiesChanged));
        }
    }

    private void Disable(Exception ex, string origin)
    {
        IsEnabled = false;
        FaultedWith = ex;

        var message = $"[alpha-fault] '{AlphaId}' threw in {origin} and has been disabled for the " +
                      $"rest of this session. Other alphas keep running. {ex.GetType().Name}: {ex.Message}";
        _log?.Invoke(message);
        _onFault?.Invoke(AlphaId, ex);
    }
}
