using Parallels.Contracts;
using Parallels.Strategies;

namespace Parallels.Tests;

/// <summary>
/// Contracts are the one typed representation that crosses both the HTTP wire
/// and the process boundary (spec 3.5), so their round-trip behaviour is load
/// bearing rather than incidental.
/// </summary>
public class ContractsTests
{
    [Fact]
    public void AJobRoundTripsThroughJsonWithItsStrategyTypeIntact()
    {
        // This is the exact hop from the API into a worker's --job-stdin. If the
        // discriminator were lost, the worker would deserialize a base
        // StrategyParameters and fail to construct any model.
        var job = new BacktestJob
        {
            JobId = "job-1",
            Symbol = "BTCUSDT",
            StartDate = new DateOnly(2023, 1, 15),
            EndDate = new DateOnly(2024, 12, 30),
            Parameters = new TrendFollowingParameters { FastPeriod = 12, SlowPeriod = 48 },
        };

        var restored = ParallelsJson.Deserialize<BacktestJob>(ParallelsJson.Serialize(job));

        Assert.NotNull(restored);
        var parameters = Assert.IsType<TrendFollowingParameters>(restored.Parameters);
        Assert.Equal(12, parameters.FastPeriod);
        Assert.Equal(48, parameters.SlowPeriod);
        Assert.Equal(new DateOnly(2023, 1, 15), restored.StartDate);
    }

    [Fact]
    public void DatesSerializeAsPlainIsoStringsForTheTypeScriptSide()
    {
        var job = new BacktestJob
        {
            JobId = "job-1",
            Symbol = "BTCUSDT",
            StartDate = new DateOnly(2023, 1, 15),
            EndDate = new DateOnly(2024, 12, 30),
            Parameters = new TrendFollowingParameters(),
        };

        Assert.Contains("\"startDate\":\"2023-01-15\"", ParallelsJson.Serialize(job));
    }

    [Theory]
    [InlineData(60, 20, "FastPeriod must be strictly less than SlowPeriod.")]
    [InlineData(0, 60, "FastPeriod must be >= 1.")]
    public void InvalidParameterCombinationsAreRejected(int fast, int slow, string expected)
    {
        var parameters = new TrendFollowingParameters { FastPeriod = fast, SlowPeriod = slow };
        Assert.Contains(expected, parameters.Validate());
    }

    [Fact]
    public void AJobEndingBeforeItStartsIsRejected()
    {
        var job = new BacktestJob
        {
            JobId = "j",
            Symbol = "BTCUSDT",
            StartDate = new DateOnly(2024, 6, 1),
            EndDate = new DateOnly(2024, 1, 1),
            Parameters = new TrendFollowingParameters(),
        };

        Assert.Contains("EndDate must be after StartDate.", job.Validate());
    }

    [Fact]
    public void EveryCatalogFieldMatchesARealParameterProperty()
    {
        // The frontend renders its form from this catalog and posts the field
        // names straight back. A typo here would produce a form whose values
        // silently never reach the strategy.
        var descriptor = StrategyCatalog.Find(StrategyTypes.TrendFollowing);
        Assert.NotNull(descriptor);

        var properties = typeof(TrendFollowingParameters)
            .GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet();

        foreach (var field in descriptor.Fields)
            Assert.Contains(field.Name, properties);
    }

    [Fact]
    public void ABacktestSessionExposesExactlyOneAlpha()
    {
        // Spec 3.3: backtest is exactly one configuration, live is however many
        // are enabled — the algorithm host iterates the same shape either way.
        var session = new ParallelsSession
        {
            Mode = SessionMode.Backtest,
            OutputDirectory = "/tmp",
            Job = new BacktestJob
            {
                JobId = "job-1",
                Symbol = "BTCUSDT",
                StartDate = new DateOnly(2024, 1, 1),
                EndDate = new DateOnly(2024, 2, 1),
                Parameters = new TrendFollowingParameters(),
            },
        };

        var alpha = Assert.Single(session.Alphas);
        Assert.Equal("job-1", alpha.Id);
        Assert.Equal(["BTCUSDT"], alpha.Symbols);
    }
}

public class SessionContextTests
{
    [Fact]
    public void PublishingASecondSessionInOneProcessThrows()
    {
        // One process is one LEAN engine session: Composer, Config and the
        // handler singletons are process-wide, so a second publish means a second
        // session in a process that cannot support one. Failing loudly here turns
        // that into an obvious error rather than a subtly wrong run.
        SessionContext.ResetForTests();

        var session = new ParallelsSession
        {
            Mode = SessionMode.Backtest,
            OutputDirectory = "/tmp",
            Job = new BacktestJob
            {
                JobId = "job-1",
                Symbol = "BTCUSDT",
                StartDate = new DateOnly(2024, 1, 1),
                EndDate = new DateOnly(2024, 2, 1),
                Parameters = new TrendFollowingParameters(),
            },
        };

        SessionContext.Publish(session);

        var ex = Assert.Throws<InvalidOperationException>(() => SessionContext.Publish(session));
        Assert.Contains("One process = one LEAN engine session", ex.Message);

        SessionContext.ResetForTests();
    }
}
