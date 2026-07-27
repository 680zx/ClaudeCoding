namespace Parallels.Contracts;

/// <summary>
/// Everything needed to reproduce a result exactly (spec 5). Recorded next to
/// every run rather than described in a README, because a number nobody can
/// regenerate is not a result.
/// </summary>
public sealed record RunProvenance
{
    public required string Symbol { get; init; }
    public required string Market { get; init; }
    public required string Resolution { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required string StrategyType { get; init; }

    /// <summary>The exact parameter set, re-serialized from what the engine actually ran.</summary>
    public required StrategyParameters Parameters { get; init; }

    public decimal StartingCash { get; init; }
    public decimal FeeFraction { get; init; }
    public decimal SlippageFraction { get; init; }

    /// <summary>Resolved LEAN package version, read from the loaded assembly at run time.</summary>
    public string? LeanVersion { get; init; }

    /// <summary>Worker assembly version that hosted the engine.</summary>
    public string? WorkerVersion { get; init; }

    public DateTimeOffset RunStartedUtc { get; init; }
    public DateTimeOffset RunCompletedUtc { get; init; }

    /// <summary>Provenance of the price data the run consumed.</summary>
    public string? DataSource { get; init; }
}

/// <summary>
/// Headline statistics only — the chart series live in a companion
/// chart-data.json (spec 3.4) so polling this stays cheap.
///
/// Every statistic here is read out of LEAN's own computed statistics rather
/// than recomputed locally; the raw dictionary is preserved in
/// <see cref="Statistics"/> so nothing is lost to the typed projection.
/// </summary>
public sealed record BacktestResult
{
    public required string JobId { get; init; }
    public required BacktestStatus Status { get; init; }

    public string? Name { get; init; }

    /// <summary>Populated when Status is Failed.</summary>
    public string? Error { get; init; }

    public RunProvenance? Provenance { get; init; }

    /// <summary>LEAN's full statistics dictionary, verbatim.</summary>
    public IReadOnlyDictionary<string, string> Statistics { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Typed projection of the statistics the UI puts on top.</summary>
    public decimal? TotalReturnPercent { get; init; }
    public decimal? SharpeRatio { get; init; }
    public decimal? MaxDrawdownPercent { get; init; }
    public decimal? WinRatePercent { get; init; }
    public int? TotalTrades { get; init; }
    public decimal? EndingEquity { get; init; }

    /// <summary>True when a chart-data.json companion was written for this run.</summary>
    public bool HasChartData { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }
}
