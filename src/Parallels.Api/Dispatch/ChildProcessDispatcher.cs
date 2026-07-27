using Parallels.Contracts;

namespace Parallels.Api.Dispatch;

public sealed record ChildProcessDispatchOptions
{
    /// <summary>Path to the built worker DLL on this host.</summary>
    public required string WorkerDllPath { get; init; }

    public required string DataFolder { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// The honest fallback when no Docker daemon is available (spec 3.2).
///
/// It is the same shape as the Docker path with the container boundary removed:
/// a long-lived dispatcher spawning a fresh child process per job. The property
/// that actually matters — one LEAN engine session per OS process — holds
/// identically. What it does not provide is the isolation and image pinning the
/// container gives, so it is a development and proof convenience, not the
/// deployment target.
/// </summary>
public sealed class ChildProcessDispatcher(ChildProcessDispatchOptions options, ILogger<ChildProcessDispatcher> logger)
    : IJobDispatcher
{
    public string Mechanism => "child process (no Docker daemon available)";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(File.Exists(options.WorkerDllPath));

    public async Task<DispatchOutcome> RunAsync(BacktestJob job, string resultsRoot, CancellationToken ct = default)
    {
        if (!File.Exists(options.WorkerDllPath))
            throw new FileNotFoundException($"Worker not found at {options.WorkerDllPath}. Build the solution first.");

        var json = ParallelsJson.Serialize(job);

        var result = await ProcessRunner.RunAsync(
            "dotnet",
            [
                options.WorkerDllPath,
                "--job-stdin",
                "--data-folder", options.DataFolder,
                "--results", resultsRoot,
            ],
            stdin: json,
            timeout: options.Timeout,
            ct);

        var identity = DockerExecDispatcher.ExtractWorkerIdentity(result.Stdout);

        logger.LogInformation("Job {JobId} finished with exit code {ExitCode} via child process ({Identity}).",
            job.JobId, result.ExitCode, identity);

        return new DispatchOutcome(result.ExitCode, result.Stdout, result.Stderr, Mechanism, identity);
    }
}
