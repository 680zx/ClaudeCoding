using Parallels.Strategies.Execution;
using QuantConnect.Algorithm.Framework.Portfolio;

namespace Parallels.Tests;

/// <summary>
/// The exchange filter runs in backtest as well as live (spec 5), so these
/// assertions are about both.
/// </summary>
public class ExchangeFilterGateTests
{
    [Fact]
    public void ZeroTargetIsApprovedBecauseItMeansCloseThePosition()
    {
        // Regression for a defect that silently invalidated an entire backtest.
        // A zero target is a flatten instruction, but the gate originally
        // inferred rejection from a zero quantity and dropped every exit. The
        // run then reported a 100% win rate and a positive return, because a
        // position that never closes never books a loss.
        var fixture = new AlgorithmFixture();
        fixture.SetHoldings(fixture.Symbol, 1.5m, 45_000m);

        var decision = new ExchangeFilterGate()
            .Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 0m));

        Assert.True(decision.Approved);
        Assert.Equal(0m, decision.ApprovedQuantity);
    }

    [Fact]
    public void QuantityIsRoundedDownToBinanceLotSize()
    {
        var fixture = new AlgorithmFixture();

        // Binance BTCUSDT lot size is 0.00001; the trailing digits cannot be sent.
        var decision = new ExchangeFilterGate()
            .Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1.234567891m));

        Assert.True(decision.Approved);
        Assert.Equal(1.23456m, decision.ApprovedQuantity);
        Assert.Contains("lot size", decision.Reason);
    }

    [Fact]
    public void RoundingNeverIncreasesPositionSize()
    {
        // Rounding away from zero would hand back more than the risk gate
        // approved, defeating the gate that ran before this one.
        Assert.Equal(1.23456m, ExchangeFilterGate.RoundToLotSize(1.234569m, 0.00001m));
        Assert.Equal(-1.23456m, ExchangeFilterGate.RoundToLotSize(-1.234569m, 0.00001m));
    }

    [Fact]
    public void OrderBelowVenueMinimumNotionalIsRefused()
    {
        // At 50,000 USDT the smallest sendable order is 0.0001 BTC (5 USDT).
        // 0.00002 BTC is 1 USDT — Binance would reject it outright, so a
        // backtest must not fill it either.
        var fixture = new AlgorithmFixture();

        var decision = new ExchangeFilterGate()
            .Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 0.00002m));

        Assert.False(decision.Approved);
        Assert.Contains("below the venue minimum", decision.Reason);
    }

    [Fact]
    public void MinimumNotionalAppliesToTheOrderNotTheResultingPosition()
    {
        // Holding 1.0 BTC and targeting 1.00005 sends a 0.00005 BTC top-up —
        // 2.50 USDT, under Binance's 5 USDT minimum. The resulting position is
        // large, so checking the position instead of the delta would wave it
        // through. The delta is chosen to survive lot-size rounding: anything
        // finer than 0.00001 BTC rounds away before the notional test is reached.
        var fixture = new AlgorithmFixture();
        fixture.SetHoldings(fixture.Symbol, 1.0m, 50_000m);

        var decision = new ExchangeFilterGate()
            .Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1.00005m));

        Assert.False(decision.Approved);
    }

    [Fact]
    public void PriceRoundsToTickSizeAwayFromThePosition()
    {
        // A stop rounds down and a target rounds up, so tick rounding can never
        // pull a protective level tighter than the model intended.
        Assert.Equal(49_999.99m, ExchangeFilterGate.RoundToTickSize(49_999.994m, 0.01m));
        Assert.Equal(50_000.01m, ExchangeFilterGate.RoundToTickSize(50_000.004m, 0.01m, roundUp: true));
    }
}
