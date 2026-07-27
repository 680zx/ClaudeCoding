using Parallels.Strategies.Alphas;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;

namespace Parallels.Tests;

/// <summary>
/// Hosting N alphas in one live process has one real cost: a single alpha's bug
/// can threaten every other alpha sharing the process. These cover the
/// mitigation (spec 3.2) — and its limits.
/// </summary>
public class FaultIsolationTests
{
    private sealed class ThrowingAlphaModel : IAlphaModel, INamedModel
    {
        public string Name => "throwing";
        public int UpdateCalls { get; private set; }

        public IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            UpdateCalls++;
            throw new InvalidOperationException("deliberate fault");
        }

        public void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes) { }
    }

    private sealed class WorkingAlphaModel(Symbol symbol) : IAlphaModel, INamedModel
    {
        public string Name => "working";
        public int UpdateCalls { get; private set; }

        public IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            UpdateCalls++;
            return [Insight.Price(symbol, TimeSpan.FromHours(1), InsightDirection.Up, weight: 0.1)];
        }

        public void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes) { }
    }

    [Fact]
    public void AThrowingAlphaIsDisabledAndTheOthersKeepRunning()
    {
        var fixture = new AlgorithmFixture();
        var slice = new Slice(fixture.Algorithm.Time, [], fixture.Algorithm.Time);

        var faults = new List<string>();
        var throwing = new ThrowingAlphaModel();
        var working = new WorkingAlphaModel(fixture.Symbol);

        var wrappedThrowing = new FaultIsolatingAlphaModel(
            throwing, "alpha-bad", onFault: (id, _) => faults.Add(id));
        var wrappedWorking = new FaultIsolatingAlphaModel(working, "alpha-good");

        // Three bars: the faulty alpha throws on the first and is disabled; the
        // healthy one is untouched throughout.
        for (var bar = 0; bar < 3; bar++)
        {
            Assert.Empty(wrappedThrowing.Update(fixture.Algorithm, slice));
            Assert.Single(wrappedWorking.Update(fixture.Algorithm, slice));
        }

        Assert.False(wrappedThrowing.IsEnabled);
        Assert.IsType<InvalidOperationException>(wrappedThrowing.FaultedWith);
        Assert.Equal(["alpha-bad"], faults);

        // Disabled means disabled: the faulty model is not called again, so a
        // model that throws on every bar cannot flood the log.
        Assert.Equal(1, throwing.UpdateCalls);

        Assert.True(wrappedWorking.IsEnabled);
        Assert.Equal(3, working.UpdateCalls);
    }

    [Fact]
    public void ExceptionsFromDeferredIterationAreStillCaught()
    {
        // An alpha model written with yield return is an iterator: its body does
        // not run until enumerated. If the wrapper returned the iterator
        // unevaluated, the exception would surface inside LEAN's own enumeration
        // instead of here — which is exactly the case this must not miss.
        var fixture = new AlgorithmFixture();
        var slice = new Slice(fixture.Algorithm.Time, [], fixture.Algorithm.Time);

        var wrapped = new FaultIsolatingAlphaModel(new LazyThrowingAlphaModel(), "alpha-lazy");

        var insights = wrapped.Update(fixture.Algorithm, slice);

        Assert.Empty(insights);
        Assert.False(wrapped.IsEnabled);
    }

    private sealed class LazyThrowingAlphaModel : IAlphaModel, INamedModel
    {
        public string Name => "lazy-throwing";

        public IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            foreach (var insight in Generate()) yield return insight;
        }

        private static IEnumerable<Insight> Generate()
        {
            throw new InvalidOperationException("thrown during enumeration");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes) { }
    }

    [Fact]
    public void InsightCountsAreTrackedPerAlpha()
    {
        var fixture = new AlgorithmFixture();
        var slice = new Slice(fixture.Algorithm.Time, [], fixture.Algorithm.Time);
        var wrapped = new FaultIsolatingAlphaModel(new WorkingAlphaModel(fixture.Symbol), "alpha-good");

        for (var i = 0; i < 4; i++) wrapped.Update(fixture.Algorithm, slice);

        Assert.Equal(4, wrapped.EmittedInsightCount);
    }
}
