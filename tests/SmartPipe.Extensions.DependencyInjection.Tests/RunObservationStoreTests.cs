using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class RunObservationStoreTests
{
    [Fact]
    public void Capture_UnknownKeyFailsAndRegisteredKeyHasEmptyObservation()
    {
        var services = CreateServices("orders");
        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();

        var observation = source.Capture(new PipelineKey("orders"));

        Assert.Equal("orders", observation.PipelineKey.Value);
        Assert.Empty(observation.ActiveRuns);
        Assert.Null(observation.LatestTerminal);
        Assert.Throws<KeyNotFoundException>(() => source.Capture(new PipelineKey("Orders")));
    }

    [Fact]
    public void RecordTerminal_ReplacesOneValueWithMonotonicSequence()
    {
        var services = CreateServices("orders");
        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();

        SmartPipeTerminalRunObservation? latest = null;
        for (var index = 0; index < 10_000; index++)
        {
            latest = store.RecordTerminal(Candidate("orders", Guid.NewGuid()));
        }

        Assert.NotNull(latest);
        Assert.Equal(10_000, latest.Sequence);
        Assert.Same(latest, source.Capture(new PipelineKey("orders")).LatestTerminal);
    }

    [Fact]
    public async Task Capture_ComposesActiveRunsWithLatestTerminalObservation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("composed"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new GateSource(gate))))
            .Build();
        var services = new ServiceCollection();
        services.AddSmartPipe().AddPipeline(definition);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();

        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        var terminal = store.RecordTerminal(Candidate("composed", Guid.NewGuid()));

        var observation = source.Capture(definition.Key);

        Assert.Equal(run.RunId, Assert.Single(observation.ActiveRuns).Identity.RunId);
        Assert.Same(terminal, observation.LatestTerminal);

        gate.SetResult();
        await run.Completion;
    }

    [Fact]
    public void RecordTerminal_UsesCommitOrderWhenRunTimestampsAreReversed()
    {
        var services = CreateServices("orders");
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var laterStarted = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var earlierStarted = DateTimeOffset.UnixEpoch.AddMinutes(1);

        var first = store.RecordTerminal(CandidateAt("orders", Guid.NewGuid(), laterStarted));
        var second = store.RecordTerminal(CandidateAt("orders", Guid.NewGuid(), earlierStarted));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Same(second, source.Capture(new PipelineKey("orders")).LatestTerminal);
    }

    [Fact]
    public void RecordTerminal_InvalidPublicationDoesNotAdvancePerKeySequence()
    {
        var services = CreateServices("orders");
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();

        Assert.Equal(1, store.RecordTerminal(Candidate("orders", Guid.NewGuid())).Sequence);
        var invalid = CandidateAt(
            "orders",
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch.AddMinutes(2),
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Throws<ArgumentException>(() => store.RecordTerminal(invalid));
        Assert.Equal(2, store.RecordTerminal(Candidate("orders", Guid.NewGuid())).Sequence);
    }

    [Fact]
    public void Capture_WhenMetricsProviderThrows_LeavesTerminalAndSequenceUnchanged()
    {
        var services = CreateServices("orders");
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<SmartPipeRunRegistry>();
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();
        var before = store.RecordTerminal(Candidate("orders", Guid.NewGuid()));
        var failure = new InvalidOperationException("sensitive metrics provider failure");
        var run = new PipelineRun<int>(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            Task.CompletedTask,
            static () => PipelineRunState.Running,
            cancel: null,
            drain: null,
            tryDrain: null,
            abort: null,
            dispose: null,
            metricsProvider: () => throw failure,
            new PipelineKey("orders"),
            Guid.NewGuid(),
            inputCapacity: 8,
            outputCapacity: 4);
        using var registration = registry.Register<int, int>(run, DateTimeOffset.UnixEpoch);

        var observed = Assert.Throws<InvalidOperationException>(
            () => source.Capture(new PipelineKey("orders")));

        Assert.Same(failure, observed);
        registration.Dispose();
        Assert.Same(before, source.Capture(new PipelineKey("orders")).LatestTerminal);
        Assert.Equal(2, store.RecordTerminal(Candidate("orders", Guid.NewGuid())).Sequence);
    }

    [Fact]
    public void RecordTerminal_IsolatedByOrdinalCaseSensitiveKey()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        builder.AddPipeline(Definition("orders"));
        builder.AddPipeline(Definition("Orders"));
        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();

        store.RecordTerminal(Candidate("orders", Guid.NewGuid()));

        Assert.NotNull(source.Capture(new PipelineKey("orders")).LatestTerminal);
        Assert.Null(source.Capture(new PipelineKey("Orders")).LatestTerminal);
    }

    [Fact]
    public void AddSmartPipe_RegistersOneObservationStoreForBothContracts()
    {
        var services = CreateServices("orders");
        services.AddSmartPipe();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<ISmartPipeRunObservationSource>(),
            provider.GetRequiredService<ISmartPipeMutableRunObservationStore>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISmartPipeRunObservationSource));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISmartPipeMutableRunObservationStore));
    }

    [Fact]
    public void AddSmartPipeCanonicalRegistrationContainsNoHealthChecksServices()
    {
        var services = new ServiceCollection();

        services.AddSmartPipe();

        Assert.DoesNotContain(services, descriptor =>
            ContainsHealthChecksName(descriptor.ServiceType)
            || ContainsHealthChecksName(descriptor.ImplementationType));
    }

    [Fact]
    public async Task ConcurrentTerminalCommitsAcrossKeysRemainBoundedAndStrictlySequenced()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        var keys = Enumerable.Range(0, 20).Select(index => $"pipeline-{index:D2}").ToArray();
        foreach (var key in keys) builder.AddPipeline(Definition(key));
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISmartPipeMutableRunObservationStore>();
        var source = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var committed = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<long>>();

        await Task.WhenAll(Enumerable.Range(0, 1_000).Select(index => Task.Run(() =>
        {
            var key = keys[index % keys.Length];
            var terminal = store.RecordTerminal(Candidate(key, Guid.NewGuid()));
            committed.GetOrAdd(key, static _ => []).Add(terminal.Sequence);
            _ = source.Capture(new PipelineKey(key));
        })));

        Assert.Equal(keys, source.CaptureAll().Select(item => item.PipelineKey.Value));
        foreach (var key in keys)
        {
            Assert.Equal(Enumerable.Range(1, 50).Select(value => (long)value), committed[key].Order());
            Assert.Equal(50, source.Capture(new PipelineKey(key)).LatestTerminal?.Sequence);
        }
    }

    [Fact]
    public async Task Capture_DoesNotBlockUnrelatedKeyWhileActiveSnapshotProviderIsBlocked()
    {
        var blockedKey = new PipelineKey("blocked");
        var freeKey = new PipelineKey("free");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SmartPipeRunObservationStore(
            new TestRegistry(blockedKey, freeKey),
            new BlockingRunRegistry(blockedKey, entered, release),
            TimeProvider.System);

        var blockedCapture = Task.Run(() => source.Capture(blockedKey));
        await entered.Task;

        var freeCapture = await Task.Run(() => source.Capture(freeKey))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(freeKey, freeCapture.PipelineKey);
        Assert.False(blockedCapture.IsCompleted);

        release.SetResult();
        await blockedCapture;
    }

    private static ServiceCollection CreateServices(string key)
    {
        var services = new ServiceCollection();
        services.AddSmartPipe().AddPipeline(Definition(key));
        return services;
    }

    private static PipelineDefinition<int, int> Definition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
            .Build();

    private static SmartPipeTerminalRunCandidate Candidate(string key, Guid runId) =>
        CandidateAt(key, runId, DateTimeOffset.UnixEpoch);

    private static SmartPipeTerminalRunCandidate CandidateAt(
        string key,
        Guid runId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc = null) => new(
        new SmartPipeRunIdentity { PipelineKey = new PipelineKey(key), RunId = runId },
        typeof(int),
        typeof(int),
        SmartPipeRunObservationOutcome.Completed,
        startedAtUtc,
        completedAtUtc ?? startedAtUtc.AddSeconds(1),
        SmartPipeMetricsSnapshot.Empty,
        8,
        4);

    private static bool ContainsHealthChecksName(Type? type) =>
        type?.Assembly.GetName().Name?.Contains("HealthChecks", StringComparison.OrdinalIgnoreCase) == true
        || type?.FullName?.Contains("HealthCheck", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class GateSource : IPipelineSource<int>
    {
        private readonly TaskCompletionSource _gate;

        internal GateSource(TaskCompletionSource gate) => _gate = gate;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await _gate.Task.WaitAsync(ct);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRegistry : ISmartPipeRegistry
    {
        private readonly Dictionary<PipelineKey, SmartPipeRegistrationDescriptor> _registrations;

        internal TestRegistry(params PipelineKey[] keys) =>
            _registrations = keys.Select((key, index) => new SmartPipeRegistrationDescriptor
            {
                Key = key,
                InputType = typeof(int),
                OutputType = typeof(int),
                DefinitionType = typeof(object),
                FactoryType = typeof(object),
                DisplayName = key.Value,
                RegistrationOrder = index,
                IsReusable = true,
            })
                .ToDictionary(static registration => registration.Key);

        public IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations() =>
            Array.AsReadOnly(_registrations.Values.OrderBy(item => item.RegistrationOrder).ToArray());

        public SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key) =>
            _registrations.TryGetValue(key, out var registration)
                ? registration
                : throw new KeyNotFoundException(key.Value);

        public bool TryGetRegistration(
            PipelineKey key,
            [NotNullWhen(true)]
            out SmartPipeRegistrationDescriptor? registration) =>
            _registrations.TryGetValue(key, out registration);
    }

    private sealed class BlockingRunRegistry : ISmartPipeRunRegistry
    {
        private readonly PipelineKey _blockedKey;
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;

        internal BlockingRunRegistry(
            PipelineKey blockedKey,
            TaskCompletionSource entered,
            TaskCompletionSource release)
        {
            _blockedKey = blockedKey;
            _entered = entered;
            _release = release;
        }

        public IReadOnlyList<SmartPipeRunSnapshot> GetActiveRuns(PipelineKey pipelineKey)
        {
            if (pipelineKey == _blockedKey)
            {
                _entered.SetResult();
                _release.Task.GetAwaiter().GetResult();
            }

            return Array.Empty<SmartPipeRunSnapshot>();
        }
    }

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
