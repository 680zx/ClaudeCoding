using System.Text.Json.Serialization;

namespace Parallels.Contracts;

/// <summary>
/// One backtest: exactly one strategy configuration over one symbol and date
/// range.
///
/// One job maps to one LEAN engine process and therefore to one independently
/// attributable set of LEAN statistics (spec 3.2) — jobs are never batched into
/// a single engine run, because LEAN computes statistics per algorithm run and
/// blending configurations would blend their P&amp;L into one report.
/// </summary>
public sealed record BacktestJob
{
    /// <summary>
    /// Assigned by the API when a client does not supply one, which is the
    /// normal case from the Backtest tab.
    ///
    /// Deliberately not <c>required</c>: a required member makes
    /// System.Text.Json reject any payload that omits it, so binding would fail
    /// before the API ever got the chance to assign the id. <see cref="Validate"/>
    /// still enforces that it is set by the time the job is dispatched.
    /// </summary>
    public string JobId { get; init; } = "";

    /// <summary>Exchange ticker, e.g. BTCUSDT.</summary>
    public required string Symbol { get; init; }

    /// <summary>LEAN market name. Binance for now; the field exists so a second venue is data, not code.</summary>
    public string Market { get; init; } = "binance";

    /// <summary>LEAN resolution name: Minute, Hour or Daily.</summary>
    public string Resolution { get; init; } = "Hour";

    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }

    public decimal StartingCash { get; init; } = 100_000m;

    /// <summary>Quote currency held as starting cash, e.g. USDT for BTCUSDT.</summary>
    public string AccountCurrency { get; init; } = "USDT";

    public required StrategyParameters Parameters { get; init; }

    /// <summary>
    /// Taker fee actually charged per fill, as a fraction of notional. Modelled
    /// in backtest so backtest and live stay comparable (spec 5).
    /// </summary>
    public decimal FeeFraction { get; init; } = 0.001m;

    /// <summary>Slippage applied to every fill, as a fraction of price.</summary>
    public decimal SlippageFraction { get; init; } = 0.0005m;

    /// <summary>Optional human label carried through into the saved report.</summary>
    public string? Name { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(JobId)) yield return "JobId is required.";
        if (string.IsNullOrWhiteSpace(Symbol)) yield return "Symbol is required.";
        if (string.IsNullOrWhiteSpace(Market)) yield return "Market is required.";
        if (!ValidResolutions.Contains(Resolution, StringComparer.OrdinalIgnoreCase))
            yield return $"Resolution must be one of: {string.Join(", ", ValidResolutions)}.";
        if (EndDate <= StartDate) yield return "EndDate must be after StartDate.";
        if (StartingCash <= 0) yield return "StartingCash must be > 0.";
        if (FeeFraction < 0 || FeeFraction > 0.05m) yield return "FeeFraction must be in [0, 0.05].";
        if (SlippageFraction < 0 || SlippageFraction > 0.05m) yield return "SlippageFraction must be in [0, 0.05].";
        if (Parameters is null) yield return "Parameters are required.";
        else foreach (var error in Parameters.Validate()) yield return error;
    }

    [JsonIgnore]
    public static IReadOnlyList<string> ValidResolutions { get; } = ["Minute", "Hour", "Daily"];
}

/// <summary>Lifecycle of a dispatched job, as the Backtest tab polls it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BacktestStatus>))]
public enum BacktestStatus
{
    Queued,
    Running,
    Completed,
    Failed
}
