using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Orders;

namespace Parallels.Strategies.Execution;

/// <summary>
/// Rounds a target to something the exchange would actually accept: position
/// quantity to the lot-size step, and refusal when the resulting <em>order</em>
/// would fall under the venue's minimum notional.
///
/// This runs in <em>backtest as well as live</em> (spec 5). If it only ran live,
/// a backtest could fill 0.00037 BTC orders that Binance would have rejected
/// outright, and the two would stop being comparable — the backtest would be
/// reporting edge that could never have been captured.
/// </summary>
public sealed class ExchangeFilterGate : IOrderGate
{
    public GateDecision Evaluate(QCAlgorithm algorithm, IPortfolioTarget target)
    {
        var security = algorithm.Securities[target.Symbol];
        var properties = security.SymbolProperties;
        var holdings = algorithm.Portfolio[target.Symbol].Quantity;
        var requested = target.Quantity;

        // Closing a position is always allowed through untouched. The holding
        // being closed was itself acquired in exchange-legal size, so the
        // resulting sell is legal by construction — and refusing an exit because
        // it looks small would strand positions open indefinitely.
        if (requested == 0m) return GateDecision.Approve(0m);

        var rounded = RoundToLotSize(requested, properties.LotSize);
        if (rounded == 0m)
            return GateDecision.Reject(
                $"target {requested} rounds to zero at lot size {properties.LotSize}");

        // The venue minimum applies to the order that would be sent, which is the
        // delta against current holdings — not to the size of the resulting
        // position. Checking the position instead would wave through a 0.30 USDT
        // top-up simply because the position it adjusts is large.
        var delta = rounded - holdings;
        if (delta == 0m) return GateDecision.Approve(rounded);

        var price = security.Price;
        if (price > 0m && properties.MinimumOrderSize is { } minimumNotional)
        {
            var notional = Math.Abs(delta) * price;
            if (notional < minimumNotional)
                return GateDecision.Reject(
                    $"order notional {notional:F2} {properties.QuoteCurrency} is below the venue minimum {minimumNotional:F2}");
        }

        return rounded == requested
            ? GateDecision.Approve(rounded)
            : GateDecision.Reduce(rounded, $"rounded {requested} to lot size {properties.LotSize}");
    }

    public void OnOrderEvent(QCAlgorithm algorithm, OrderEvent orderEvent) { }

    /// <summary>
    /// Rounds toward zero, so filtering can only ever shrink a position request.
    /// Rounding away from zero would let the filter hand back a larger position
    /// than the risk gate approved, defeating the gate upstream of it.
    /// </summary>
    public static decimal RoundToLotSize(decimal quantity, decimal lotSize)
    {
        if (lotSize <= 0m) return quantity;
        var steps = decimal.Truncate(Math.Abs(quantity) / lotSize);
        var magnitude = steps * lotSize;
        return quantity < 0m ? -magnitude : magnitude;
    }

    /// <summary>Rounds a price to the venue's tick size, used for protective orders.</summary>
    public static decimal RoundToTickSize(decimal price, decimal tickSize, bool roundUp = false)
    {
        if (tickSize <= 0m) return price;
        var steps = price / tickSize;
        var rounded = roundUp ? Math.Ceiling(steps) : Math.Floor(steps);
        return rounded * tickSize;
    }
}
