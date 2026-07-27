using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Parallels.Contracts;
using Parallels.Strategies;
using Parallels.Strategies.Execution;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.Server;
using QuantConnect.Util;

namespace Parallels.Worker.Backtest;

/// <summary>
/// Runs exactly one backtest, then exits.
///
/// One process is one LEAN engine session (spec 2) — <c>Composer</c>,
/// <c>Config</c> and LEAN's handler singletons are process-wide, so this process
/// deliberately does not loop over jobs. The warm container the API dispatches
/// into stays up; this process inside it does not (spec 3.5).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        WorkerHostOptions options;
        try
        {
            options = WorkerHostOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] could not read job: {ex.Message}");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);

        Console.WriteLine($"[worker] pid {Environment.ProcessId} job {options.Job.JobId} " +
                          $"(from {options.JobSource})");
        Console.WriteLine($"[worker] data folder  : {options.DataFolder}");
        Console.WriteLine($"[worker] output folder: {options.OutputDirectory}");

        try
        {
            var statistics = RunEngine(options);
            var result = BuildResult(options, statistics, startedUtc, error: null);
            WriteResult(options, result);

            Console.WriteLine($"[worker] completed in {stopwatch.Elapsed.TotalSeconds:F1}s");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] run failed: {ex}");
            var result = BuildResult(options, new Dictionary<string, string>(), startedUtc, ex.Message);
            try { WriteResult(options, result); } catch { /* the exception above is the real story */ }
            return 1;
        }
    }

    /// <summary>
    /// The LEAN bootstrap ceremony, in the order LEAN itself requires.
    ///
    /// Config must be fully populated before any handler is constructed, because
    /// <c>FromConfiguration</c> resolves handler implementations by reading it;
    /// and the session must be published before the engine runs, because LEAN
    /// constructs the algorithm by reflection with no constructor injection
    /// available (spec 2).
    /// </summary>
    private static Dictionary<string, string> RunEngine(WorkerHostOptions options)
    {
        ConfigureLean(options);

        SessionContext.Publish(new ParallelsSession
        {
            Mode = SessionMode.Backtest,
            Job = options.Job,
            OutputDirectory = options.OutputDirectory,
            VerboseSignals = options.VerboseSignals,
            // Backtest gets exchange-filter rounding only. The aggregate risk and
            // rate-limit gates are live concerns; applying them here would make
            // the backtest model constraints that a single-strategy run does not
            // actually operate under.
            OrderGate = new ExchangeFilterGate(),
        });

        // Constructed directly rather than via FromConfiguration: the job queue
        // and messaging implementations LEAN's own launcher uses are not on
        // NuGet (see LeanHostShims), and this process already knows its job.
        var strategiesAssemblyPath = typeof(ParallelsAlgorithm).Assembly.Location;
        var packet = PacketFactory.ForBacktest(options.Job);

        var systemHandlers = new LeanEngineSystemHandlers(
            new SingleJobQueue(packet, strategiesAssemblyPath),
            new QuantConnect.Api.Api(),
            new NullMessagingHandler(),
            new LocalLeanManager());

        systemHandlers.Initialize();

        var job = systemHandlers.JobQueue.NextJob(out var assemblyPath);

        var algorithmHandlers = LeanEngineAlgorithmHandlers.FromConfiguration(Composer.Instance, researchMode: false);
        var algorithmManager = new AlgorithmManager(liveMode: false, job);

        systemHandlers.LeanManager.Initialize(systemHandlers, algorithmHandlers, job, algorithmManager);

        try
        {
            var engine = new Engine(systemHandlers, algorithmHandlers, liveMode: false);
            engine.Run(job, algorithmManager, assemblyPath, WorkerThread.Instance);

            // Read statistics from LEAN's own result handler rather than
            // recomputing anything locally — these are the standard LEAN
            // statistics the whole project exists to attribute per strategy.
            return algorithmHandlers.Results is BacktestingResultHandler backtestResults
                ? new Dictionary<string, string>(backtestResults.FinalStatistics)
                : [];
        }
        finally
        {
            systemHandlers.JobQueue.AcknowledgeJob(job);
            algorithmHandlers.Dispose();
            systemHandlers.Dispose();
        }
    }

    private static void ConfigureLean(WorkerHostOptions options)
    {
        var strategiesAssembly = typeof(ParallelsAlgorithm).Assembly;
        var algorithmLocation = Path.GetFileName(strategiesAssembly.Location);

        Config.Set("environment", "backtesting");
        Config.Set("algorithm-type-name", nameof(ParallelsAlgorithm));
        Config.Set("algorithm-language", "CSharp");
        Config.Set("algorithm-location", algorithmLocation);

        Config.Set("data-folder", options.DataFolder);
        Config.Set("results-destination-folder", options.OutputDirectory);
        Config.Set("algorithm-id", options.Job.JobId);

        // Backtesting handler set. Naming them explicitly rather than relying on
        // defaults keeps this readable as "this is the backtest hosting code",
        // which is the whole reason backtest and live are separate executables
        // (spec 2). The system handlers above are constructed directly, so only
        // the algorithm-side handlers are resolved from config here.
        Config.Set("setup-handler", "QuantConnect.Lean.Engine.Setup.BacktestingSetupHandler");
        Config.Set("result-handler", "QuantConnect.Lean.Engine.Results.BacktestingResultHandler");
        Config.Set("data-feed-handler", "QuantConnect.Lean.Engine.DataFeeds.FileSystemDataFeed");
        Config.Set("real-time-handler", "QuantConnect.Lean.Engine.RealTime.BacktestingRealTimeHandler");
        Config.Set("history-provider", "QuantConnect.Lean.Engine.HistoricalData.SubscriptionDataReaderHistoryProvider");
        Config.Set("transaction-handler", "QuantConnect.Lean.Engine.TransactionHandlers.BacktestingTransactionHandler");
        Config.Set("data-provider", "QuantConnect.Lean.Engine.DataFeeds.DefaultDataProvider");
        Config.Set("map-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskMapFileProvider");
        Config.Set("factor-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskFactorFileProvider");
        Config.Set("object-store", "QuantConnect.Lean.Engine.Storage.LocalObjectStore");
        Config.Set("data-permission-manager", "QuantConnect.Lean.Engine.DataFeeds.DataPermissionManager");

        Config.Set("log-handler", "QuantConnect.Logging.CompositeLogHandler");
        Config.Set("close-automatically", "true");
        Config.Set("show-missing-data-logs", "true");
        Config.Set("maximum-data-points-per-chart-series", "1000000");
    }

    private static BacktestResult BuildResult(
        WorkerHostOptions options,
        IReadOnlyDictionary<string, string> statistics,
        DateTimeOffset startedUtc,
        string? error)
    {
        var job = options.Job;
        var chartPath = Path.Combine(options.OutputDirectory, "chart-data.json");

        return new BacktestResult
        {
            JobId = job.JobId,
            Name = job.Name,
            Status = error is null ? BacktestStatus.Completed : BacktestStatus.Failed,
            Error = error,
            Statistics = statistics,
            TotalReturnPercent = ReadPercent(statistics, "Net Profit", "Total Net Profit"),
            SharpeRatio = ReadDecimal(statistics, "Sharpe Ratio"),
            MaxDrawdownPercent = ReadPercent(statistics, "Drawdown"),
            WinRatePercent = ReadPercent(statistics, "Win Rate"),
            TotalTrades = ReadInt(statistics, "Total Orders", "Total Trades"),
            EndingEquity = ReadDecimal(statistics, "End Equity"),
            HasChartData = File.Exists(chartPath),
            CompletedUtc = DateTimeOffset.UtcNow,
            Provenance = new RunProvenance
            {
                Symbol = job.Symbol,
                Market = job.Market,
                Resolution = job.Resolution,
                StartDate = job.StartDate,
                EndDate = job.EndDate,
                StrategyType = job.Parameters.StrategyType,
                Parameters = job.Parameters,
                StartingCash = job.StartingCash,
                FeeFraction = job.FeeFraction,
                SlippageFraction = job.SlippageFraction,
                LeanVersion = typeof(Config).Assembly.GetName().Version?.ToString(),
                WorkerVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                RunStartedUtc = startedUtc,
                RunCompletedUtc = DateTimeOffset.UtcNow,
                DataSource = File.Exists(Path.Combine(options.DataFolder, "PROVENANCE.txt"))
                    ? File.ReadAllText(Path.Combine(options.DataFolder, "PROVENANCE.txt")).Trim()
                    : null,
            },
        };
    }

    private static void WriteResult(WorkerHostOptions options, BacktestResult result)
    {
        var path = Path.Combine(options.OutputDirectory, "result.json");
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, result, ParallelsJson.Options);
        Console.WriteLine($"[worker] wrote {path}");
    }

    private static decimal? ReadDecimal(IReadOnlyDictionary<string, string> stats, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (stats.TryGetValue(key, out var raw) &&
                decimal.TryParse(raw.Replace("$", "").Replace("%", "").Replace(",", ""),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static decimal? ReadPercent(IReadOnlyDictionary<string, string> stats, params string[] keys) =>
        ReadDecimal(stats, keys);

    private static int? ReadInt(IReadOnlyDictionary<string, string> stats, params string[] keys)
    {
        var value = ReadDecimal(stats, keys);
        return value is null ? null : (int)value.Value;
    }
}
