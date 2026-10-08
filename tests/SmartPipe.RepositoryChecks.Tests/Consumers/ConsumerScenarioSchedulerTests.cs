using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Tests.Consumers;

public sealed class ConsumerScenarioSchedulerTests
{
    [Fact]
    public async Task ParallelWorkers_PreserveManifestOrderAndBoundActiveScenarios()
    {
        var started = Signal();
        var firstRelease = Signal();
        var secondRelease = Signal();
        var calls = 0;
        var active = 0;
        var scenarios = new[] { Scenario("first"), Scenario("second") };
        var run = ConsumerScenarioScheduler.RunAsync(scenarios, 2, async (scenario, ct) =>
        {
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref calls) == 2) started.TrySetResult();
            try
            {
                await (scenario.Id == "first" ? firstRelease.Task : secondRelease.Task).WaitAsync(ct);
                return Result(scenario);
            }
            finally { Interlocked.Decrement(ref active); }
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, Volatile.Read(ref active));
            secondRelease.TrySetResult();
            Assert.False(run.IsCompleted);
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); }

        var results = await run;
        Assert.Equal(["first", "second"], results.Select(result => result.Scenario));
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task NativeAotScenarios_AreSerializedAndReleasedAfterCompletion()
    {
        var firstEntered = Signal();
        var release = Signal();
        var active = 0;
        var peak = 0;
        var scenarios = Enumerable.Range(0, 4).Select(index => Scenario($"aot-{index}", ConsumerMode.PublishNativeAot)).ToArray();
        var run = ConsumerScenarioScheduler.RunAsync(scenarios, 4, async (scenario, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            firstEntered.TrySetResult();
            try { await release.Task.WaitAsync(ct); return Result(scenario); }
            finally { Interlocked.Decrement(ref active); }
        }, TestContext.Current.CancellationToken);
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref active));
        }
        finally { release.TrySetResult(); }

        Assert.Equal(4, (await run).Count);
        Assert.Equal(1, peak);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task Failure_CancelsAndDrainsSiblingBeforeRethrowingOriginal()
    {
        var siblingEntered = Signal();
        var canceled = Signal();
        var cleanupRelease = Signal();
        var original = new InvalidOperationException("scenario failed");
        var run = ConsumerScenarioScheduler.RunAsync([Scenario("failure"), Scenario("sibling")], 2, async (scenario, ct) =>
        {
            if (scenario.Id == "failure")
            {
                await siblingEntered.Task.WaitAsync(ct);
                throw original;
            }
            siblingEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Result(scenario); }
            finally
            {
                canceled.TrySetResult();
                await cleanupRelease.Task;
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
        }
        finally { cleanupRelease.TrySetResult(); }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Same(original, error);
    }

    [Fact]
    public async Task ProcessHostFailure_ReportsScenarioPhaseAndRetainsCause()
    {
        var cause = new ProcessRunnerException(
            ProcessFailureKind.StartFailure,
            "Repository-check process host did not complete its authenticated control protocol (phase: wait-target-exit; failure: ProcessHostProtocolException).");
        var error = await Assert.ThrowsAsync<ConsumerScenarioException>(
            () => ConsumerScenarioScheduler.RunAsync(
                [Scenario("otel-host-failure")],
                1,
                (_, _) => Task.FromException<ConsumerScenarioResult>(cause),
                TestContext.Current.CancellationToken));

        Assert.Equal("SPCONS030", error.Code);
        Assert.Contains("otel-host-failure", error.Message, StringComparison.Ordinal);
        Assert.Contains("phase: wait-target-exit", error.Message, StringComparison.Ordinal);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public async Task SerialOverride_DoesNotStartNextScenarioBeforePreviousCompletes()
    {
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        var run = ConsumerScenarioScheduler.RunAsync([Scenario("first"), Scenario("second")], 1, async (scenario, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Result(scenario);
        }, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(2, (await run).Count);
        Assert.Equal(2, calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ConsumerScenarioResult Result(ConsumerScenario scenario) => new(1, scenario.Id, "passed", "2.2.0", true, 0, [], []);

    private static ConsumerScenario Scenario(string id, ConsumerMode mode = ConsumerMode.BuildAndRun) => new()
    {
        Id = id,
        Set = "current",
        Mode = mode,
        TemplatePath = "unused",
        PackageIds = [],
        ExpectedSmartPipeDependencies = [],
        ForbiddenDependencies = [],
        Timeout = TimeSpan.FromMinutes(1),
        RunSecondLockedRestore = false,
    };
}
