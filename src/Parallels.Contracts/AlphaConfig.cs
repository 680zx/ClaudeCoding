namespace Parallels.Contracts;

/// <summary>
/// One configured live alpha — the Live tab's tile shape (spec 3.1/4.1).
///
/// Note the parameter type is the same <see cref="StrategyParameters"/> the
/// Backtest tab uses. That is deliberate (spec 4.2): a saved backtest config
/// must be usable as the starting point for a live alpha, so the two shapes are
/// never allowed to diverge.
/// </summary>
public sealed record AlphaConfig
{
    /// <summary>Assigned by the API when absent; see the note on <see cref="BacktestJob.JobId"/>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name shown on the tile.</summary>
    public required string Name { get; init; }

    /// <summary>Symbols this alpha trades. One entry for now; a list so a basket alpha needs no shape change.</summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    public string Market { get; init; } = "binance";

    public string Resolution { get; init; } = "Hour";

    public required StrategyParameters Parameters { get; init; }

    /// <summary>
    /// Desired state, not observed state. Disabling is soft by default (spec
    /// 4.1): the alpha stops opening new positions, but any existing position
    /// and its resting protective orders are left alone on the exchange.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Cap on this alpha's share of account equity. The live risk gateway
    /// enforces it across all alphas at once (spec 3.2).
    /// </summary>
    public decimal MaxExposureFraction { get; init; } = 0.25m;

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Set when this alpha was created from a saved backtest config (spec 4.2 / phase 4).</summary>
    public string? SourceSavedConfigId { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) yield return "Id is required.";
        if (string.IsNullOrWhiteSpace(Name)) yield return "Name is required.";
        if (Symbols is null || Symbols.Count == 0) yield return "At least one symbol is required.";
        else if (Symbols.Any(string.IsNullOrWhiteSpace)) yield return "Symbols must not be blank.";
        if (!BacktestJob.ValidResolutions.Contains(Resolution, StringComparer.OrdinalIgnoreCase))
            yield return $"Resolution must be one of: {string.Join(", ", BacktestJob.ValidResolutions)}.";
        if (MaxExposureFraction <= 0 || MaxExposureFraction > 1m)
            yield return "MaxExposureFraction must be in (0, 1].";
        if (Parameters is null) yield return "Parameters are required.";
        else foreach (var error in Parameters.Validate()) yield return error;
    }
}

/// <summary>
/// The whole desired state handed to a live worker at launch, as one object.
///
/// This is what gets serialized into the container's environment variable (spec
/// 3.5) — never written to a file that the container is assumed to be able to
/// read from the API's own disk.
/// </summary>
public sealed record LiveSessionConfig
{
    public required string SessionId { get; init; }

    /// <summary>Only the enabled alphas are handed over; disabled ones simply are not loaded.</summary>
    public required IReadOnlyList<AlphaConfig> Alphas { get; init; }

    /// <summary>"testnet" or "mainnet". Mainnet additionally requires the confirmation flag below.</summary>
    public string Environment { get; init; } = "testnet";

    /// <summary>
    /// Must be explicitly true to trade against mainnet. The worker refuses to
    /// start otherwise (spec 3.1, mainnet confirmation gate) — a config that
    /// merely says "mainnet" is not consent.
    /// </summary>
    public bool ConfirmMainnet { get; init; }

    /// <summary>Aggregate ceiling across every alpha in this session, as a fraction of equity.</summary>
    public decimal MaxAggregateExposureFraction { get; init; } = 0.60m;

    /// <summary>Binance order rate budget shared by every alpha in the process.</summary>
    public int MaxOrdersPerTenSeconds { get; init; } = 40;

    public DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;
}
