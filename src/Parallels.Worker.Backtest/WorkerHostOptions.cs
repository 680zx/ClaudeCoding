using Parallels.Contracts;

namespace Parallels.Worker.Backtest;

/// <summary>
/// How one backtest job reaches this process.
///
/// The mechanism the API actually uses is the <c>--job</c> argument (or stdin):
/// the job JSON travels with the <c>docker exec</c> invocation itself, so no
/// shared filesystem between API and worker is assumed (spec 3.5).
///
/// <c>JOB_CONFIG_PATH</c> exists purely so a human can run this binary directly
/// against a file while developing. It is deliberately not how the API dispatches
/// work — that path only functions when both sides happen to share a disk.
/// </summary>
public sealed record WorkerHostOptions
{
    public required BacktestJob Job { get; init; }

    /// <summary>Where LEAN reads market data from.</summary>
    public required string DataFolder { get; init; }

    /// <summary>Where this run writes its result, chart data and LEAN's own output.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Human-readable description of where the job came from, for the run log.</summary>
    public required string JobSource { get; init; }

    /// <summary>Set by <c>--verbose</c>; logs every entry/exit decision and the levels behind it.</summary>
    public bool VerboseSignals { get; init; }

    public static WorkerHostOptions Parse(string[] args)
    {
        var (json, source) = ReadJobJson(args);

        var job = ParallelsJson.Deserialize<BacktestJob>(json)
            ?? throw new ArgumentException("Job JSON deserialized to null.");

        var errors = job.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Job {job.JobId} is invalid:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", errors)}");
        }

        var resultsRoot = ValueAfter(args, "--results")
            ?? Environment.GetEnvironmentVariable("PARALLELS_RESULTS_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "results");

        var dataFolder = ValueAfter(args, "--data-folder")
            ?? Environment.GetEnvironmentVariable("PARALLELS_DATA_FOLDER")
            ?? Path.Combine(AppContext.BaseDirectory, "data");

        return new WorkerHostOptions
        {
            Job = job,
            DataFolder = Path.GetFullPath(dataFolder),
            OutputDirectory = Path.GetFullPath(Path.Combine(resultsRoot, job.JobId)),
            JobSource = source,
            VerboseSignals = args.Contains("--verbose"),
        };
    }

    private static (string Json, string Source) ReadJobJson(string[] args)
    {
        var inline = ValueAfter(args, "--job");
        if (!string.IsNullOrWhiteSpace(inline))
            return (inline, "--job argument");

        if (args.Contains("--job-stdin"))
        {
            var stdin = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(stdin))
                throw new ArgumentException("--job-stdin was given but stdin was empty.");
            return (stdin, "stdin");
        }

        var path = ValueAfter(args, "--job-file")
            ?? Environment.GetEnvironmentVariable(ParallelsJson.JobConfigPathEnvVar);

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Job file not found: {path}", path);
            return (File.ReadAllText(path), $"file {path}");
        }

        throw new ArgumentException(
            "No job supplied. Use --job '<json>', --job-stdin, --job-file <path>, " +
            $"or set {ParallelsJson.JobConfigPathEnvVar}.");
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
