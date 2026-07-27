using Parallels.Worker.Live.Binance.Risk;
using QuantConnect.Algorithm.Framework.Portfolio;

namespace Parallels.Tests;

/// <summary>
/// Phase 3's central claim: two alphas on one account share one exposure budget,
/// and the second's sizing is visibly reduced by what the first has committed.
/// </summary>
public class RiskGatewayTests
{
    private static RiskGateway Build(decimal aggregate = 0.60m) => new(
        symbolToAlpha: new Dictionary<string, string> { ["BTCUSDT"] = "alpha-btc", ["ETHUSDT"] = "alpha-eth" },
        perAlphaLimits: new Dictionary<string, decimal> { ["alpha-btc"] = 0.50m, ["alpha-eth"] = 0.50m },
        maxAggregateFraction: aggregate);

    [Fact]
    public void SecondAlphaIsReducedByTheFirstAlphasCommittedExposure()
    {
        // 100,000 USDT equity, aggregate cap 60% = 60,000.
        var fixture = new AlgorithmFixture(cash: 100_000m, price: 50_000m);
        var eth = fixture.Add("ETHUSDT", 2_500m);
        var gateway = Build();

        // Alpha 1 commits 50,000 (1 BTC) — inside its own 50% limit.
        var first = gateway.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1m));
        Assert.True(first.Approved);
        Assert.Equal(1m, first.ApprovedQuantity);

        // Alpha 2 asks for 20 ETH = 50,000, which alone is inside its own limit.
        // Only 10,000 of aggregate budget remains, so it must be scaled to 4 ETH.
        var second = gateway.Evaluate(fixture.Algorithm, new PortfolioTarget(eth.Symbol, 20m));

        Assert.True(second.Approved);
        Assert.Equal(4m, second.ApprovedQuantity);
        Assert.Contains("aggregate", second.Reason);
        Assert.Contains("alpha-eth", second.Reason);
    }

    [Fact]
    public void AlphaIsRefusedOutrightWhenOthersHaveConsumedTheWholeBudget()
    {
        var fixture = new AlgorithmFixture(cash: 100_000m, price: 50_000m);
        var eth = fixture.Add("ETHUSDT", 2_500m);
        var gateway = Build(aggregate: 0.50m);

        // First alpha takes the entire 50,000 aggregate budget.
        Assert.True(gateway.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1m)).Approved);

        var second = gateway.Evaluate(fixture.Algorithm, new PortfolioTarget(eth.Symbol, 4m));

        Assert.False(second.Approved);
        Assert.Contains("aggregate limit", second.Reason);
    }

    [Fact]
    public void PerAlphaCeilingAppliesEvenWhenAggregateBudgetIsFree()
    {
        var fixture = new AlgorithmFixture(cash: 100_000m, price: 50_000m);
        var gateway = Build();

        // Nothing else is committed, but alpha-btc's own ceiling is 50% = 50,000,
        // so a request for 1.5 BTC (75,000) is cut back to 1 BTC.
        var decision = gateway.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1.5m));

        Assert.True(decision.Approved);
        Assert.Equal(1m, decision.ApprovedQuantity);
        Assert.Contains("per-alpha", decision.Reason);
    }

    [Fact]
    public void ReducingAPositionIsNeverGated()
    {
        // Gating an exit on an exposure limit would make a breach unrecoverable:
        // the position could never be cut back below the limit it exceeds.
        var fixture = new AlgorithmFixture(cash: 100_000m, price: 50_000m);
        fixture.SetHoldings(fixture.Symbol, 2m, 50_000m);
        var gateway = Build(aggregate: 0.01m);

        var decision = gateway.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 0m));

        Assert.True(decision.Approved);
        Assert.Equal(0m, decision.ApprovedQuantity);
    }

    [Fact]
    public void ExposureIsRederivedFromRealHoldingsOnRestart()
    {
        // Reconciliation on restart re-reads the exchange; the in-memory view has
        // to start from that rather than from zero, or a restarted session would
        // believe the whole budget is free while positions are still open.
        var fixture = new AlgorithmFixture(cash: 100_000m, price: 50_000m);
        fixture.SetHoldings(fixture.Symbol, 0.5m, 50_000m);

        var gateway = Build();
        gateway.ResetFromPortfolio(fixture.Algorithm);

        Assert.Equal(25_000m, gateway.CommittedByAlpha["alpha-btc"]);
    }

    [Fact]
    public void ConcurrentEvaluationsCannotBothClaimTheSameBudget()
    {
        // Check-then-reserve must be atomic. If it were a read followed by a
        // separate write, two alphas evaluating at once would each be told the
        // full remaining budget was available.
        var fixture = new AlgorithmFixture(cash: 100_000m, price: 50_000m);
        var eth = fixture.Add("ETHUSDT", 2_500m);
        var gateway = Build();

        var approved = new decimal[2];
        Parallel.Invoke(
            () => approved[0] = Claim(gateway, fixture, new PortfolioTarget(fixture.Symbol, 1m), 50_000m),
            () => approved[1] = Claim(gateway, fixture, new PortfolioTarget(eth.Symbol, 20m), 50_000m));

        // Whatever the interleaving, the two together cannot exceed the 60,000 cap.
        Assert.True(approved[0] + approved[1] <= 60_000m + 0.01m,
            $"combined committed notional {approved[0] + approved[1]} exceeded the 60,000 aggregate cap");
    }

    private static decimal Claim(RiskGateway gateway, AlgorithmFixture fixture, PortfolioTarget target, decimal fullNotional)
    {
        var decision = gateway.Evaluate(fixture.Algorithm, target);
        if (!decision.Approved) return 0m;
        return fullNotional * (decision.ApprovedQuantity / target.Quantity);
    }
}
