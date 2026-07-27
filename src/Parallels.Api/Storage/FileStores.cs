using System.Collections.Concurrent;
using System.Text.Json;
using Parallels.Contracts;

namespace Parallels.Api.Storage;

/// <summary>
/// Where the API keeps state and where workers write their output.
///
/// Both the API and every worker container must see the same
/// <see cref="ResultsRoot"/>. For the single-host deployment this targets, a
/// shared mounted volume is a reasonable and standard answer (spec 3.5) — and
/// its limitation is stated plainly rather than engineered around: a genuinely
/// multi-node deployment needs a network-shared volume or object storage, and
/// that is not built here.
/// </summary>
public sealed record StorageOptions
{
    public required string StateRoot { get; init; }
    public required string ResultsRoot { get; init; }
}

/// <summary>
/// Reads results back from the shared results volume.
///
/// The worker writes <c>result.json</c> and <c>chart-data.json</c> into the run's
/// own directory; this store never writes those two — it only reads them. The
/// status file it does own tracks jobs that are queued, running, or that died
/// before the worker could write anything.
/// </summary>
public sealed class FileBacktestStore(StorageOptions options) : IBacktestStore
{
    private readonly ConcurrentDictionary<string, BacktestResult> _pending = new();

    private string RunDirectory(string jobId) => Path.Combine(options.ResultsRoot, jobId);

    public Task SaveAsync(BacktestResult result, CancellationToken ct = default)
    {
        // In-flight and failed-before-start states live in memory plus a status
        // file; a completed run's authoritative record is the worker's own
        // result.json, which is never overwritten from here.
        _pending[result.JobId] = result;

        var directory = RunDirectory(result.JobId);
        Directory.CreateDirectory(directory);

        if (result.Status is BacktestStatus.Queued or BacktestStatus.Running or BacktestStatus.Failed)
        {
            var path = Path.Combine(directory, "status.json");
            File.WriteAllText(path, ParallelsJson.Serialize(result));
        }

        return Task.CompletedTask;
    }

    public async Task<BacktestResult?> GetAsync(string jobId, CancellationToken ct = default)
    {
        var resultPath = Path.Combine(RunDirectory(jobId), "result.json");
        if (File.Exists(resultPath))
        {
            var fromWorker = await ReadAsync<BacktestResult>(resultPath, ct);
            if (fromWorker is not null)
            {
                _pending.TryRemove(jobId, out _);
                return fromWorker;
            }
        }

        if (_pending.TryGetValue(jobId, out var pending)) return pending;

        var statusPath = Path.Combine(RunDirectory(jobId), "status.json");
        return File.Exists(statusPath) ? await ReadAsync<BacktestResult>(statusPath, ct) : null;
    }

    public async Task<IReadOnlyList<BacktestResult>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(options.ResultsRoot)) return [];

        var results = new List<BacktestResult>();
        foreach (var directory in Directory.EnumerateDirectories(options.ResultsRoot))
        {
            var jobId = Path.GetFileName(directory);
            var result = await GetAsync(jobId, ct);
            if (result is not null) results.Add(result);
        }

        return [.. results.OrderByDescending(r => r.CompletedUtc ?? DateTimeOffset.MinValue)];
    }

    public async Task<ChartData?> GetChartDataAsync(string jobId, CancellationToken ct = default)
    {
        var path = Path.Combine(RunDirectory(jobId), "chart-data.json");
        return File.Exists(path) ? await ReadAsync<ChartData>(path, ct) : null;
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, ParallelsJson.Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A run that is mid-write is a normal transient state while polling,
            // not an error worth surfacing to the caller.
            return default;
        }
    }
}

/// <summary>
/// A JSON-file collection store, guarded by a lock.
///
/// Deliberately not a database: this is desired-state configuration measured in
/// tens of records, edited by one operator through one UI. A file that can be
/// read and diffed by hand is worth more here than transactional throughput.
/// </summary>
public abstract class JsonCollectionStore<T>(string filePath)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    protected abstract string IdOf(T item);

    protected async Task<List<T>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(filePath)) return [];

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, ParallelsJson.Options, ct) ?? [];
    }

    protected async Task WriteAllAsync(List<T> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Write-then-rename: a crash mid-write leaves the previous good file
        // intact rather than a truncated one that fails to parse on restart.
        var temporary = filePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, items, ParallelsJson.Options, ct);

        File.Move(temporary, filePath, overwrite: true);
    }

    protected async Task<TResult> WithLockAsync<TResult>(Func<List<T>, Task<TResult>> action, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return await action(await ReadAllAsync(ct));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try { return await ReadAllAsync(ct); }
        finally { _mutex.Release(); }
    }

    public async Task<T?> GetAsync(string id, CancellationToken ct = default)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault(x => IdOf(x) == id);
    }

    public Task UpsertAsync(T item, CancellationToken ct = default) =>
        WithLockAsync(async all =>
        {
            all.RemoveAll(x => IdOf(x) == IdOf(item));
            all.Add(item);
            await WriteAllAsync(all, ct);
            return true;
        }, ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        WithLockAsync(async all =>
        {
            var removed = all.RemoveAll(x => IdOf(x) == id) > 0;
            if (removed) await WriteAllAsync(all, ct);
            return removed;
        }, ct);
}

public sealed class FileAlphaConfigStore(StorageOptions options)
    : JsonCollectionStore<AlphaConfig>(Path.Combine(options.StateRoot, "alphas.json")), IAlphaConfigStore
{
    protected override string IdOf(AlphaConfig item) => item.Id;
}

public sealed class FileSavedConfigStore(StorageOptions options)
    : JsonCollectionStore<SavedBacktestConfig>(Path.Combine(options.StateRoot, "saved-configs.json")), ISavedConfigStore
{
    protected override string IdOf(SavedBacktestConfig item) => item.Id;

    public Task SaveAsync(SavedBacktestConfig config, CancellationToken ct = default) =>
        UpsertAsync(config, ct);
}
