namespace Parallels.Contracts;

/// <summary>Kind of editor the frontend should render for a parameter.</summary>
public static class ParameterKinds
{
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Fraction = "fraction";
}

/// <summary>One editable field of a strategy's parameter set.</summary>
/// <param name="Name">Property name, matching the JSON member exactly (camelCase applied by the serializer).</param>
public sealed record ParameterField(
    string Name,
    string Label,
    string Kind,
    decimal Default,
    decimal? Min = null,
    decimal? Max = null,
    decimal? Step = null,
    string? Help = null);

/// <summary>A strategy type and everything the UI needs to render a form for it.</summary>
public sealed record StrategyDescriptor(
    string StrategyType,
    string DisplayName,
    string Description,
    IReadOnlyList<ParameterField> Fields);

/// <summary>
/// The schema the Backtest tab renders its parameter fields from (spec 4.2) and
/// the Live tab reuses when adding an alpha.
///
/// Keeping this in Contracts rather than hardcoding a form in the React app is
/// what makes "config-driven parameters, never hardcoded" (spec 5) true on both
/// sides of the wire: adding a strategy adds a descriptor here, and the UI grows
/// a form for it without a frontend change.
/// </summary>
public static class StrategyCatalog
{
    public static IReadOnlyList<StrategyDescriptor> All { get; } =
    [
        new StrategyDescriptor(
            StrategyTypes.TrendFollowing,
            "Trend Following (EMA cross + ATR stop)",
            "Long when the fast EMA is above the slow EMA, flat otherwise. Stop and target are ATR multiples; size is set so the stop distance costs the configured fraction of equity.",
            [
                new ParameterField("fastPeriod", "Fast EMA period", ParameterKinds.Integer, 20, 1, 500, 1,
                    "Bars of the selected resolution."),
                new ParameterField("slowPeriod", "Slow EMA period", ParameterKinds.Integer, 60, 2, 1000, 1,
                    "Must be greater than the fast period."),
                new ParameterField("atrPeriod", "ATR period", ParameterKinds.Integer, 14, 1, 500, 1,
                    "Drives both stop distance and position size."),
                new ParameterField("atrStopMultiple", "Stop (× ATR)", ParameterKinds.Decimal, 2.0m, 0.1m, 20m, 0.1m),
                new ParameterField("atrTakeProfitMultiple", "Target (× ATR)", ParameterKinds.Decimal, 4.0m, 0.1m, 50m, 0.1m),
                new ParameterField("riskPerTradeFraction", "Risk per trade", ParameterKinds.Fraction, 0.01m, 0.0001m, 0.5m, 0.0025m,
                    "Fraction of equity lost if the stop is hit."),
                new ParameterField("maxPositionFraction", "Max position size", ParameterKinds.Fraction, 0.25m, 0.01m, 1m, 0.01m,
                    "Ceiling on position value as a fraction of equity."),
                new ParameterField("minCrossSeparationFraction", "Min EMA separation", ParameterKinds.Fraction, 0m, 0m, 0.5m, 0.0005m,
                    "Ignore crosses where the EMAs are closer together than this fraction of price."),
            ]),
    ];

    public static StrategyDescriptor? Find(string strategyType) =>
        All.FirstOrDefault(d => string.Equals(d.StrategyType, strategyType, StringComparison.OrdinalIgnoreCase));
}
