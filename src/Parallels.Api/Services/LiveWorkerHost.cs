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

    /// <summary>Paths as *this* process sees them.</summary>
    public required string DataFolder { get; init; }
    public required string ResultsRoot { get; init; }

    /// <summary>
    /// The same two directories as the <em>Docker host</em> sees them.
    ///
    /// These are not interchangeable with the properties above, and conflating
    /// them is a silent failure. When the API is itself containerised, its data
    /// folder is <c>/data</c> inside its own filesystem — but a <c>-v</c>
    /// argument is interpreted by the daemon on the host, so passing <c>/data</c>
    /// there mounts the host's <c>/data</c> (usually nonexistent, hence an empty
    /// read-only mount) instead of the repository folder. The live worker would
    /// then start, find no market data, and simply never trade.
    /// </summary>
    public string? HostDataFolder { get; init; }
    public string? HostResultsRoot { get; init; }

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

        // Bind mounts are resolved by the daemon on the host, so they must use
        // host paths. Falling back to this process's own paths is correct only
        // when the API is not itself containerised — inside a container those
        // are /data and /results, which the daemon would resolve against the
        // host root and silently mount the wrong (usually empty) directories.
        //
        // Checked here rather than as a compose-level required variable: the
        // paths only matter when a live container is actually being launched,
        // and making them mandatory up front broke routine `down`, `logs` and
        // `ps` for everyone who never touches live trading.
        var hostData = Coalesce(options.HostDataFolder, options.DataFolder);
        var hostResults = Coalesce(options.HostResultsRoot, options.ResultsRoot);

        if (RunningInContainer() && (options.HostDataFolder is null || options.HostResultsRoot is null))
        {
            throw new InvalidOperationException(
                "Refusing to launch the live worker: this API is running inside a container, but " +
                "PARALLELS_HOST_DATA_FOLDER / PARALLELS_HOST_RESULTS_PATH are not set. Docker resolves " +
                "-v arguments against the host filesystem, so without them the live container would " +
                "mount the host's /data and /results instead of this checkout — it would start, find no " +
                "market data, and never trade. Set PARALLELS_HOST_ROOT to the absolute path of the " +
                "checkout and restart the API.");
        }

        static string Coalesce(string? preferred, string fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

        var arguments = new List<string>
        {
            "run", "-d", "--name", options.ContainerName,
            "-e", $"{ParallelsJson.LiveConfigEnvVar}={ParallelsJson.Serialize(config)}",
            "-v", $"{hostData}:/data:ro",
            "-v", $"{hostResults}:/results",
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

    /// <summary>
    /// Whether this process is itself containerised, which is what makes its own
    /// paths unusable as bind-mount sources.
    /// </summary>
    private static bool RunningInContainer() =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
        || File.Exists("/.dockerenv");
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
