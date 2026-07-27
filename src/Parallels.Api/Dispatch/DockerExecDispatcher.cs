using Parallels.Contracts;

namespace Parallels.Api.Dispatch;

public sealed record DockerDispatchOptions
{
    /// <summary>Name of the already-running, warm backtest container.</summary>
    public string ContainerName { get; init; } = "parallels-worker-backtest";

    /// <summary>Path to the worker DLL inside the container.</summary>
    public string WorkerDllPath { get; init; } = "/app/Parallels.Worker.Backtest.dll";

    /// <summary>Data and results paths as the container sees them.</summary>
    public string ContainerDataFolder { get; init; } = "/data";
    public string ContainerResultsPath { get; init; } = "/results";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Dispatches a job with <c>docker exec</c> into a container that is already up.
///
/// The container is started once and stays warm; only the process inside it is
/// per-job (spec 3.5). <c>docker exec</c> starts a genuinely new process in the
/// existing container's namespace every time, so each job gets its own
/// <c>Composer</c>/<c>Config</c> while image pull and container creation are paid
/// once rather than per backtest.
/// </summary>
public sealed class DockerExecDispatcher(DockerDispatchOptions options, ILogger<DockerExecDispatcher> logger)
    : IJobDispatcher
{
    public string Mechanism => $"docker exec into container '{options.ContainerName}'";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            // Both checks matter: a reachable daemon is not enough if the warm
            // container is not actually up to exec into.
            var result = await ProcessRunner.RunAsync(
                "docker",
                ["inspect", "-f", "{{.State.Running}}", options.ContainerName],
                stdin: null,
                timeout: TimeSpan.FromSeconds(10),
                ct);

            return result.ExitCode == 0 &&
                   result.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Docker availability check failed.");
            return false;
        }
    }

    public async Task<DispatchOutcome> RunAsync(BacktestJob job, string resultsRoot, CancellationToken ct = default)
    {
        var json = ParallelsJson.Serialize(job);

        var result = await ProcessRunner.RunAsync(
            "docker",
            [
                "exec", "-i", options.ContainerName,
                "dotnet", options.WorkerDllPath,
                "--job-stdin",
                "--data-folder", options.ContainerDataFolder,
                "--results", options.ContainerResultsPath,
            ],
            stdin: json,
            timeout: options.Timeout,
            ct);

        // The worker prints its pid; recording it is what makes "two jobs, one
        // container, two different processes" checkable after the fact rather
        // than merely asserted.
        var identity = ExtractWorkerIdentity(result.Stdout);

        logger.LogInformation("Job {JobId} finished with exit code {ExitCode} via docker exec ({Identity}).",
            job.JobId, result.ExitCode, identity);

        return new DispatchOutcome(result.ExitCode, result.Stdout, result.Stderr, Mechanism, identity);
    }

    internal static string? ExtractWorkerIdentity(string stdout)
    {
        var line = stdout.Split('\n').FirstOrDefault(l => l.Contains("[worker] pid ", StringComparison.Ordinal));
        return line?.Trim();
    }
}
