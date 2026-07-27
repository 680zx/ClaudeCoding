using System.Diagnostics;
using Parallels.Api.Dispatch;
using Parallels.Contracts;

namespace Parallels.Api.Services;

public sealed record LiveWorkerStatus(bool Running, string? Identity, string Mechanism, string? Detail);

/// <summary>
/// Owns the lifetime of the live worker for one broker account.
///
/// Live restarts on every desired-state change rather than hot-swapping alphas,
/// because LEAN does not reliably support adding an <c>AlphaModel</c> after
/// <c>Initialize()</c> has run (spec 3.2). That is affordable precisely because
/// it is rare — an operator editing a tile, not a job dispatched many times a
/// minute — and reconciliation on restart is what makes it safe.
/// </summary>
public interface ILiveWorkerHost
{
    string Mechanism { get; }
    Task<LiveWorkerStatus> GetStatusAsync(CancellationToken ct = default);
    Task StartAsync(LiveSessionConfig config, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed record LiveHostOptions
{
    public string ContainerName { get; init; } = "parallels-worker-live-binance";
    public string ImageName { get; init; } = "parallels/worker-live-binance:latest";
    public required string WorkerDllPath { get; init; }
    public required string DataFolder { get; init; }
    public required string ResultsRoot { get; init; }

    /// <summary>Credentials are read from the API's own environment and passed through; never persisted to state.</summary>
    public string[] PassthroughEnvVars { get; init; } =
        ["BINANCE_API_KEY", "BINANCE_API_SECRET", "BINANCE_ENVIRONMENT"];
}

/// <summary>
/// Runs the live worker as a container, configured by an environment variable at
/// launch (spec 3.5).
///
/// The desired state travels in the launch environment rather than through a
/// file the API writes and the container is assumed to be able to read — that
/// assumption only holds when both share a filesystem, which is true on one host
/// and not in general.
/// </summary>
public sealed class DockerLiveWorkerHost(LiveHostOptions options, ILogger<DockerLiveWorkerHost> logger)
    : ILiveWorkerHost
{
    public string Mechanism => $"docker container '{options.ContainerName}'";

    public async Task<LiveWorkerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("docker",
                ["inspect", "-f", "{{.State.Running}}|{{.Id}}", options.ContainerName],
                stdin: null, timeout: TimeSpan.FromSeconds(10), ct);

            if (result.ExitCode != 0) return new LiveWorkerStatus(false, null, Mechanism, "container does not exist");

            var parts = result.Stdout.Trim().Split('|');
            var running = parts[0].Equals("true", StringComparison.OrdinalIgnoreCase);
            return new LiveWorkerStatus(running, parts.ElementAtOrDefault(1), Mechanism, null);
        }
        catch (Exception ex)
        {
            return new LiveWorkerStatus(false, null, Mechanism, ex.Message);
        }
    }

    public async Task StartAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        await StopAsync(ct);

        var arguments = new List<string>
        {
            "run", "-d", "--name", options.ContainerName,
            "-e", $"{ParallelsJson.LiveConfigEnvVar}={ParallelsJson.Serialize(config)}",
            "-v", $"{options.DataFolder}:/data:ro",
            "-v", $"{options.ResultsRoot}:/results",
        };

        foreach (var name in options.PassthroughEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) arguments.AddRange(["-e", $"{name}={value}"]);
        }

        arguments.Add(options.ImageName);

        var result = await ProcessRunner.RunAsync("docker", arguments, null, TimeSpan.FromMinutes(2), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker run failed: {result.Stderr.Trim()}");

        logger.LogInformation("Live worker started with {Count} enabled alphas.", config.Alphas.Count);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await ProcessRunner.RunAsync("docker", ["rm", "-f", options.ContainerName],
            null, TimeSpan.FromSeconds(30), ct);
    }
}

/// <summary>
/// The no-Docker fallback: the live worker as a child process of the API, with
/// the same environment-variable hand-off.
///
/// Same honesty caveat as the backtest fallback — this proves the configuration
/// path and the worker's own startup behaviour, not container isolation.
/// </summary>
public sealed class ChildProcessLiveWorkerHost(LiveHostOptions options, ILogger<ChildProcessLiveWorkerHost> logger)
    : ILiveWorkerHost
{
    private Process? _process;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public string Mechanism => "child process (no Docker daemon available)";

    public Task<LiveWorkerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var process = _process;
        var running = process is { HasExited: false };
        return Task.FromResult(new LiveWorkerStatus(
            running,
            running ? $"pid {process!.Id}" : null,
            Mechanism,
            process is { HasExited: true } ? $"exited with code {process.ExitCode}" : null));
    }

    public async Task StartAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            await StopCoreAsync();

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(options.WorkerDllPath);
            startInfo.ArgumentList.Add("--data-folder");
            startInfo.ArgumentList.Add(options.DataFolder);
            startInfo.ArgumentList.Add("--results");
            startInfo.ArgumentList.Add(options.ResultsRoot);

            startInfo.Environment[ParallelsJson.LiveConfigEnvVar] = ParallelsJson.Serialize(config);

            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogInformation("[live] {Line}", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogWarning("[live] {Line}", e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            logger.LogInformation("Live worker started as pid {Pid} with {Count} enabled alphas.",
                process.Id, config.Alphas.Count);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try { await StopCoreAsync(); }
        finally { _mutex.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (_process is null || _process.HasExited) { _process = null; return; }

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop the previous live worker cleanly.");
        }
        finally
        {
            _process = null;
        }
    }
}
