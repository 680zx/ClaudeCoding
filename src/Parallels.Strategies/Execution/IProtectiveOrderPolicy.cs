using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Orders;

namespace Parallels.Strategies.Execution;

/// <summary>
/// Supplies the protective levels an alpha decided on at entry.
///
/// The levels belong to the model that chose them — they are derived from its
/// own ATR state — so they are read back from it rather than recomputed
/// elsewhere. Recomputing would give a stop that drifts away from the one the
/// position was sized against, quietly changing the risk per trade.
/// </summary>
public interface IProtectiveLevelSource
{
    bool TryGetProtectiveLevels(Symbol symbol, out decimal stopPrice, out decimal targetPrice);
}

/// <summary>
/// Places and maintains resting protective orders after an entry fills.
///
/// Live-only. A backtest evaluates its stop against the bar close inside the
/// model (see <c>TrendFollowingAlphaModel</c>); live puts real stop and limit
/// orders on the exchange, so a position is protected even if this process dies.
/// That difference is deliberate and is one of the few places backtest and live
/// genuinely diverge.
/// </summary>
public interface IProtectiveOrderPolicy
{
    /// <summary>Called after any fill, with the algorithm's post-fill holdings already updated.</summary>
    void OnFill(QCAlgorithm algorithm, OrderEvent fill);
}
