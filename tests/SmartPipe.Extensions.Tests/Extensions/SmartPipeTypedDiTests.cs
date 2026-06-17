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
    public async Task DI_FactoryCreatesNewRuntimePerStart()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();

        var first = factory.Start();
        var second = factory.Start();

        first.Should().NotBeSameAs(second);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DI_ScopedStageResolvedWithinScope()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        await factory.Start().Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await factory.Start().Completion.WaitAsync(TimeSpan.FromSeconds(5));

        recorder.StageScopeIds.Should().HaveCount(2);
        recorder.StageScopeIds.Should().OnlyHaveUniqueItems();
        recorder.SinkScopeIds.Should().BeEquivalentTo(recorder.StageScopeIds);
    }

    [Fact]
    public async Task DI_ScopedSinkDisposedWithScope()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        await factory.Start().Completion.WaitAsync(TimeSpan.FromSeconds(5));

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

        var run = factory.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await run.DisposeAsync();

        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DI_Factory_CompletionAndManualDisposeRace_DisposesScopeOnce()
    {
        var services = CreateTypedPipelineServices();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var recorder = provider.GetRequiredService<TypedDiRecorder>();

        var run = factory.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => run.DisposeAsync().AsTask()));

        recorder.DisposedSinkScopeIds.Should().HaveCount(1);
        recorder.DisposedMarkerScopeIds.Should().HaveCount(1);
    }

    [Fact]
    public void DI_ValidateScopes_DoesNotThrow()
    {
        var services = CreateTypedPipelineServices();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var run = factory.Start();
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
    public async Task HostedService_FaultBehaviorRethrow_Rethrows()
    {
        var exception = new InvalidOperationException("pipeline failed");
        var hostedService = new ExposedHostedService(
            new FaultingTypedFactory(exception),
            new SmartPipeHostedServiceOptions
            {
                FailureBehavior = SmartPipeHostedFailureBehavior.Rethrow,
            });

        var act = () => hostedService.ExecuteForTestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("pipeline failed");
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

    private sealed class TypedDiRecorder
    {
        public List<Guid> StageScopeIds { get; } = [];

        public List<Guid> SinkScopeIds { get; } = [];

        public List<Guid> DisposedSinkScopeIds { get; } = [];

        public List<Guid> DisposedMarkerScopeIds { get; } = [];
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
