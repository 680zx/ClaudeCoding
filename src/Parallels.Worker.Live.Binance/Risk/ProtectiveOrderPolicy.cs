using Parallels.Strategies;
using Parallels.Strategies.Execution;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Orders;

namespace Parallels.Worker.Live.Binance.Risk;

/// <summary>
/// Keeps a resting stop and take-profit on the exchange for every open position.
///
/// Live only, and the reason is worth being explicit about: a stop that lives
/// inside this process protects nothing if the process dies. Resting orders sit
/// on Binance and execute whether or not this worker is alive, which is the
/// whole point of placing them.
///
/// Binance spot has no native OCO through LEAN's brokerage here, so the pair is
/// maintained manually: when one side fills or the position closes, the sibling
/// is cancelled. Without that, a filled take-profit would leave a stop order
/// behind that later sells a position the account no longer holds.
/// </summary>
public sealed class ProtectiveOrderPolicy(Action<string> log) : IProtectiveOrderPolicy
{
    private readonly Dictionary<Symbol, ProtectivePair> _open = [];

    private sealed record ProtectivePair(int StopOrderId, int TargetOrderId);

    public void OnFill(QCAlgorithm algorithm, OrderEvent fill)
    {
        if (fill.Status != OrderStatus.Filled) return;

        var symbol = fill.Symbol;
        var holdings = algorithm.Portfolio[symbol].Quantity;

        // The position is flat: whatever protective orders remain are now
        // orphaned and must come off the book.
        if (holdings == 0m)
        {
            CancelPair(algorithm, symbol);
            return;
        }

        // A protective order filling is itself a fill; it closed part of the
        // position, so re-derive rather than stacking another pair on top.
        CancelPair(algorithm, symbol);

        if (algorithm is not ParallelsAlgorithm parallels) return;
        if (!parallels.TryGetProtectiveLevels(symbol, out var stopPrice, out var targetPrice)) return;
        if (stopPrice <= 0m || targetPrice <= 0m) return;

        var security = algorithm.Securities[symbol];
        var tickSize = security.SymbolProperties.MinimumPriceVariation;

        // Round away from the position: the stop rounds down and the target
        // rounds up, so tick rounding can never pull either level tighter than
        // the model intended.
        var stop = ExchangeFilterGate.RoundToTickSize(stopPrice, tickSize, roundUp: false);
        var target = ExchangeFilterGate.RoundToTickSize(targetPrice, tickSize, roundUp: true);

        var quantity = -holdings;

        try
        {
            var stopTicket = algorithm.StopMarketOrder(symbol, quantity, stop, tag: "protective-stop");
            var targetTicket = algorithm.LimitOrder(symbol, quantity, target, tag: "protective-target");

            _open[symbol] = new ProtectivePair(stopTicket.OrderId, targetTicket.OrderId);

            log($"[protective] {symbol.Value}: stop {stop} / target {target} on {holdings} units " +
                $"(orders {stopTicket.OrderId}, {targetTicket.OrderId})");
        }
        catch (Exception ex)
        {
            log($"[protective] FAILED to place protective orders for {symbol.Value}: {ex.Message}");
            throw;
        }
    }

    private void CancelPair(QCAlgorithm algorithm, Symbol symbol)
    {
        if (!_open.Remove(symbol, out var pair)) return;

        foreach (var orderId in new[] { pair.StopOrderId, pair.TargetOrderId })
        {
            var ticket = algorithm.Transactions.GetOrderTicket(orderId);
            if (ticket is null) continue;
            if (ticket.Status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Invalid) continue;

            ticket.Cancel("superseded protective order");
        }
    }
}
