using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.Hosting.Tests.Fakes;

namespace SmartPipe.Extensions.Hosting.Tests.Runtime;

[Trait("Category", "HostingLifecycle")]
public sealed class SmartPipeHostedOrchestratorStartTests
{
    [Fact]
    public async Task StartAsync_WithNoRegistrations_ReachesRunningState()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        using var orchestrator = CreateOrchestrator(lifetime, []);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HostedOrchestratorState.Running, orchestrator.State);
    }

    [Fact]
    public async Task StartAsync_SortsAndWaitsForEachReadyRunSequentially()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        var starts = new List<string>();
        var cReady = NewRunGate();
        var bReady = NewRunGate();
        var aReady = NewRunGate();
        var bStarted = NewSignal();
        var aStarted = NewSignal();
        var registrations = new[]
        {
            CreateRegistration("a", order: 1, registrationOrder: 0, _ =>
            {
                starts.Add("a");
                aStarted.TrySetResult();
                return aReady.Task;
            }),
            CreateRegistration("b", order: 0, registrationOrder: 2, _ =>
            {
                starts.Add("b");
                bStarted.TrySetResult();
                return bReady.Task;
            }),
            CreateRegistration("c", order: 0, registrationOrder: 1, _ =>
            {
                starts.Add("c");
                return cReady.Task;
            }),
        };
        using var orchestrator = CreateOrchestrator(lifetime, registrations);

        var start = orchestrator.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["c"], starts);
        Assert.Null(orchestrator.ExecuteTask);

        cReady.SetResult(new ControlledHostedRun("c"));
        await bStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["c", "b"], starts);
        Assert.False(aStarted.Task.IsCompleted);

        bReady.SetResult(new ControlledHostedRun("b"));
        await aStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["c", "b", "a"], starts);
        Assert.Null(orchestrator.ExecuteTask);

        aReady.SetResult(new ControlledHostedRun("a"));
        await start;

        Assert.NotNull(orchestrator.ExecuteTask);
        Assert.Equal(HostedOrchestratorState.Running, orchestrator.State);
    }

    [Fact]
    public async Task ConcurrentStartAsyncCallers_ShareOneStartupTaskAndFactoryCall()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        var ready = NewRunGate();
        var registration = CreateRegistration("orders", 0, 0, _ => ready.Task);
        using var orchestrator = CreateOrchestrator(lifetime, [registration]);

        var starts = Enumerable.Range(0, 32)
            .Select(_ => orchestrator.StartAsync(TestContext.Current.CancellationToken))
            .ToArray();

        Assert.All(starts, task => Assert.Same(starts[0], task));
        Assert.Equal(1, registration.StartCalls);
        ready.SetResult(new ControlledHostedRun("orders"));
        await Task.WhenAll(starts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartFailure_RollsBackOnlyStartedRunsInReverseOrder(int failureIndex)
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        var primary = new InvalidOperationException("start");
        var calls = new List<string>();
        var registrations = Enumerable.Range(0, 3).Select(index =>
            CreateRegistration($"p{index}", 0, index, _ =>
            {
                if (index == failureIndex)
                    return Task.FromException<IHostedPipelineRun>(primary);

                var run = new ControlledHostedRun($"p{index}") { CallObserver = calls.Add };
                return Task.FromResult<IHostedPipelineRun>(run);
            })).ToArray();
        using var orchestrator = CreateOrchestrator(lifetime, registrations);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(primary, thrown);
        Assert.Equal(
            Enumerable.Range(0, failureIndex)
                .Reverse()
                .SelectMany(index => new[] { $"p{index}:abort", $"p{index}:dispose" }),
            calls);
        Assert.Equal(HostedOrchestratorState.Faulted, orchestrator.State);
        Assert.Null(orchestrator.ExecuteTask);
    }

    [Fact]
    public async Task StartFailure_AggregatesPrimaryThenReverseCleanupActionOrder()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        var primary = new InvalidOperationException("start");
        var aAbort = new InvalidOperationException("a-abort");
        var bAbort = new InvalidOperationException("b-abort");
        var bDispose = new InvalidOperationException("b-dispose");
        var a = new ControlledHostedRun("a") { AbortError = aAbort };
        var b = new ControlledHostedRun("b")
        {
            AbortError = bAbort,
            DisposeError = bDispose,
        };
        var registrations = new[]
        {
            CreateRegistration("a", 0, 0, _ => Task.FromResult<IHostedPipelineRun>(a)),
            CreateRegistration("b", 0, 1, _ => Task.FromResult<IHostedPipelineRun>(b)),
            CreateRegistration("c", 0, 2, _ => Task.FromException<IHostedPipelineRun>(primary)),
        };
        using var orchestrator = CreateOrchestrator(lifetime, registrations);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            orchestrator.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal([primary, bAbort, bDispose, aAbort], aggregate.InnerExceptions);
    }

    [Fact]
    public async Task CancelledStartup_UsesNoneForRollbackCleanup()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        using var cancellation = new CancellationTokenSource();
        var run = new ControlledHostedRun("a");
        cancellation.Cancel();
        var registrations = new[]
        {
            CreateRegistration("a", 0, 0, _ => Task.FromResult<IHostedPipelineRun>(run)),
            CreateRegistration("b", 0, 1, _ => Task.FromCanceled<IHostedPipelineRun>(cancellation.Token)),
        };
        using var orchestrator = CreateOrchestrator(lifetime, registrations);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.StartAsync(cancellation.Token));

        Assert.Equal(["abort", "dispose"], run.Calls);
        Assert.Equal(CancellationToken.None, run.AbortToken);
    }

    private static SmartPipeHostedOrchestrator CreateOrchestrator(
        RecordingHostApplicationLifetime lifetime,
        IEnumerable<IHostedPipelineRegistration> registrations) =>
        new(registrations, lifetime, NullLogger<SmartPipeHostedOrchestrator>.Instance);

    private static ControlledHostedRegistration CreateRegistration(
        string key,
        int order,
        int registrationOrder,
        Func<CancellationToken, Task<IHostedPipelineRun>> start) =>
        new(CreateDescriptor(key, order, registrationOrder), start);

    private static HostedPipelineDescriptor CreateDescriptor(
        string key,
        int order,
        int registrationOrder) =>
        new()
        {
            Key = new PipelineKey(key),
            InputType = typeof(int),
            OutputType = typeof(int),
            Order = order,
            RegistrationOrder = registrationOrder,
            DrainTimeout = TimeSpan.FromSeconds(30),
            FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication,
            CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive,
        };

    private static TaskCompletionSource<IHostedPipelineRun> NewRunGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
