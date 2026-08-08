using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
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
        var cStarted = NewSignal();
        var bStarted = NewSignal();
        var aStarted = NewSignal();
        var registrations = new[]
        {
            CreateRegistration("a", order: 1, _ =>
            {
                starts.Add("a");
                aStarted.TrySetResult();
                return aReady.Task;
            }),
            CreateRegistration("c", order: 0, _ =>
            {
                starts.Add("c");
                cStarted.TrySetResult();
                return cReady.Task;
            }),
            CreateRegistration("b", order: 0, _ =>
            {
                starts.Add("b");
                bStarted.TrySetResult();
                return bReady.Task;
            }),
        };
        using var orchestrator = CreateOrchestrator(lifetime, registrations);

        var start = orchestrator.StartAsync(TestContext.Current.CancellationToken);
        await cStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
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
        var factoryStarted = NewSignal();
        var registration = CreateRegistration("orders", 0, _ =>
        {
            factoryStarted.TrySetResult();
            return ready.Task;
        });
        using var orchestrator = CreateOrchestrator(lifetime, [registration]);

        var starts = Enumerable.Range(0, 32)
            .Select(_ => orchestrator.StartAsync(TestContext.Current.CancellationToken))
            .ToArray();

        Assert.All(starts, task => Assert.Same(starts[0], task));
        await factoryStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, registration.StartCalls);
        ready.SetResult(new ControlledHostedRun("orders"));
        await Task.WhenAll(starts);
    }

    [Fact]
    public async Task ConcurrentStartAsyncCallers_ShareFirstCallerCancellationOwnership()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        using var firstCancellation = new CancellationTokenSource();
        using var laterCancellation = new CancellationTokenSource();
        var factoryStarted = NewSignal();
        var neverReady = NewSignal();
        var registration = CreateRegistration("orders", 0, async token =>
        {
            factoryStarted.TrySetResult();
            await neverReady.Task.WaitAsync(token);
            throw new InvalidOperationException("Unreachable.");
        });
        using var orchestrator = CreateOrchestrator(lifetime, [registration]);

        var first = orchestrator.StartAsync(firstCancellation.Token);
        var later = orchestrator.StartAsync(laterCancellation.Token);
        await factoryStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        laterCancellation.Cancel();

        Assert.Same(first, later);
        Assert.False(registration.StartToken.IsCancellationRequested);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.True(registration.StartToken.IsCancellationRequested);
        Assert.Equal(HostedOrchestratorState.Faulted, orchestrator.State);
    }

    [Fact]
    public void Constructor_MissingCanonicalRegistrationFailsClosed()
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        var registration = CreateRegistration(
            "orders",
            0,
            _ => Task.FromResult<IHostedPipelineRun>(new ControlledHostedRun("orders")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new SmartPipeHostedOrchestrator(
                [registration],
                new FixedRegistry(null),
                lifetime,
                NullLogger<SmartPipeHostedOrchestrator>.Instance));

        Assert.Contains("no canonical DI registration", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Constructor_MismatchedCanonicalMetadataFailsClosed(int mismatch)
    {
        using var lifetime = new RecordingHostApplicationLifetime();
        var registration = CreateRegistration(
            "orders",
            0,
            _ => Task.FromResult<IHostedPipelineRun>(new ControlledHostedRun("orders")));
        var canonical = CreateCanonicalDescriptor(
            mismatch == 0 ? new PipelineKey("other") : new PipelineKey("orders"),
            mismatch == 1 ? typeof(string) : typeof(int),
            mismatch == 2 ? typeof(string) : typeof(int));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new SmartPipeHostedOrchestrator(
                [registration],
                new FixedRegistry(canonical),
                lifetime,
                NullLogger<SmartPipeHostedOrchestrator>.Instance));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
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
            CreateRegistration($"p{index}", 0, _ =>
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
            CreateRegistration("a", 0, _ => Task.FromResult<IHostedPipelineRun>(a)),
            CreateRegistration("b", 0, _ => Task.FromResult<IHostedPipelineRun>(b)),
            CreateRegistration("c", 0, _ => Task.FromException<IHostedPipelineRun>(primary)),
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
            CreateRegistration("a", 0, _ => Task.FromResult<IHostedPipelineRun>(run)),
            CreateRegistration("b", 0, _ => Task.FromCanceled<IHostedPipelineRun>(cancellation.Token)),
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
        new(
            registrations,
            TestSmartPipeRegistry.FromHosted(registrations),
            lifetime,
            NullLogger<SmartPipeHostedOrchestrator>.Instance);

    private static ControlledHostedRegistration CreateRegistration(
        string key,
        int order,
        Func<CancellationToken, Task<IHostedPipelineRun>> start) =>
        new(CreateDescriptor(key, order), start);

    private static HostedPipelineDescriptor CreateDescriptor(
        string key,
        int order) =>
        new()
        {
            Key = new PipelineKey(key),
            InputType = typeof(int),
            OutputType = typeof(int),
            Order = order,
            DrainTimeout = TimeSpan.FromSeconds(30),
            FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication,
            CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive,
        };

    private static TaskCompletionSource<IHostedPipelineRun> NewRunGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SmartPipeRegistrationDescriptor CreateCanonicalDescriptor(
        PipelineKey key,
        Type inputType,
        Type outputType) =>
        new()
        {
            Key = key,
            InputType = inputType,
            OutputType = outputType,
            DefinitionType = typeof(PipelineDefinition<int, int>),
            FactoryType = typeof(ISmartPipeRunFactory<int, int>),
            DisplayName = key.Value,
            RegistrationOrder = 0,
            IsReusable = true,
        };

    private sealed class FixedRegistry(SmartPipeRegistrationDescriptor? descriptor)
        : ISmartPipeRegistry
    {
        public IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations() =>
            descriptor is null ? [] : [descriptor];

        public SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key) =>
            descriptor ?? throw new KeyNotFoundException();

        public bool TryGetRegistration(
            PipelineKey key,
            [NotNullWhen(true)]
            out SmartPipeRegistrationDescriptor? registration)
        {
            registration = descriptor;
            return registration is not null;
        }
    }
}
