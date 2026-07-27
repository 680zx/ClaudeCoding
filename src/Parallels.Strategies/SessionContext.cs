using Parallels.Contracts;
using Parallels.Strategies.Execution;

namespace Parallels.Strategies;

public enum SessionMode
{
    Backtest,
    Live
}

/// <summary>
/// Everything <see cref="ParallelsAlgorithm"/> needs that LEAN cannot hand it.
/// </summary>
public sealed class ParallelsSession
{
    public required SessionMode Mode { get; init; }

    /// <summary>Set in <see cref="SessionMode.Backtest"/>. Exactly one strategy configuration.</summary>
    public BacktestJob? Job { get; init; }

    /// <summary>Set in <see cref="SessionMode.Live"/>. One broker account's worth of enabled alphas.</summary>
    public LiveSessionConfig? Live { get; init; }

    /// <summary>Directory the run writes chart-data.json and its result into.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Log every entry/exit decision with the levels behind it. Diagnostic; noisy on long runs.</summary>
    public bool VerboseSignals { get; init; }

    /// <summary>
    /// Sits in front of order placement. In backtest this is exchange-filter
    /// rounding only; live additionally supplies the aggregate risk and rate
    /// budget (spec 3.2). No alpha can bypass it, because alphas emit insights
    /// and never place orders themselves.
    /// </summary>
    public IOrderGate OrderGate { get; init; } = NullOrderGate.Instance;

    /// <summary>
    /// The uniform view <see cref="ParallelsAlgorithm"/> actually iterates: one
    /// entry for a backtest, N for a live session (spec 3.3).
    /// </summary>
    public IReadOnlyList<AlphaConfig> Alphas
    {
        get
        {
            if (Mode == SessionMode.Live)
                return Live?.Alphas ?? [];

            if (Job is null) return [];
            return
            [
                new AlphaConfig
                {
                    Id = Job.JobId,
                    Name = Job.Name ?? Job.Parameters.StrategyType,
                    Symbols = [Job.Symbol],
                    Market = Job.Market,
                    Resolution = Job.Resolution,
                    Parameters = Job.Parameters,
                    Enabled = true,
                    // A backtest is a single strategy against the whole account,
                    // so it is not competing with anything for exposure.
                    MaxExposureFraction = 1m,
                }
            ];
        }
    }
}

/// <summary>
/// The static hand-off to the algorithm instance.
///
/// This exists because of a hard LEAN constraint, not as a convenience:
/// <c>QCAlgorithm</c> is constructed by reflection inside LEAN's setup handler
/// with no constructor injection available (spec 2), so a process-wide static is
/// the only channel for handing an algorithm its configuration.
///
/// <see cref="Publish"/> deliberately throws on a second call. One process is
/// one LEAN engine session — <c>Composer</c>, <c>Config</c> and the handler
/// singletons are all process-wide, so a second publish would mean a second
/// session in a process that cannot support one. Failing loudly here turns that
/// into an immediate, obvious error instead of a subtly wrong run.
/// </summary>
public static class SessionContext
{
    private static ParallelsSession? _session;
    private static int _published;

    public static void Publish(ParallelsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Interlocked.Exchange(ref _published, 1) == 1)
        {
            throw new InvalidOperationException(
                "SessionContext.Publish was called twice in one process. One process = one LEAN " +
                "engine session; LEAN's Composer, Config and handler singletons are process-wide " +
                "and cannot be reused for a second job. Start a new process instead.");
        }

        _session = session;
    }

    public static ParallelsSession Current =>
        _session ?? throw new InvalidOperationException(
            "SessionContext.Publish has not been called. The worker must publish the session " +
            "before LEAN constructs the algorithm.");

    /// <summary>True once a session has been published, without throwing.</summary>
    public static bool IsPublished => Volatile.Read(ref _published) == 1;

    /// <summary>
    /// Test-only escape hatch. Never called by either worker: a real session
    /// deliberately cannot be reset, which is the whole point of the guard.
    /// </summary>
    internal static void ResetForTests()
    {
        _session = null;
        Interlocked.Exchange(ref _published, 0);
    }
}
