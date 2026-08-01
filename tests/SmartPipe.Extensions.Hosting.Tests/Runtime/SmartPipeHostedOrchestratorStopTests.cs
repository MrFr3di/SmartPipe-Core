using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.Hosting.Tests.Fakes;

namespace SmartPipe.Extensions.Hosting.Tests.Runtime;

[Trait("Category", "HostingLifecycle")]
public sealed class SmartPipeHostedOrchestratorStopTests
{
    [Fact]
    public async Task StopAsync_CleansRunsInExactReverseStartupOrder()
    {
        var calls = new List<string>();
        var runs = new[]
        {
            CreateRun("a", calls),
            CreateRun("b", calls),
            CreateRun("c", calls),
        };
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(lifetime, runs);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        await orchestrator.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["c:drain", "c:dispose", "b:drain", "b:dispose", "a:drain", "a:dispose"],
            calls);
        Assert.Equal(HostedOrchestratorState.Stopped, orchestrator.State);
    }

    [Fact]
    public async Task StopAsync_AttemptsAllRunsAndAggregatesReverseRunActionOrder()
    {
        var calls = new List<string>();
        var cDrain = new InvalidOperationException("c-drain");
        var cAbort = new InvalidOperationException("c-abort");
        var cDispose = new InvalidOperationException("c-dispose");
        var bDispose = new InvalidOperationException("b-dispose");
        var a = CreateRun("a", calls);
        var b = CreateRun("b", calls);
        b.DisposeError = bDispose;
        var c = CreateRun("c", calls);
        c.DrainError = cDrain;
        c.AbortError = cAbort;
        c.DisposeError = cDispose;
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(lifetime, [a, b, c]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            orchestrator.StopAsync(TestContext.Current.CancellationToken));

        Assert.Equal([cDrain, cAbort, cDispose, bDispose], aggregate.InnerExceptions);
        Assert.Contains("a:dispose", calls);
        Assert.Equal(HostedOrchestratorState.Faulted, orchestrator.State);
    }

    [Fact]
    public async Task StopAsync_PreCancelledTokenSkipsDrainButAbortsAndDisposesEveryRun()
    {
        var calls = new List<string>();
        var runs = new[] { CreateRun("a", calls), CreateRun("b", calls) };
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(lifetime, runs);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.StopAsync(cancellation.Token));

        Assert.Equal(["b:abort", "b:dispose", "a:abort", "a:dispose"], calls);
        Assert.All(runs, run => Assert.Equal(CancellationToken.None, run.AbortToken));
    }

    [Fact]
    public async Task StopAsync_PassesInfiniteDrainTimeoutExactly()
    {
        var run = new ControlledHostedRun("orders");
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            [run],
            drainTimeout: Timeout.InfiniteTimeSpan);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        await orchestrator.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Timeout.InfiniteTimeSpan, run.DrainTimeout);
    }

    [Fact]
    public async Task StopAsync_QuiescesMonitorBeforeRunCleanup()
    {
        var run = new ControlledHostedRun("orders")
        {
            Completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task,
        };
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(lifetime, [run]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        run.CallObserver = action =>
        {
            if (action == "orders:drain")
                Assert.True(orchestrator.ExecuteTask!.IsCompleted);
        };

        await orchestrator.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["drain", "dispose"], run.Calls);
    }

    [Fact]
    public async Task ConcurrentAndRepeatedStopAsyncCallers_ShareOneCleanupTask()
    {
        var run = new ControlledHostedRun("orders");
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(lifetime, [run]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        var stops = Enumerable.Range(0, 32)
            .Select(_ => orchestrator.StopAsync(TestContext.Current.CancellationToken))
            .ToArray();

        Assert.All(stops, task => Assert.Same(stops[0], task));
        await Task.WhenAll(stops);
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["drain", "dispose"], run.Calls);
    }

    [Fact]
    public async Task StopRacingFinalStartupReadiness_WaitsThenCleansOnce()
    {
        var ready = new TaskCompletionSource<IHostedPipelineRun>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ControlledHostedRun("orders");
        using var lifetime = new RecordingHostApplicationLifetime();
        var registration = CreateRegistration(
            run,
            _ => ready.Task,
            TimeSpan.FromSeconds(30));
        using var orchestrator = new SmartPipeHostedOrchestrator(
            [registration],
            lifetime,
            NullLogger<SmartPipeHostedOrchestrator>.Instance);

        var start = orchestrator.StartAsync(TestContext.Current.CancellationToken);
        var stop = orchestrator.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);

        ready.SetResult(run);
        await Task.WhenAll(start, stop);

        Assert.Equal(["drain", "dispose"], run.Calls);
        Assert.Equal(HostedOrchestratorState.Stopped, orchestrator.State);
    }

    [Fact]
    public async Task StopRacingStartupFailureReportsPrimaryOnceAndDoesNotRepeatCleanup()
    {
        var ready = new TaskCompletionSource<IHostedPipelineRun>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("start failed");
        var started = new ControlledHostedRun("started");
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = new SmartPipeHostedOrchestrator(
            [
                CreateRegistration(
                    started,
                    _ => Task.FromResult<IHostedPipelineRun>(started),
                    TimeSpan.FromSeconds(30)),
                CreateRegistration(
                    new ControlledHostedRun("failing"),
                    _ => ready.Task,
                    TimeSpan.FromSeconds(30),
                    registrationOrder: 1),
            ],
            lifetime,
            NullLogger<SmartPipeHostedOrchestrator>.Instance);

        var start = orchestrator.StartAsync(TestContext.Current.CancellationToken);
        var stop = orchestrator.StopAsync(TestContext.Current.CancellationToken);
        ready.SetException(primary);

        var startError = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        var stopError = await Assert.ThrowsAsync<InvalidOperationException>(() => stop);

        Assert.Same(primary, startError);
        Assert.Same(primary, stopError);
        Assert.Equal(["abort", "dispose"], started.Calls);
    }

    [Fact]
    public async Task RunFaultRacingStopNeverRequestsApplicationStop()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var run = new ControlledHostedRun($"orders-{iteration}")
            {
                Completion = completion.Task,
            };
            using var lifetime = new RecordingHostApplicationLifetime();
            using var orchestrator = CreateOrchestrator(lifetime, [run]);
            await orchestrator.StartAsync(TestContext.Current.CancellationToken);

            var stop = orchestrator.StopAsync(TestContext.Current.CancellationToken);
            completion.SetException(new InvalidOperationException("stopping"));
            await stop;

            Assert.Equal(0, lifetime.StopApplicationCalls);
        }
    }

    [Fact]
    public async Task Dispose_DoesNotOwnAsyncCleanupAndStartAfterStopIsRejected()
    {
        var run = new ControlledHostedRun("orders");
        using var lifetime = new RecordingHostApplicationLifetime();
        var orchestrator = CreateOrchestrator(lifetime, [run]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        orchestrator.Dispose();
        Assert.Empty(run.Calls);
        await orchestrator.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["drain", "dispose"], run.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartAsync(TestContext.Current.CancellationToken));
    }

    private static SmartPipeHostedOrchestrator CreateOrchestrator(
        RecordingHostApplicationLifetime lifetime,
        IEnumerable<ControlledHostedRun> runs,
        TimeSpan? drainTimeout = null) =>
        new(
            runs.Select((run, index) => CreateRegistration(
                run,
                _ => Task.FromResult<IHostedPipelineRun>(run),
                drainTimeout ?? TimeSpan.FromSeconds(30),
                index)),
            lifetime,
            NullLogger<SmartPipeHostedOrchestrator>.Instance);

    private static ControlledHostedRegistration CreateRegistration(
        ControlledHostedRun run,
        Func<CancellationToken, Task<IHostedPipelineRun>> start,
        TimeSpan drainTimeout,
        int registrationOrder = 0) =>
        new(
            new HostedPipelineDescriptor
            {
                Key = run.Key,
                InputType = typeof(int),
                OutputType = typeof(int),
                Order = 0,
                RegistrationOrder = registrationOrder,
                DrainTimeout = drainTimeout,
                FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication,
                CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive,
            },
            start);

    private static ControlledHostedRun CreateRun(string key, List<string> calls) =>
        new(key) { CallObserver = calls.Add };
}
