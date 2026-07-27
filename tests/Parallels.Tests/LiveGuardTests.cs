using Parallels.Contracts;
using Parallels.Strategies.Execution;
using Parallels.Worker.Live.Binance;
using Parallels.Worker.Live.Binance.Risk;
using QuantConnect.Algorithm.Framework.Portfolio;

namespace Parallels.Tests;

public class IdempotencyGuardTests
{
    [Fact]
    public void ADuplicateSubmissionIsRefusedRatherThanDoubleExecuted()
    {
        // The Phase 2 requirement: the same intent submitted twice must be caught,
        // not filled twice. Live, a double fill is a real doubled position.
        var fixture = new AlgorithmFixture();
        var guard = new IdempotencyGuard(TimeSpan.FromMinutes(2));
        var target = new PortfolioTarget(fixture.Symbol, 1.5m);

        var first = guard.Evaluate(fixture.Algorithm, target);
        var duplicate = guard.Evaluate(fixture.Algorithm, target);

        Assert.True(first.Approved);
        Assert.False(duplicate.Approved);
        Assert.Contains("duplicate submission", duplicate.Reason);
    }

    [Fact]
    public void ADifferentSizeForTheSameSymbolIsNotADuplicate()
    {
        var fixture = new AlgorithmFixture();
        var guard = new IdempotencyGuard(TimeSpan.FromMinutes(2));

        Assert.True(guard.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1.5m)).Approved);
        Assert.True(guard.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 2.0m)).Approved);
    }

    [Fact]
    public void TheSameIntentIsAllowedAgainOnceTheOrderSettles()
    {
        // Otherwise a genuine later re-entry at the same size would be blocked
        // forever by a guard that never releases.
        var fixture = new AlgorithmFixture();
        var guard = new IdempotencyGuard(TimeSpan.FromMinutes(2));
        var target = new PortfolioTarget(fixture.Symbol, 1.5m);

        guard.Evaluate(fixture.Algorithm, target);
        guard.OnOrderEvent(fixture.Algorithm, new QuantConnect.Orders.OrderEvent(
            1, fixture.Symbol, fixture.Algorithm.UtcTime, QuantConnect.Orders.OrderStatus.Filled,
            QuantConnect.Orders.OrderDirection.Buy, 50_000m, 1.5m,
            QuantConnect.Orders.Fees.OrderFee.Zero));

        Assert.Equal(0, guard.InFlightCount);
        Assert.True(guard.Evaluate(fixture.Algorithm, target).Approved);
    }
}

public class RateLimitGateTests
{
    [Fact]
    public void TheOrderBudgetIsSharedAcrossAlphasAndRefusesWhenExhausted()
    {
        // The budget belongs to the API key, not to a strategy: Binance counts
        // requests per account, so alphas that each stay under the limit alone
        // still rate-limit the account together.
        var fixture = new AlgorithmFixture();
        var eth = fixture.Add("ETHUSDT", 2_500m);
        var gate = new RateLimitGate(maxOrdersPerWindow: 3, window: TimeSpan.FromSeconds(10));

        Assert.True(gate.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1m)).Approved);
        Assert.True(gate.Evaluate(fixture.Algorithm, new PortfolioTarget(eth.Symbol, 2m)).Approved);
        Assert.True(gate.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 3m)).Approved);

        var refused = gate.Evaluate(fixture.Algorithm, new PortfolioTarget(eth.Symbol, 4m));

        Assert.False(refused.Approved);
        Assert.Contains("rate budget exhausted", refused.Reason);
    }

    [Fact]
    public void ANoOpTargetConsumesNoBudget()
    {
        // Nothing would be sent, so charging it against the rate budget would
        // starve orders that actually need to go out.
        var fixture = new AlgorithmFixture();
        fixture.SetHoldings(fixture.Symbol, 1m, 50_000m);
        var gate = new RateLimitGate(maxOrdersPerWindow: 1, window: TimeSpan.FromSeconds(10));

        gate.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1m));

        Assert.Equal(0, gate.UsedInWindow(fixture.Algorithm.UtcTime));
    }
}

public class MainnetGateTests
{
    private static string SessionJson(string environment, bool confirm) =>
        ParallelsJson.Serialize(new LiveSessionConfig
        {
            SessionId = "t",
            Environment = environment,
            ConfirmMainnet = confirm,
            Alphas =
            [
                new AlphaConfig
                {
                    Id = "a1",
                    Name = "n",
                    Symbols = ["BTCUSDT"],
                    Resolution = "Hour",
                    Enabled = true,
                    MaxExposureFraction = 0.25m,
                    Parameters = new TrendFollowingParameters { MinCrossSeparationFraction = 0.008m },
                }
            ],
        });

    [Fact]
    public void MainnetWithoutExplicitConfirmationIsRefused()
    {
        // Selecting mainnet is a destination, not consent. No amount of editing
        // stored desired state can promote a session to real funds by itself.
        using var _ = new ScopedEnvironment(ParallelsJson.LiveConfigEnvVar, SessionJson("mainnet", confirm: false));

        var ex = Assert.Throws<MainnetGateException>(() => LiveWorkerOptions.Parse([]));
        Assert.Contains("MAINNET", ex.Message);
        Assert.Contains("No orders have been placed", ex.Message);
    }

    [Fact]
    public void MainnetWithExplicitConfirmationIsAllowed()
    {
        using var _ = new ScopedEnvironment(ParallelsJson.LiveConfigEnvVar, SessionJson("mainnet", confirm: true));

        var options = LiveWorkerOptions.Parse([]);

        Assert.True(options.IsMainnet);
    }

    [Fact]
    public void TestnetNeedsNoConfirmation()
    {
        using var _ = new ScopedEnvironment(ParallelsJson.LiveConfigEnvVar, SessionJson("testnet", confirm: false));

        var options = LiveWorkerOptions.Parse([]);

        Assert.False(options.IsMainnet);
        Assert.Single(options.Session.Alphas);
    }

    [Fact]
    public void AMissingSessionIsRejectedRatherThanDefaulted()
    {
        using var _ = new ScopedEnvironment(ParallelsJson.LiveConfigEnvVar, null);

        var ex = Assert.Throws<ArgumentException>(() => LiveWorkerOptions.Parse([]));
        Assert.Contains(ParallelsJson.LiveConfigEnvVar, ex.Message);
    }

    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public ScopedEnvironment(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            // BINANCE_ENVIRONMENT would otherwise override what the session says.
            Environment.SetEnvironmentVariable("BINANCE_ENVIRONMENT", null);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

public class CompositeOrderGateTests
{
    [Fact]
    public void AFlattenInstructionSurvivesEveryGateInTheChain()
    {
        // The composite must not treat an approved zero as a refusal, or exits
        // would be swallowed by the chain rather than by any single gate.
        var fixture = new AlgorithmFixture();
        fixture.SetHoldings(fixture.Symbol, 1m, 50_000m);

        var chain = new CompositeOrderGate(
            new IdempotencyGuard(TimeSpan.FromMinutes(2)),
            new RateLimitGate(10, TimeSpan.FromSeconds(10)),
            new ExchangeFilterGate());

        var decision = chain.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 0m));

        Assert.True(decision.Approved);
        Assert.Equal(0m, decision.ApprovedQuantity);
    }

    [Fact]
    public void ARefusalAnywhereStopsTheChain()
    {
        var fixture = new AlgorithmFixture();
        var chain = new CompositeOrderGate(
            new RateLimitGate(0, TimeSpan.FromSeconds(10)),
            new ExchangeFilterGate());

        var decision = chain.Evaluate(fixture.Algorithm, new PortfolioTarget(fixture.Symbol, 1m));

        Assert.False(decision.Approved);
    }
}
