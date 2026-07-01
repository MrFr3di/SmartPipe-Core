#nullable enable

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Tests.Extensions;

public sealed class SmartPipeTypedDiTests
{
    [Fact]
    public async Task DI_Factory_StartAsyncCreatesNewRuntimePerStart()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();

        var first = await factory.StartAsync();
        var second = await factory.StartAsync();

        first.Should().NotBeSameAs(second);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DI_Factory_Start_CompatibilityPath_CreatesRuntime()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();

        var run = factory.Start();

        run.Should().NotBeNull();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DI_Factory_Run_PreservesTryDrainAsync()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = CreateControllablePipelineServices(gate);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();

        var run = await factory.StartAsync();

        try
        {
            var result = await run.TryDrainAsync(TimeSpan.FromMilliseconds(50));

            result.Status.Should().NotBe(PipelineDrainStatus.AlreadyCompleted);
        }
        finally
        {
            gate.TrySetResult();
            await ObserveCompletionAfterManualDisposeAsync(run);
        }
    }

    [Fact]
    public async Task DI_Factory_Run_PreservesMetricsSnapshot()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();

        var run = await factory.StartAsync();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.Metrics.ItemsProcessed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ISmartPipeFactory_DefaultStartAsync_DoesNotBridgeToStart()
    {
        ISmartPipeFactory<int, int> factory = new SyncOnlyTypedFactory();

        var act = async () => await factory.StartAsync();

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*StartAsync*");
    }

    [Fact]
    public async Task DI_ScopedStageResolvedWithinScope()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var first = await factory.StartAsync();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await factory.StartAsync();
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        recorder.StageScopeIds.Should().HaveCount(2);
        recorder.StageScopeIds.Should().OnlyHaveUniqueItems();
        recorder.SinkScopeIds.Should().BeEquivalentTo(recorder.StageScopeIds);
    }

    [Fact]
    public async Task DI_Factory_ScopedComponentsDisposedWithScope()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var run = await factory.StartAsync();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
        recorder.DisposedSinkScopeIds.Should().BeEquivalentTo(recorder.DisposedMarkerScopeIds);
    }

    [Fact]
    public async Task DI_Factory_ManualDisposeAfterCompletion_DisposesScopeOnce()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var run = await factory.StartAsync();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await run.DisposeAsync();

        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DI_Factory_CompletionDisposesScopeOnce()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var run = await factory.StartAsync();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        // No manual dispose: completion alone must dispose the scope exactly once.
        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DI_Factory_ManualDisposeBeforeCompletion_DisposesScopeOnce()
    {
        // A controllable source blocks on a TCS gate. The test disposes the run
        // before the source yields, then signals the gate; the scope must be
        // disposed exactly once across the disposal race.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = CreateControllablePipelineServices(gate);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var run = await factory.StartAsync();
        // Dispose while the source is still blocked on the gate.
        await run.DisposeAsync();

        // After dispose, releasing the gate must not cause a second scope disposal.
        gate.SetResult();
        await ObserveCompletionAfterManualDisposeAsync(run);

        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DI_Factory_CompletionAndManualDisposeRace_DisposesScopeOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = CreateControllablePipelineServices(gate);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var run = await factory.StartAsync();

        // 8 concurrent disposes race the completion path; exactly one must win.
        var disposeTasks = Enumerable.Range(0, 8).Select(_ => run.DisposeAsync().AsTask()).ToArray();
        var disposes = Task.WhenAll(disposeTasks);
        gate.SetResult();

        await disposes.WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveCompletionAfterManualDisposeAsync(run);

        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DI_Factory_StartFailure_DisposesScope()
    {
        // Use a source that takes a scoped marker and throws in its constructor;
        // resolution happens inside the factory's StartAsync, the catch block
        // must dispose the scope (and the marker within it) before rethrowing.
        var services = new ServiceCollection();
        services.AddSingleton<TypedDiRecorder>();
        services.AddScoped<TypedScopedMarker>();
        services.AddScoped<ThrowingInitSource>();
        services.AddScoped<ScopedMarkerStage>();
        services.AddScoped<ScopedRecordingSink>();
        services.AddSmartPipe<int, Guid>(
            "typed-di-start-fail",
            builder => builder
                .UseSource<ThrowingInitSource>()
                .UseStage<ScopedMarkerStage>()
                .UseSink<ScopedRecordingSink>()
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
                }));
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var act = async () => await factory.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("init boom");

        // The source takes a scoped marker as a constructor dependency. The
        // constructor of ThrowingInitSource is invoked during scope resolution,
        // so the marker has been created; the start-failure catch block must
        // dispose the scope and therefore the marker.
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public void DI_Factory_StartFailure_DisposesAsyncScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TypedDiRecorder>();
        services.AddScoped<AsyncOnlyScopedMarker>();
        services.AddScoped<TypedScopedMarker>();
        services.AddScoped<ThrowingSourceWithAsyncOnlyMarker>();
        services.AddScoped<ScopedMarkerStage>();
        services.AddScoped<ScopedRecordingSink>();
        services.AddSmartPipe<int, Guid>(
            "typed-di-sync-start-fail",
            builder => builder
                .UseSource<ThrowingSourceWithAsyncOnlyMarker>()
                .UseStage<ScopedMarkerStage>()
                .UseSink<ScopedRecordingSink>()
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
                }));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var act = () => factory.Start();

        var thrown = act.Should().Throw<InvalidOperationException>()
            .WithMessage("sync init boom");
        thrown.Which.StackTrace.Should().Contain(nameof(ThrowingSourceWithAsyncOnlyMarker));
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DI_Factory_ValidateScopes_RemainsGreen()
    {
        var services = CreateTypedPipelineServices();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var run = await factory.StartAsync();
        run.Should().NotBeNull();
    }

    [Fact]
    public async Task HostedService_CreatesRuntimeFromFactory()
    {
        var factory = new RecordingTypedFactory();
        var hostedService = new SmartPipeHostedService<int, int>(
            factory,
            NullLogger<SmartPipeHostedService<int, int>>.Instance);

        await hostedService.StartAsync(CancellationToken.None);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await hostedService.StopAsync(CancellationToken.None);

        factory.StartCalls.Should().Be(1);
        factory.DrainCalls.Should().Be(1);
        factory.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task HostedService_FaultBehaviorStopApplication_StopsApplication()
    {
        var lifetime = new RecordingApplicationLifetime();
        var hostedService = new ExposedHostedService(
            new FaultingTypedFactory(new InvalidOperationException("pipeline failed")),
            new SmartPipeHostedServiceOptions
            {
                FailureBehavior = SmartPipeHostedFailureBehavior.StopApplication,
            },
            lifetime);

        await hostedService.ExecuteForTestAsync(CancellationToken.None);

        lifetime.StopApplicationCalls.Should().Be(1);
    }

    [Fact]
    public async Task HostedService_FaultBehaviorRethrow_RethrowsWithOriginalStackTrace()
    {
        var exception = CreateExceptionWithOriginalStackTrace();
        exception.StackTrace.Should().Contain(nameof(ThrowOriginalHostedServiceFailure));

        var hostedService = new ExposedHostedService(
            new FaultingTypedFactory(exception),
            new SmartPipeHostedServiceOptions
            {
                FailureBehavior = SmartPipeHostedFailureBehavior.Rethrow,
            });

        var act = () => hostedService.ExecuteForTestAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("pipeline failed");
        thrown.Which.Should().BeSameAs(exception);
        thrown.Which.StackTrace.Should().Contain(nameof(ThrowOriginalHostedServiceFailure));
    }

    [Fact]
    public async Task HostedService_FaultBehaviorIgnore_DoesNotStopHost()
    {
        var lifetime = new RecordingApplicationLifetime();
        var hostedService = new ExposedHostedService(
            new FaultingTypedFactory(new InvalidOperationException("pipeline failed")),
            new SmartPipeHostedServiceOptions
            {
                FailureBehavior = SmartPipeHostedFailureBehavior.Ignore,
            },
            lifetime);

        await hostedService.ExecuteForTestAsync(CancellationToken.None);

        lifetime.StopApplicationCalls.Should().Be(0);
    }

    [Fact]
    public void HostedServiceOptions_Defaults_AreReleaseContract()
    {
        var options = new SmartPipeHostedServiceOptions();

        options.FailureBehavior.Should().Be(SmartPipeHostedFailureBehavior.StopApplication);
        options.DrainTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Fact]
    public async Task HostedService_StopAsync_UsesConfiguredDrainTimeout()
    {
        var factory = new RecordingTypedFactory();
        var hostedService = new SmartPipeHostedService<int, int>(
            factory,
            NullLogger<SmartPipeHostedService<int, int>>.Instance,
            Options.Create(new SmartPipeHostedServiceOptions
            {
                DrainTimeout = TimeSpan.FromMilliseconds(123),
            }));

        await hostedService.StartAsync(CancellationToken.None);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await hostedService.StopAsync(CancellationToken.None);

        factory.LastDrainTimeout.Should().Be(TimeSpan.FromMilliseconds(123));
    }

    private static ServiceCollection CreateTypedPipelineServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TypedDiRecorder>();
        services.AddScoped<TypedScopedMarker>();
        services.AddScoped<SingleItemSource>();
        services.AddScoped<ScopedMarkerStage>();
        services.AddScoped<ScopedRecordingSink>();
        services.AddSmartPipe<int, Guid>(
            "typed-di",
            builder => builder
                .UseSource<SingleItemSource>()
                .UseStage<ScopedMarkerStage>()
                .UseSink<ScopedRecordingSink>()
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
                }));
        return services;
    }

    private static PipelineRun<int> CreateCompletedRun()
    {
        var outputs = Channel.CreateUnbounded<PipelineOutput<int>>();
        outputs.Writer.TryComplete();

        return new PipelineRun<int>(
            outputs.Reader,
            Task.CompletedTask,
            () => PipelineRunState.Completed);
    }

    private static ServiceCollection CreateControllablePipelineServices(
        TaskCompletionSource startGate)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TypedDiRecorder>();
        services.AddScoped<TypedScopedMarker>();
        services.AddScoped(sp => new ControllableSource(startGate));
        services.AddScoped<ScopedMarkerStage>();
        services.AddScoped<ScopedRecordingSink>();
        services.AddSmartPipe<int, Guid>(
            "typed-di-controllable",
            builder => builder
                .UseSource<ControllableSource>()
                .UseStage<ScopedMarkerStage>()
                .UseSink<ScopedRecordingSink>()
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
                }));
        return services;
    }

    private static async Task ObserveCompletionAfterManualDisposeAsync(PipelineRun<Guid> run)
    {
        try
        {
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            // Manual dispose cancels the inner run; this test asserts scope disposal idempotency.
        }
        catch (TimeoutException)
        {
            // Completion may not reach a terminal state after the run is already disposed.
        }
    }

    private static Exception CreateExceptionWithOriginalStackTrace()
    {
        try
        {
            ThrowOriginalHostedServiceFailure();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Unreachable.");
    }

    private static void ThrowOriginalHostedServiceFailure()
    {
        throw new InvalidOperationException("pipeline failed");
    }

    private sealed class SingleItemSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return ProcessingEnvelope<int>.Create(1, "typed-di", "run", 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControllableSource : IPipelineSource<int>
    {
        private readonly TaskCompletionSource _startGate;

        public ControllableSource(TaskCompletionSource startGate)
        {
            _startGate = startGate;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Wait for the gate OR for cancellation, whichever comes first.
            // Avoid WaitAsync(ct) because the runtime's CTS is disposed during
            // run dispose, which would surface as ObjectDisposedException here.
            var gateTask = _startGate.Task;
            var cancelTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            try
            {
                registration = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), cancelTcs);
            }
            catch (ObjectDisposedException)
            {
                cancelTcs.TrySetResult(false);
            }
            try
            {
                var winner = await Task.WhenAny(gateTask, cancelTcs.Task).ConfigureAwait(false);
                if (winner != gateTask)
                    yield break;
            }
            finally
            {
                registration.Dispose();
            }
            yield return ProcessingEnvelope<int>.Create(1, "typed-di", "run", 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingInitSource : IPipelineSource<int>
    {
        public ThrowingInitSource(TypedScopedMarker marker) =>
            throw new InvalidOperationException("init boom");

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSourceWithAsyncOnlyMarker : IPipelineSource<int>
    {
        public ThrowingSourceWithAsyncOnlyMarker(AsyncOnlyScopedMarker marker) =>
            throw new InvalidOperationException("sync init boom");

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScopedMarkerStage : IPipelineTransformer<int, Guid>
    {
        private readonly TypedScopedMarker _marker;
        private readonly TypedDiRecorder _recorder;

        public ScopedMarkerStage(TypedScopedMarker marker, TypedDiRecorder recorder)
        {
            _marker = marker;
            _recorder = recorder;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<Guid>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            _recorder.StageScopeIds.Add(_marker.Id);
            return ValueTask.FromResult(StageResult<Guid>.Success(_marker.Id));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScopedRecordingSink : IPipelineSink<Guid>
    {
        private readonly TypedScopedMarker _marker;
        private readonly TypedDiRecorder _recorder;

        public ScopedRecordingSink(TypedScopedMarker marker, TypedDiRecorder recorder)
        {
            _marker = marker;
            _recorder = recorder;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<Guid> envelope, CancellationToken ct = default)
        {
            _recorder.SinkScopeIds.Add(_marker.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _recorder.DisposedSinkScopeIds.Add(_marker.Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TypedScopedMarker : IDisposable
    {
        private readonly TypedDiRecorder _recorder;

        public TypedScopedMarker(TypedDiRecorder recorder)
        {
            _recorder = recorder;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public void Dispose() => _recorder.DisposedMarkerScopeIds.Add(Id);
    }

    private sealed class AsyncOnlyScopedMarker : IAsyncDisposable
    {
        private readonly TypedDiRecorder _recorder;

        public AsyncOnlyScopedMarker(TypedDiRecorder recorder)
        {
            _recorder = recorder;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public ValueTask DisposeAsync()
        {
            _recorder.DisposedMarkerScopeIds.Add(Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TypedDiRecorder
    {
        public List<Guid> StageScopeIds { get; } = [];

        public List<Guid> SinkScopeIds { get; } = [];

        public List<Guid> DisposedSinkScopeIds { get; } = [];

        public List<Guid> DisposedMarkerScopeIds { get; } = [];
    }

    private sealed class SyncOnlyTypedFactory : ISmartPipeFactory<int, int>
    {
        public int StartCalls { get; private set; }

        public PipelineRun<int> Start(CancellationToken ct = default)
        {
            StartCalls++;
            return CreateCompletedRun();
        }
    }

    private sealed class RecordingTypedFactory : ISmartPipeFactory<int, int>
    {
        private readonly Channel<PipelineOutput<int>> _outputs = Channel.CreateUnbounded<PipelineOutput<int>>();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started => _started;

        public int StartCalls { get; private set; }

        public int DrainCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public TimeSpan? LastDrainTimeout { get; private set; }

        public PipelineRun<int> Start(CancellationToken ct = default)
        {
            return StartCore();
        }

        public Task<PipelineRun<int>> StartAsync(CancellationToken ct = default)
        {
            return Task.FromResult(StartCore());
        }

        private PipelineRun<int> StartCore()
        {
            StartCalls++;
            _started.SetResult();
            return new PipelineRun<int>(
                _outputs.Reader,
                _completion.Task,
                () => PipelineRunState.Running,
                drain: (timeout, _) =>
                {
                    DrainCalls++;
                    LastDrainTimeout = timeout;
                    _completion.TrySetResult();
                    _outputs.Writer.TryComplete();
                    return ValueTask.CompletedTask;
                },
                dispose: () =>
                {
                    DisposeCalls++;
                    return ValueTask.CompletedTask;
                });
        }
    }

    private sealed class FaultingTypedFactory : ISmartPipeFactory<int, int>
    {
        private readonly Channel<PipelineOutput<int>> _outputs = Channel.CreateUnbounded<PipelineOutput<int>>();
        private readonly Exception _exception;

        public FaultingTypedFactory(Exception exception)
        {
            _exception = exception;
        }

        public PipelineRun<int> Start(CancellationToken ct = default)
        {
            return StartCore();
        }

        public Task<PipelineRun<int>> StartAsync(CancellationToken ct = default)
        {
            return Task.FromResult(StartCore());
        }

        private PipelineRun<int> StartCore()
        {
            _outputs.Writer.TryComplete(_exception);
            return new PipelineRun<int>(
                _outputs.Reader,
                Task.FromException(_exception),
                () => PipelineRunState.Faulted);
        }
    }

    private sealed class ExposedHostedService : SmartPipeHostedService<int, int>
    {
        public ExposedHostedService(
            ISmartPipeFactory<int, int> factory,
            SmartPipeHostedServiceOptions options,
            IHostApplicationLifetime? lifetime = null)
            : base(
                factory,
                NullLogger<SmartPipeHostedService<int, int>>.Instance,
                Options.Create(options),
                lifetime)
        {
        }

        public Task ExecuteForTestAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private sealed class RecordingApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public int StopApplicationCalls { get; private set; }

        public void StopApplication() => StopApplicationCalls++;
    }
}
