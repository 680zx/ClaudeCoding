using Parallels.Contracts;

namespace Parallels.Api.Services;

public sealed record LiveSessionState(
    bool Running,
    string? Identity,
    string Mechanism,
    int EnabledAlphaCount,
    string Environment,
    DateTimeOffset? LastAppliedUtc,
    string? Detail);

/// <summary>
/// Turns "the desired state changed" into "the live worker is running that
/// state".
///
/// Every create, edit, delete and toggle funnels through
/// <see cref="ApplyDesiredStateAsync"/>: it recomputes the enabled set and
/// restarts the worker, or stops it entirely when nothing is enabled. Full
/// reload rather than hot-swap, for the LEAN reason in spec 3.2.
/// </summary>
public sealed class LiveSessionManager(
    IAlphaConfigStore alphas,
    ILiveWorkerHost host,
    ILogger<LiveSessionManager> logger)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private DateTimeOffset? _lastApplied;

    public async Task<LiveSessionState> GetStateAsync(CancellationToken ct = default)
    {
        var status = await host.GetStatusAsync(ct);
        var enabled = (await alphas.ListAsync(ct)).Count(a => a.Enabled);

        return new LiveSessionState(
            status.Running,
            status.Identity,
            status.Mechanism,
            enabled,
            CurrentEnvironment(),
            _lastApplied,
            status.Detail);
    }

    /// <summary>
    /// Restarts the live worker with the currently enabled alphas, or stops it
    /// when none remain.
    /// </summary>
    public async Task<LiveSessionState> ApplyDesiredStateAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var enabled = (await alphas.ListAsync(ct)).Where(a => a.Enabled).ToList();

            if (enabled.Count == 0)
            {
                logger.LogInformation("No alphas enabled — stopping the live worker.");
                await host.StopAsync(ct);
                _lastApplied = DateTimeOffset.UtcNow;
                return await GetStateAsync(ct);
            }

            var environment = CurrentEnvironment();

            var config = new LiveSessionConfig
            {
                SessionId = $"live-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}",
                Alphas = enabled,
                Environment = environment,
                // Consent to trade real money comes from the operator's
                // environment, never from stored desired state — so enabling an
                // alpha can never by itself promote a session to mainnet.
                ConfirmMainnet = string.Equals(
                    System.Environment.GetEnvironmentVariable("PARALLELS_CONFIRM_MAINNET"),
                    "true", StringComparison.OrdinalIgnoreCase),
            };

            await host.StartAsync(config, ct);
            _lastApplied = DateTimeOffset.UtcNow;

            return await GetStateAsync(ct);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string CurrentEnvironment() =>
        System.Environment.GetEnvironmentVariable("BINANCE_ENVIRONMENT") ?? "testnet";
}
