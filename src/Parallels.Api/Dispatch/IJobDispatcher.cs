using Parallels.Contracts;

namespace Parallels.Api.Dispatch;

/// <summary>Outcome of running one backtest job to completion.</summary>
public sealed record DispatchOutcome(int ExitCode, string Stdout, string Stderr, string Mechanism, string? WorkerIdentity);

/// <summary>
/// Runs one backtest job in a fresh OS process and waits for it to finish.
///
/// The two implementations are the same shape at different boundaries: a
/// long-lived dispatcher handing each job to a brand-new child process. With
/// Docker that is <c>docker exec</c> into an already-running container; without
/// it, a plain child process. Either way the constraint that matters is
/// satisfied — LEAN's <c>Composer</c>/<c>Config</c> process-wide state is never
/// shared between two jobs (spec 3.5).
///
/// What is explicitly <em>not</em> allowed is <c>docker run</c> per job: that
/// pays container-creation cost on every single backtest for a guarantee the
/// warm-container pattern already provides.
/// </summary>
public interface IJobDispatcher
{
    /// <summary>How work is actually being dispatched, for the run log and the UI.</summary>
    string Mechanism { get; }

    /// <summary>True when this dispatcher can currently accept work.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<DispatchOutcome> RunAsync(BacktestJob job, string resultsRoot, CancellationToken ct = default);
}
