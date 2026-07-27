using Parallels.Contracts;

namespace Parallels.Worker.Live.Binance;

/// <summary>Raised when the mainnet confirmation gate refuses to start a session.</summary>
public sealed class MainnetGateException(string message) : Exception(message);

/// <summary>
/// Everything the live worker needs, assembled from its environment.
///
/// The desired state arrives in an environment variable set at container launch
/// (spec 3.5) — never read from a file path the API wrote on its own disk, since
/// that only works when both happen to share a filesystem.
/// </summary>
public sealed record LiveWorkerOptions
{
    public required LiveSessionConfig Session { get; init; }
    public required string DataFolder { get; init; }
    public required string OutputDirectory { get; init; }
    public required bool IsMainnet { get; init; }
    public string? ApiKey { get; init; }
    public string? ApiSecret { get; init; }

    public static LiveWorkerOptions Parse(string[] args)
    {
        var json = Environment.GetEnvironmentVariable(ParallelsJson.LiveConfigEnvVar);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException(
                $"{ParallelsJson.LiveConfigEnvVar} is not set. The live worker takes its desired state " +
                "from the environment at launch; the API sets it when it starts the container.");
        }

        var session = ParallelsJson.Deserialize<LiveSessionConfig>(json)
            ?? throw new ArgumentException($"{ParallelsJson.LiveConfigEnvVar} did not deserialize to a session.");

        if (session.Alphas.Count == 0)
            throw new ArgumentException("The session contains no alphas. Nothing to run.");

        foreach (var alpha in session.Alphas)
        {
            var errors = alpha.Validate().ToList();
            if (errors.Count > 0)
            {
                throw new ArgumentException(
                    $"Alpha '{alpha.Id}' is invalid: {string.Join("; ", errors)}");
            }
        }

        var environmentName = Environment.GetEnvironmentVariable("BINANCE_ENVIRONMENT")
            ?? session.Environment;
        var isMainnet = string.Equals(environmentName, "mainnet", StringComparison.OrdinalIgnoreCase);

        // The mainnet confirmation gate (spec 3.1). Selecting "mainnet" is a
        // destination, not consent — trading real money additionally requires an
        // explicit confirmation flag, so no amount of editing stored desired
        // state can promote a session to real funds on its own.
        if (isMainnet && !session.ConfirmMainnet)
        {
            throw new MainnetGateException(
                "Refusing to start: this session targets Binance MAINNET but was not explicitly " +
                "confirmed. Set PARALLELS_CONFIRM_MAINNET=true on the API (which sets confirmMainnet " +
                "on the session) to trade real funds. No orders have been placed.");
        }

        var resultsRoot = ValueAfter(args, "--results")
            ?? Environment.GetEnvironmentVariable("PARALLELS_RESULTS_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "results");

        var dataFolder = ValueAfter(args, "--data-folder")
            ?? Environment.GetEnvironmentVariable("PARALLELS_DATA_FOLDER")
            ?? Path.Combine(AppContext.BaseDirectory, "data");

        return new LiveWorkerOptions
        {
            Session = session,
            DataFolder = Path.GetFullPath(dataFolder),
            OutputDirectory = Path.GetFullPath(Path.Combine(resultsRoot, session.SessionId)),
            IsMainnet = isMainnet,
            ApiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY"),
            ApiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET"),
        };
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
