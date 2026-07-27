using Parallels.Api.Dispatch;
using Parallels.Api.Services;
using Parallels.Api.Storage;
using Parallels.Contracts;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Serialization: the same options Contracts defines, so a request body, a
// worker hand-off and a stored file are all the same JSON (spec 3.5).
// ---------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(options =>
{
    var shared = ParallelsJson.Options;
    options.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.DefaultIgnoreCondition = shared.DefaultIgnoreCondition;
    foreach (var converter in shared.Converters) options.SerializerOptions.Converters.Add(converter);
});

var repoRoot = builder.Configuration["PARALLELS_ROOT"]
    ?? Environment.GetEnvironmentVariable("PARALLELS_ROOT")
    ?? Directory.GetCurrentDirectory();

var storage = new StorageOptions
{
    StateRoot = Environment.GetEnvironmentVariable("PARALLELS_STATE_PATH")
                ?? Path.Combine(repoRoot, "state"),
    ResultsRoot = Environment.GetEnvironmentVariable("PARALLELS_RESULTS_PATH")
                  ?? Path.Combine(repoRoot, "results"),
};

Directory.CreateDirectory(storage.StateRoot);
Directory.CreateDirectory(storage.ResultsRoot);

var dataFolder = Environment.GetEnvironmentVariable("PARALLELS_DATA_FOLDER")
                 ?? Path.Combine(repoRoot, "data");

var backtestWorkerDll = Environment.GetEnvironmentVariable("PARALLELS_BACKTEST_WORKER_DLL")
    ?? Path.Combine(repoRoot, "src", "Parallels.Worker.Backtest", "bin", "Debug", "net10.0", "Parallels.Worker.Backtest.dll");

var liveWorkerDll = Environment.GetEnvironmentVariable("PARALLELS_LIVE_WORKER_DLL")
    ?? Path.Combine(repoRoot, "src", "Parallels.Worker.Live.Binance", "bin", "Debug", "net10.0", "Parallels.Worker.Live.Binance.dll");

builder.Services.AddSingleton(storage);
builder.Services.AddSingleton<IBacktestStore, FileBacktestStore>();
builder.Services.AddSingleton<IAlphaConfigStore, FileAlphaConfigStore>();
builder.Services.AddSingleton<ISavedConfigStore, FileSavedConfigStore>();

// ---------------------------------------------------------------------------
// Dispatch: prefer docker exec into the warm container, fall back to a child
// process when no daemon is reachable. Which one actually ran is reported by
// /api/system rather than assumed (spec 3.2, spec 7).
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IJobDispatcher>(sp =>
{
    var dockerOptions = new DockerDispatchOptions
    {
        ContainerName = Environment.GetEnvironmentVariable("PARALLELS_BACKTEST_CONTAINER")
                        ?? "parallels-worker-backtest",
    };

    var docker = new DockerExecDispatcher(dockerOptions,
        sp.GetRequiredService<ILogger<DockerExecDispatcher>>());

    if (docker.IsAvailableAsync().GetAwaiter().GetResult()) return docker;

    sp.GetRequiredService<ILogger<Program>>().LogWarning(
        "No warm Docker backtest container reachable — dispatching jobs as child processes instead.");

    return new ChildProcessDispatcher(
        new ChildProcessDispatchOptions { WorkerDllPath = backtestWorkerDll, DataFolder = dataFolder },
        sp.GetRequiredService<ILogger<ChildProcessDispatcher>>());
});

builder.Services.AddSingleton<ILiveWorkerHost>(sp =>
{
    var liveOptions = new LiveHostOptions
    {
        WorkerDllPath = liveWorkerDll,
        DataFolder = dataFolder,
        ResultsRoot = storage.ResultsRoot,
        // Set by compose when the API runs in a container: `docker run -v` is
        // evaluated by the daemon on the host, so the live container has to be
        // given host paths, not this process's own. Either name the two paths
        // outright, or set PARALLELS_HOST_ROOT and let them be derived.
        HostDataFolder = HostPath("PARALLELS_HOST_DATA_FOLDER", "data"),
        HostResultsRoot = HostPath("PARALLELS_HOST_RESULTS_PATH", "results"),
    };

    var docker = new DockerLiveWorkerHost(liveOptions, sp.GetRequiredService<ILogger<DockerLiveWorkerHost>>());
    var dockerUsable = ProbeDockerDaemon();

    return dockerUsable
        ? docker
        : new ChildProcessLiveWorkerHost(liveOptions, sp.GetRequiredService<ILogger<ChildProcessLiveWorkerHost>>());
});

builder.Services.AddSingleton<BacktestService>();
builder.Services.AddSingleton<LiveSessionManager>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
          .AllowAnyHeader()
          .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// ---------------------------------------------------------------------------
// Metadata
// ---------------------------------------------------------------------------

/// The parameter schema the Backtest tab renders its form from (spec 4.2).
app.MapGet("/api/strategies", () => Results.Ok(StrategyCatalog.All));

app.MapGet("/api/symbols", () =>
{
    // Offer exactly the symbols there is actually data on disk for. Listing a
    // symbol the engine would then find no bars for turns a missing-data problem
    // into a silently flat equity curve.
    var hourly = Path.Combine(dataFolder, "crypto", "binance", "hour");
    if (!Directory.Exists(hourly)) return Results.Ok(Array.Empty<string>());

    var symbols = Directory.EnumerateFiles(hourly, "*_trade.zip")
        .Select(f => Path.GetFileName(f).Replace("_trade.zip", "", StringComparison.Ordinal).ToUpperInvariant())
        .Order()
        .ToArray();

    return Results.Ok(symbols);
});

app.MapGet("/api/system", async (BacktestService backtests, LiveSessionManager live) => Results.Ok(new
{
    dispatchMechanism = backtests.Mechanism,
    dataFolder,
    resultsRoot = storage.ResultsRoot,
    live = await live.GetStateAsync(),
}));

// ---------------------------------------------------------------------------
// Backtests
// ---------------------------------------------------------------------------

// The request binds straight into BacktestJob — the same type that is serialized
// into the worker hand-off. No API-only request model in between (spec 3.5).
app.MapPost("/api/backtests", async (BacktestJob job, BacktestService service, CancellationToken ct) =>
{
    var submitted = job with
    {
        JobId = string.IsNullOrWhiteSpace(job.JobId) ? NewJobId() : job.JobId,
    };

    var errors = submitted.Validate().ToList();
    if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["job"] = [.. errors] });

    return Results.Accepted($"/api/backtests/{submitted.JobId}", await service.SubmitAsync(submitted, ct));
});

app.MapGet("/api/backtests", async (IBacktestStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(ct)));

app.MapGet("/api/backtests/{jobId}", async (string jobId, IBacktestStore store, CancellationToken ct) =>
    await store.GetAsync(jobId, ct) is { } result ? Results.Ok(result) : Results.NotFound());

app.MapGet("/api/backtests/{jobId}/chart", async (string jobId, IBacktestStore store, CancellationToken ct) =>
    await store.GetChartDataAsync(jobId, ct) is { } chart ? Results.Ok(chart) : Results.NotFound());

// ---------------------------------------------------------------------------
// Saved configs (the Backtest tab's Save modal, spec 4.2)
// ---------------------------------------------------------------------------

app.MapGet("/api/saved-configs", async (ISavedConfigStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(ct)));

app.MapPost("/api/saved-configs", async (SavedBacktestConfig config, ISavedConfigStore store,
    IBacktestStore backtests, CancellationToken ct) =>
{
    var toSave = config with { Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("n")[..12] : config.Id };

    var errors = toSave.Validate().ToList();

    // A saved config's whole purpose is to link parameters to the report that
    // justified them, so a dangling source run makes it worthless.
    if (await backtests.GetAsync(toSave.SourceJobId, ct) is null)
        errors.Add($"No backtest found with id '{toSave.SourceJobId}'.");

    if (errors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["config"] = [.. errors] });

    await store.SaveAsync(toSave, ct);
    return Results.Created($"/api/saved-configs/{toSave.Id}", toSave);
});

app.MapGet("/api/saved-configs/{id}", async (string id, ISavedConfigStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is { } config ? Results.Ok(config) : Results.NotFound());

app.MapDelete("/api/saved-configs/{id}", async (string id, ISavedConfigStore store, CancellationToken ct) =>
    await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// ---------------------------------------------------------------------------
// Live alphas (the Live tab's tiles, spec 4.1)
// ---------------------------------------------------------------------------

app.MapGet("/api/alphas", async (IAlphaConfigStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(ct)));

app.MapPost("/api/alphas", async (AlphaConfig config, IAlphaConfigStore store,
    LiveSessionManager live, CancellationToken ct) =>
{
    var toSave = config with
    {
        Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("n")[..12] : config.Id,
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    var errors = toSave.Validate().ToList();
    if (errors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["alpha"] = [.. errors] });

    await store.UpsertAsync(toSave, ct);
    await live.ApplyDesiredStateAsync(ct);
    return Results.Created($"/api/alphas/{toSave.Id}", toSave);
});

app.MapPut("/api/alphas/{id}", async (string id, AlphaConfig config, IAlphaConfigStore store,
    LiveSessionManager live, CancellationToken ct) =>
{
    var existing = await store.GetAsync(id, ct);
    if (existing is null) return Results.NotFound();

    var updated = config with { Id = id, CreatedUtc = existing.CreatedUtc, UpdatedUtc = DateTimeOffset.UtcNow };

    var errors = updated.Validate().ToList();
    if (errors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["alpha"] = [.. errors] });

    await store.UpsertAsync(updated, ct);
    await live.ApplyDesiredStateAsync(ct);
    return Results.Ok(updated);
});

app.MapDelete("/api/alphas/{id}", async (string id, IAlphaConfigStore store,
    LiveSessionManager live, CancellationToken ct) =>
{
    if (!await store.DeleteAsync(id, ct)) return Results.NotFound();
    await live.ApplyDesiredStateAsync(ct);
    return Results.NoContent();
});

// Toggling is a soft action by default (spec 4.1): a disabled alpha stops
// opening new positions, and any existing position and its resting protective
// orders are deliberately left alone on the exchange.
app.MapPost("/api/alphas/{id}/enabled", async (string id, ToggleRequest request,
    IAlphaConfigStore store, LiveSessionManager live, CancellationToken ct) =>
{
    var existing = await store.GetAsync(id, ct);
    if (existing is null) return Results.NotFound();

    var updated = existing with { Enabled = request.Enabled, UpdatedUtc = DateTimeOffset.UtcNow };
    await store.UpsertAsync(updated, ct);
    await live.ApplyDesiredStateAsync(ct);

    return Results.Ok(updated);
});

app.MapGet("/api/live/status", async (LiveSessionManager live, CancellationToken ct) =>
    Results.Ok(await live.GetStateAsync(ct)));

app.MapPost("/api/live/apply", async (LiveSessionManager live, CancellationToken ct) =>
    Results.Ok(await live.ApplyDesiredStateAsync(ct)));

app.Run();

static string NewJobId() =>
    $"bt-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid().ToString("n")[..6]}";

/// <summary>
/// Resolves a bind-mount source as the Docker host sees it, from an explicit
/// variable or by joining PARALLELS_HOST_ROOT. Returns null when neither is set,
/// so "absent" stays distinguishable from a wrong-but-present path — which is
/// what lets the live worker refuse with a useful message instead of mounting
/// the host's /data.
/// </summary>
static string? HostPath(string explicitVariable, string leaf)
{
    var direct = Environment.GetEnvironmentVariable(explicitVariable);
    if (!string.IsNullOrWhiteSpace(direct)) return direct;

    var root = Environment.GetEnvironmentVariable("PARALLELS_HOST_ROOT");
    return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, leaf);
}

static bool ProbeDockerDaemon()
{
    try
    {
        var result = ProcessRunner.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"],
            null, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        return result.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

/// <summary>Body of the enable/disable toggle.</summary>
public sealed record ToggleRequest(bool Enabled);

/// <summary>Named so integration tests can reference the API host.</summary>
public partial class Program;
