namespace Parallels.Contracts;

/// <summary>
/// A named, persisted parameter set plus a link back to the report that
/// justified saving it (spec 3.1/4.2).
///
/// The link is by job id rather than an embedded copy of the statistics: the
/// report is already stored, and duplicating it here would let the two drift.
/// </summary>
public sealed record SavedBacktestConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The run this config was saved from; resolves to a <see cref="BacktestResult"/> and its chart data.</summary>
    public required string SourceJobId { get; init; }

    public required string Symbol { get; init; }
    public string Market { get; init; } = "binance";
    public string Resolution { get; init; } = "Hour";
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }

    /// <summary>Same shape the Live tab consumes, so this can seed a new alpha directly.</summary>
    public required StrategyParameters Parameters { get; init; }

    public string? Notes { get; init; }

    /// <summary>Headline numbers copied at save time purely for list display.</summary>
    public decimal? TotalReturnPercent { get; init; }
    public decimal? SharpeRatio { get; init; }
    public decimal? MaxDrawdownPercent { get; init; }

    public DateTimeOffset SavedUtc { get; init; } = DateTimeOffset.UtcNow;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) yield return "Id is required.";
        if (string.IsNullOrWhiteSpace(Name)) yield return "Name is required.";
        if (string.IsNullOrWhiteSpace(SourceJobId)) yield return "SourceJobId is required.";
        if (Parameters is null) yield return "Parameters are required.";
        else foreach (var error in Parameters.Validate()) yield return error;
    }
}
