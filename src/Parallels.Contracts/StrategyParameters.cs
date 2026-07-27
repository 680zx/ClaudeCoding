using System.Text.Json.Serialization;

namespace Parallels.Contracts;

/// <summary>
/// Base for every strategy's parameter set.
///
/// This is deliberately one type used everywhere: the browser posts it, the API
/// validates it, and the very same object is serialized into the worker
/// hand-off. Spec 3.5 — exactly one typed representation, validated once, no
/// API-only request model that then gets re-shaped before crossing the process
/// boundary.
///
/// Polymorphism is carried by the "strategyType" discriminator so a job or an
/// alpha config round-trips through JSON without the reader needing to know
/// which strategy it is in advance.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "strategyType",
                 UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TrendFollowingParameters), StrategyTypes.TrendFollowing)]
public abstract record StrategyParameters
{
    /// <summary>Discriminator value; must match the registered JSON derived-type tag.</summary>
    [JsonIgnore]
    public abstract string StrategyType { get; }

    /// <summary>
    /// Returns one message per invalid field, empty when the parameter set is
    /// usable. Validation lives on the contract rather than in the API so the
    /// worker enforces the same rules when it is run directly from a shell.
    /// </summary>
    public abstract IEnumerable<string> Validate();
}

/// <summary>Discriminator constants, shared by the catalog and the JSON attributes.</summary>
public static class StrategyTypes
{
    public const string TrendFollowing = "TrendFollowing";
}

/// <summary>
/// Dual moving-average trend following with an ATR-derived stop and target.
///
/// Sizing is expressed as a fraction of equity risked per trade rather than a
/// fixed quantity (spec 5): the distance to the stop is what converts that risk
/// budget into a position size, so a volatile regime automatically produces a
/// smaller position for the same risk.
/// </summary>
public sealed record TrendFollowingParameters : StrategyParameters
{
    [JsonIgnore]
    public override string StrategyType => StrategyTypes.TrendFollowing;

    /// <summary>Fast EMA period, in bars of the job's resolution.</summary>
    public int FastPeriod { get; init; } = 20;

    /// <summary>Slow EMA period, in bars of the job's resolution.</summary>
    public int SlowPeriod { get; init; } = 60;

    /// <summary>ATR lookback used for both stop distance and position sizing.</summary>
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Stop distance as a multiple of ATR.</summary>
    public decimal AtrStopMultiple { get; init; } = 2.0m;

    /// <summary>Take-profit distance as a multiple of ATR.</summary>
    public decimal AtrTakeProfitMultiple { get; init; } = 4.0m;

    /// <summary>Fraction of total equity risked per trade, e.g. 0.01 = 1%.</summary>
    public decimal RiskPerTradeFraction { get; init; } = 0.01m;

    /// <summary>Hard ceiling on position value as a fraction of equity, after risk sizing.</summary>
    public decimal MaxPositionFraction { get; init; } = 0.25m;

    /// <summary>
    /// Minimum separation between the two EMAs, as a fraction of price, before a
    /// cross is treated as a signal. Filters the chop that otherwise generates a
    /// long tail of near-zero-edge round trips.
    /// </summary>
    public decimal MinCrossSeparationFraction { get; init; } = 0.0m;

    public override IEnumerable<string> Validate()
    {
        if (FastPeriod < 1) yield return "FastPeriod must be >= 1.";
        if (SlowPeriod < 1) yield return "SlowPeriod must be >= 1.";
        if (FastPeriod >= SlowPeriod) yield return "FastPeriod must be strictly less than SlowPeriod.";
        if (AtrPeriod < 1) yield return "AtrPeriod must be >= 1.";
        if (AtrStopMultiple <= 0) yield return "AtrStopMultiple must be > 0.";
        if (AtrTakeProfitMultiple <= 0) yield return "AtrTakeProfitMultiple must be > 0.";
        if (RiskPerTradeFraction <= 0 || RiskPerTradeFraction > 0.5m)
            yield return "RiskPerTradeFraction must be in (0, 0.5].";
        if (MaxPositionFraction <= 0 || MaxPositionFraction > 1m)
            yield return "MaxPositionFraction must be in (0, 1].";
        if (MinCrossSeparationFraction < 0 || MinCrossSeparationFraction > 0.5m)
            yield return "MinCrossSeparationFraction must be in [0, 0.5].";
    }
}
