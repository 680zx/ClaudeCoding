using Parallels.Contracts;
using QuantConnect;
using QuantConnect.Algorithm.Framework.Alphas;

namespace Parallels.Strategies.Alphas;

/// <summary>
/// Turns a persisted <see cref="AlphaConfig"/> into a live model instance.
///
/// This is the one place strategy type strings become types. Adding a strategy
/// means a parameter record in Contracts, a descriptor in the catalog, a model
/// here — and no change at all to the algorithm host, either worker, or the API.
/// </summary>
public static class AlphaModelFactory
{
    public static IAlphaModel Create(AlphaConfig config, Resolution resolution, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Parameters switch
        {
            TrendFollowingParameters p => new TrendFollowingAlphaModel(config.Id, p, resolution)
            {
                Verbose = verbose
            },

            null => throw new InvalidOperationException(
                $"Alpha '{config.Id}' has no parameters."),

            _ => throw new NotSupportedException(
                $"Alpha '{config.Id}' uses strategy type '{config.Parameters.StrategyType}', which has no " +
                $"registered model. Known types: {string.Join(", ", StrategyCatalog.All.Select(s => s.StrategyType))}.")
        };
    }

    /// <summary>Bars of warm-up the model needs before its first real decision.</summary>
    public static int WarmUpBarsFor(AlphaConfig config) => config.Parameters switch
    {
        TrendFollowingParameters p => Math.Max(p.SlowPeriod, p.AtrPeriod) + 1,
        _ => 100
    };
}
