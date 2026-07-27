using System.Collections.Concurrent;
using Parallels.Api.Dispatch;
using Parallels.Api.Storage;
using Parallels.Contracts;

namespace Parallels.Api.Services;

/// <summary>
/// Accepts backtest jobs, dispatches each into its own worker process, and makes
/// the outcome pollable.
///
/// Jobs run against a bounded number of concurrent worker processes rather than
/// all at once: each one is a full LEAN engine, and oversubscribing the host
/// makes every run slower without finishing any of them sooner.
/// </summary>
public sealed class BacktestService(
    IJobDispatcher dispatcher,
    IBacktestStore store,
    StorageOptions storage,
    ILogger<BacktestService> logger)
{
    private readonly SemaphoreSlim _concurrency = new(Math.Max(1, Environment.ProcessorCount / 2));
    private readonly ConcurrentDictionary<string, Task> _running = new();

    public string Mechanism => dispatcher.Mechanism;

    /// <summary>
    /// Queues a job and returns immediately. The Backtest tab polls for
    /// completion (spec 4.2) rather than holding an HTTP request open for the
    /// minutes a real run can take.
    /// </summary>
    public async Task<BacktestResult> SubmitAsync(BacktestJob job, CancellationToken ct = default)
    {
        var queued = new BacktestResult
        {
            JobId = job.JobId,
            Name = job.Name,
            Status = BacktestStatus.Queued,
        };

        await store.SaveAsync(queued, ct);

        _running[job.JobId] = Task.Run(() => RunAsync(job), CancellationToken.None);
        return queued;
    }

    private async Task RunAsync(BacktestJob job)
    {
        await _concurrency.WaitAsync();
        try
        {
            await store.SaveAsync(new BacktestResult
            {
                JobId = job.JobId,
                Name = job.Name,
                Status = BacktestStatus.Running,
            });

            var outcome = await dispatcher.RunAsync(job, storage.ResultsRoot);

            if (outcome.ExitCode != 0)
            {
                // The worker writes its own result.json even on failure; only
                // record a failure here if it did not get that far.
                var written = await store.GetAsync(job.JobId);
                if (written is null || written.Status != BacktestStatus.Failed)
                {
                    await store.SaveAsync(new BacktestResult
                    {
                        JobId = job.JobId,
                        Name = job.Name,
                        Status = BacktestStatus.Failed,
                        Error = Summarize(outcome),
                        CompletedUtc = DateTimeOffset.UtcNow,
                    });
                }
            }

            logger.LogInformation("Job {JobId} dispatched via {Mechanism}, exit {ExitCode}.",
                job.JobId, outcome.Mechanism, outcome.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed to dispatch.", job.JobId);
            await store.SaveAsync(new BacktestResult
            {
                JobId = job.JobId,
                Name = job.Name,
                Status = BacktestStatus.Failed,
                Error = ex.Message,
                CompletedUtc = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            _concurrency.Release();
            _running.TryRemove(job.JobId, out _);
        }
    }

    private static string Summarize(DispatchOutcome outcome)
    {
        var stderr = outcome.Stderr.Trim();
        if (stderr.Length > 0) return Truncate(stderr);

        var stdout = outcome.Stdout.Trim();
        return stdout.Length > 0
            ? Truncate(stdout)
            : $"Worker exited with code {outcome.ExitCode} and produced no output.";
    }

    private static string Truncate(string value, int max = 4000) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(value.Length - max), "");
}
