using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class FactoryScopeTests
{
    [Fact]
    public async Task StartAsync_UsesOneScopeForAllComponentsAndNewScopePerRun()
    {
        var services = new ServiceCollection();
        var recorder = new ScopeRecorder();
        var timeProvider = new TestTimeProvider();
        services.AddSingleton(recorder);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddScoped<ScopedMarker>();
        var definition = CreateScopedDefinition("scoped");
        services.AddSmartPipe().AddPipeline(definition);
        await using var root = services.BuildServiceProvider();
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("scoped");
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await factory.StartAsync(cancellationToken);
        await first.Completion;
        var second = await factory.StartAsync(cancellationToken);
        await second.Completion;

        Assert.Equal(2, recorder.SourceMarkers.Count);
        Assert.Equal(recorder.SourceMarkers, recorder.StageMarkers);
        Assert.Equal(recorder.SourceMarkers, recorder.SinkMarkers);
        Assert.Equal(2, recorder.SourceMarkers.Distinct().Count());
        Assert.Equal(recorder.SourceMarkers.Order(), recorder.DisposedMarkers.Order());
        Assert.All(recorder.TimeProviders, observed => Assert.Same(timeProvider, observed));
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(definition.Key, first.PipelineKey);
        Assert.Equal(definition.Key, second.PipelineKey);
    }

    [Fact]
    public async Task StartAsync_WhenPreCanceled_CreatesNoScope()
    {
        var definition = CreateScopedDefinition("pre-canceled");
        var scopeFactory = new RejectingScopeFactory();
        var factory = new SmartPipeRunFactory<int, int>(
            definition,
            scopeFactory,
            new RejectingTimeProvider(),
            new SmartPipeRunRegistry(),
            new TestObservationStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.StartAsync(cancellation.Token));

        Assert.Equal(0, scopeFactory.CreatedScopes);
    }

    [Fact]
    public async Task StartAsync_WhenCanceledDuringActivation_DisposesCreatedScope()
    {
        await using var root = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(root.GetRequiredService<IServiceScopeFactory>());
        using var cancellation = new CancellationTokenSource();
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("activation-canceled"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>((_, cancellationToken) =>
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<IPipelineSource<int>>(new CountingEmptySource());
                }))
            .Build();
        var factory = new SmartPipeRunFactory<int, int>(
            definition,
            scopeFactory,
            TimeProvider.System,
            new SmartPipeRunRegistry(),
            new TestObservationStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.StartAsync(cancellation.Token));

        Assert.Equal(1, scopeFactory.CreatedScopes);
        Assert.Equal(1, scopeFactory.DisposedScopes);
    }

    [Fact]
    public async Task StartAsync_WhenSingleUseDefinitionStartsTwice_DisposesSecondScopeWithoutReactivation()
    {
        var source = new CountingEmptySource();
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("single-use"),
                PipelineComponent.Borrowed<IPipelineSource<int>>(source, initialize: true))
            .Build();
        await using var root = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(
            root.GetRequiredService<IServiceScopeFactory>());
        var registry = new SmartPipeRunRegistry();
        var factory = new SmartPipeRunFactory<int, int>(
            definition,
            scopeFactory,
            TimeProvider.System,
            registry,
            new TestObservationStore());

        var first = await factory.StartAsync(TestContext.Current.CancellationToken);
        await first.Completion;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, source.InitializeCalls);
        Assert.Equal(2, scopeFactory.CreatedScopes);
        Assert.Equal(2, scopeFactory.DisposedScopes);
        Assert.Empty(registry.GetActiveRuns(definition.Key));
    }

    [Fact]
    public async Task StartAsync_WhenInstanceObserverStartsTwice_DoesNotReactivateOrLeakRun()
    {
        var sourceActivations = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("observer-single-use"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>((_, _) =>
                {
                    Interlocked.Increment(ref sourceActivations);
                    return ValueTask.FromResult<IPipelineSource<int>>(new CountingEmptySource());
                }))
            .WithObserver(new NoOpObserver())
            .Build();
        await using var root = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(root.GetRequiredService<IServiceScopeFactory>());
        var registry = new SmartPipeRunRegistry();
        var factory = new SmartPipeRunFactory<int, int>(
            definition,
            scopeFactory,
            TimeProvider.System,
            registry,
            new TestObservationStore());

        var first = await factory.StartAsync(TestContext.Current.CancellationToken);
        await first.Completion;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, sourceActivations);
        Assert.Equal(2, scopeFactory.CreatedScopes);
        Assert.Equal(2, scopeFactory.DisposedScopes);
        Assert.Empty(registry.GetActiveRuns(definition.Key));
    }

    [Fact]
    public async Task StartAsync_WhenComponentActivationFails_DisposesCreatedScope()
    {
        var disposal = new DisposalRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(disposal);
        services.AddScoped<FailureMarker>();
        await using var root = services.BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(
            root.GetRequiredService<IServiceScopeFactory>());
        var failure = new InvalidOperationException("activation failed");
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("activation-failure"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>((context, cancellationToken) =>
                {
                    _ = context.Services!.GetRequiredService<FailureMarker>();
                    throw failure;
                }))
            .Build();
        var factory = new SmartPipeRunFactory<int, int>(
            definition,
            scopeFactory,
            TimeProvider.System,
            new SmartPipeRunRegistry(),
            new TestObservationStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Equal(1, scopeFactory.CreatedScopes);
        Assert.Equal(1, scopeFactory.DisposedScopes);
        Assert.Equal(1, disposal.DisposedMarkers);
    }

    [Fact]
    public async Task StartAsync_WhenRegistrationAndScopeCleanupFail_AggregatesOriginalFirst()
    {
        var registrationError = new InvalidOperationException("registration");
        var scopeDisposeError = new InvalidOperationException("scope dispose");
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("startup-cleanup"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                        new CountingEmptySource())))
            .Build();
        await using var root = new ServiceCollection().BuildServiceProvider();
        var factory = new SmartPipeRunFactory<int, int>(
            definition,
            new ThrowingDisposeScopeFactory(root, scopeDisposeError),
            TimeProvider.System,
            new ThrowingRunRegistry(registrationError),
            new TestObservationStore());

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            [registrationError, scopeDisposeError],
            error.InnerExceptions);
    }

    private static PipelineDefinition<int, int> CreateScopedDefinition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (context, _) =>
                    {
                        var services = context.Services!;
                        return ValueTask.FromResult<IPipelineSource<int>>(
                            new RecordingSource(
                                services.GetRequiredService<ScopedMarker>(),
                                services.GetRequiredService<ScopeRecorder>(),
                                context.TimeProvider));
                    }))
            .Transform(
                new PipelineStageKey("identity"),
                PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>(
                    static (context, _) =>
                    {
                        var services = context.Services!;
                        return ValueTask.FromResult<IPipelineTransformer<int, int>>(
                            new RecordingTransformer(
                                services.GetRequiredService<ScopedMarker>(),
                                services.GetRequiredService<ScopeRecorder>()));
                    }))
            .To(PipelineComponent.ScopeOwned<IPipelineSink<int>>(
                static (context, _) =>
                {
                    var services = context.Services!;
                    return ValueTask.FromResult<IPipelineSink<int>>(
                        new RecordingSink(
                            services.GetRequiredService<ScopedMarker>(),
                            services.GetRequiredService<ScopeRecorder>()));
                }));

    private sealed class ScopeRecorder
    {
        internal List<Guid> SourceMarkers { get; } = [];
        internal List<Guid> StageMarkers { get; } = [];
        internal List<Guid> SinkMarkers { get; } = [];
        internal List<Guid> DisposedMarkers { get; } = [];
        internal List<TimeProvider> TimeProviders { get; } = [];
    }

    private sealed class ScopedMarker : IDisposable
    {
        private readonly ScopeRecorder _recorder;

        public ScopedMarker(ScopeRecorder recorder) => _recorder = recorder;

        internal Guid Id { get; } = Guid.NewGuid();

        public void Dispose() => _recorder.DisposedMarkers.Add(Id);
    }

    private sealed class RecordingSource : IPipelineSource<int>
    {
        private readonly ScopedMarker _marker;
        private readonly ScopeRecorder _recorder;
        private readonly TimeProvider _timeProvider;

        internal RecordingSource(
            ScopedMarker marker,
            ScopeRecorder recorder,
            TimeProvider timeProvider)
        {
            _marker = marker;
            _recorder = recorder;
            _timeProvider = timeProvider;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            _recorder.SourceMarkers.Add(_marker.Id);
            _recorder.TimeProviders.Add(_timeProvider);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return ProcessingEnvelope<int>.Create(1, "scoped", "run", 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTransformer : IPipelineTransformer<int, int>
    {
        private readonly ScopedMarker _marker;
        private readonly ScopeRecorder _recorder;

        internal RecordingTransformer(ScopedMarker marker, ScopeRecorder recorder)
        {
            _marker = marker;
            _recorder = recorder;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            _recorder.StageMarkers.Add(_marker.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IPipelineSink<int>
    {
        private readonly ScopedMarker _marker;
        private readonly ScopeRecorder _recorder;

        internal RecordingSink(ScopedMarker marker, ScopeRecorder recorder)
        {
            _marker = marker;
            _recorder = recorder;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            _recorder.SinkMarkers.Add(_marker.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestTimeProvider : TimeProvider;

    private sealed class RejectingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("A pre-canceled start must not allocate identity metadata.");
    }

    private sealed class NoOpObserver : IPipelineObserver
    {
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class CountingEmptySource : IPipelineSource<int>
    {
        internal int InitializeCalls { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            InitializeCalls++;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposalRecorder
    {
        internal int DisposedMarkers { get; set; }
    }

    private sealed class FailureMarker : IDisposable
    {
        private readonly DisposalRecorder _recorder;

        public FailureMarker(DisposalRecorder recorder) => _recorder = recorder;

        public void Dispose() => _recorder.DisposedMarkers++;
    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;

        internal CountingScopeFactory(IServiceScopeFactory inner) => _inner = inner;

        internal int CreatedScopes { get; private set; }

        internal int DisposedScopes { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopes++;
            return new CountingScope(_inner.CreateScope(), this);
        }

        private sealed class CountingScope : IServiceScope, IAsyncDisposable
        {
            private readonly IServiceScope _inner;
            private readonly CountingScopeFactory _owner;
            private int _disposed;

            internal CountingScope(IServiceScope inner, CountingScopeFactory owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public IServiceProvider ServiceProvider => _inner.ServiceProvider;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _inner.Dispose();
                    _owner.DisposedScopes++;
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (_inner is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    _inner.Dispose();
                }

                _owner.DisposedScopes++;
            }
        }
    }

    private sealed class RejectingScopeFactory : IServiceScopeFactory
    {
        internal int CreatedScopes { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopes++;
            throw new InvalidOperationException("A pre-canceled start must not create a scope.");
        }
    }

    private sealed class ThrowingRunRegistry : ISmartPipeMutableRunRegistry
    {
        private readonly Exception _error;

        internal ThrowingRunRegistry(Exception error) => _error = error;

        public IDisposable Register<TInput, TOutput>(
            PipelineRun<TOutput> run,
            DateTimeOffset startedAtUtc) => throw _error;
    }

    private sealed class TestObservationStore : ISmartPipeMutableRunObservationStore
    {
        private long _sequence;

        public SmartPipeTerminalRunObservation RecordTerminal(SmartPipeTerminalRunCandidate candidate) => new()
        {
            Identity = candidate.Identity,
            InputType = candidate.InputType,
            OutputType = candidate.OutputType,
            Outcome = candidate.Outcome,
            StartedAtUtc = candidate.StartedAtUtc,
            CompletedAtUtc = candidate.CompletedAtUtc,
            Metrics = candidate.Metrics,
            InputCapacity = candidate.InputCapacity,
            OutputCapacity = candidate.OutputCapacity,
            Sequence = Interlocked.Increment(ref _sequence),
        };
    }

    private sealed class ThrowingDisposeScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _services;
        private readonly Exception _error;

        internal ThrowingDisposeScopeFactory(IServiceProvider services, Exception error)
        {
            _services = services;
            _error = error;
        }

        public IServiceScope CreateScope() => new ThrowingDisposeScope(_services, _error);

        private sealed class ThrowingDisposeScope : IServiceScope, IAsyncDisposable
        {
            private readonly Exception _error;

            internal ThrowingDisposeScope(IServiceProvider services, Exception error)
            {
                ServiceProvider = services;
                _error = error;
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() => throw _error;

            public ValueTask DisposeAsync() => ValueTask.FromException(_error);
        }
    }
}
