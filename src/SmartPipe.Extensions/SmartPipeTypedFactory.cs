#nullable enable

using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>Immutable typed SmartPipe pipeline definition registered in DI.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public interface ISmartPipeDefinition<TInput, TOutput>
{
    /// <summary>Gets the stable pipeline identifier.</summary>
    string PipelineId { get; }
}

/// <summary>Creates a fresh typed pipeline run from a DI-registered definition.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public interface ISmartPipeFactory<TInput, TOutput>
{
    /// <summary>Creates and starts a fresh typed pipeline runtime.</summary>
    /// <param name="ct">Cancellation token linked to the run.</param>
    /// <returns>A started pipeline run.</returns>
    PipelineRun<TOutput> Start(CancellationToken ct = default);

    /// <summary>Asynchronously creates and starts a fresh typed pipeline runtime.</summary>
    /// <param name="ct">Cancellation token linked to the run.</param>
    /// <returns>A task that completes with a started pipeline run.</returns>
    /// <remarks>
    /// Default interface method (DIM) so existing implementors are not source-broken.
    /// The default bridges to <see cref="Start"/>; production implementations should override.
    /// </remarks>
    Task<PipelineRun<TOutput>> StartAsync(CancellationToken ct = default) => Task.FromResult(Start(ct));
}

/// <summary>Builder used to configure a typed SmartPipe DI definition.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public sealed class SmartPipeDefinitionBuilder<TInput, TOutput>
{
    private Type? _sourceType;
    private Type? _stageType;
    private Type? _sinkType;
    private PipelineRuntimeOptions _runtimeOptions = new();

    /// <summary>Registers the source type resolved for each run scope.</summary>
    /// <typeparam name="TSource">Source implementation type.</typeparam>
    /// <returns>The current builder.</returns>
    public SmartPipeDefinitionBuilder<TInput, TOutput> UseSource<TSource>()
        where TSource : class, IPipelineSource<TInput>
    {
        _sourceType = typeof(TSource);
        return this;
    }

    /// <summary>Registers the single typed stage type resolved for each run scope.</summary>
    /// <typeparam name="TStage">Stage implementation type.</typeparam>
    /// <returns>The current builder.</returns>
    public SmartPipeDefinitionBuilder<TInput, TOutput> UseStage<TStage>()
        where TStage : class, IPipelineTransformer<TInput, TOutput>
    {
        _stageType = typeof(TStage);
        return this;
    }

    /// <summary>Registers the sink type resolved for each run scope.</summary>
    /// <typeparam name="TSink">Sink implementation type.</typeparam>
    /// <returns>The current builder.</returns>
    public SmartPipeDefinitionBuilder<TInput, TOutput> UseSink<TSink>()
        where TSink : class, IPipelineSink<TOutput>
    {
        _sinkType = typeof(TSink);
        return this;
    }

    /// <summary>Configures runtime options copied into each run.</summary>
    /// <param name="options">Runtime options.</param>
    /// <returns>The current builder.</returns>
    public SmartPipeDefinitionBuilder<TInput, TOutput> WithRuntimeOptions(
        PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runtimeOptions = options;
        return this;
    }

    internal SmartPipeDefinition<TInput, TOutput> Build(string pipelineId)
    {
        if (_sourceType is null)
            throw new InvalidOperationException("A typed SmartPipe definition requires a source.");

        if (_stageType is null)
            throw new InvalidOperationException("A typed SmartPipe definition requires a stage.");

        if (_sinkType is null)
            throw new InvalidOperationException("A typed SmartPipe definition requires a sink.");

        return new SmartPipeDefinition<TInput, TOutput>(
            pipelineId,
            _sourceType,
            _stageType,
            _sinkType,
            _runtimeOptions);
    }
}

/// <summary>Immutable typed SmartPipe pipeline definition.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public sealed class SmartPipeDefinition<TInput, TOutput>
    : ISmartPipeDefinition<TInput, TOutput>
{
    internal SmartPipeDefinition(
        string pipelineId,
        Type sourceType,
        Type stageType,
        Type sinkType,
        PipelineRuntimeOptions runtimeOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        PipelineId = pipelineId;
        SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
        StageType = stageType ?? throw new ArgumentNullException(nameof(stageType));
        SinkType = sinkType ?? throw new ArgumentNullException(nameof(sinkType));
        RuntimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
    }

    /// <inheritdoc />
    public string PipelineId { get; }

    internal Type SourceType { get; }

    internal Type StageType { get; }

    internal Type SinkType { get; }

    internal PipelineRuntimeOptions RuntimeOptions { get; }

    internal PipelineRun<TOutput> Start(IServiceProvider serviceProvider, CancellationToken ct)
    {
        var source = new ScopedPipelineSource<TInput>(
            (IPipelineSource<TInput>)serviceProvider.GetRequiredService(SourceType));
        var stage = new ScopedPipelineTransformer<TInput, TOutput>(
            (IPipelineTransformer<TInput, TOutput>)serviceProvider.GetRequiredService(StageType));
        var sink = new ScopedPipelineSink<TOutput>(
            (IPipelineSink<TOutput>)serviceProvider.GetRequiredService(SinkType));

        return PipelineBuilder
            .FromFactory(_ => source, serviceProvider)
            .TransformFactory(_ => stage)
            .WithPipelineId(PipelineId)
            .WithRuntimeOptions(RuntimeOptions)
            .ToFactory(_ => sink, ct);
    }
}

/// <summary>DI-backed typed SmartPipe factory that creates one runtime per start.</summary>
/// <typeparam name="TInput">Pipeline input payload type.</typeparam>
/// <typeparam name="TOutput">Pipeline output payload type.</typeparam>
public sealed class SmartPipeFactory<TInput, TOutput> : ISmartPipeFactory<TInput, TOutput>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SmartPipeDefinition<TInput, TOutput> _definition;
    private readonly SmartPipeRunHealthMonitor<TInput, TOutput>? _healthMonitor;

    /// <summary>Creates a factory for a typed SmartPipe definition.</summary>
    /// <param name="scopeFactory">Scope factory used to own scoped components per run.</param>
    /// <param name="definition">Immutable typed pipeline definition.</param>
    public SmartPipeFactory(
        IServiceScopeFactory scopeFactory,
        ISmartPipeDefinition<TInput, TOutput> definition)
        : this(scopeFactory, definition, null)
    {
    }

    /// <summary>Creates a factory for a typed SmartPipe definition with health monitoring.</summary>
    /// <param name="scopeFactory">Scope factory used to own scoped components per run.</param>
    /// <param name="definition">Immutable typed pipeline definition.</param>
    /// <param name="healthMonitor">Optional typed health monitor updated when a run starts.</param>
    public SmartPipeFactory(
        IServiceScopeFactory scopeFactory,
        ISmartPipeDefinition<TInput, TOutput> definition,
        SmartPipeRunHealthMonitor<TInput, TOutput>? healthMonitor)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _definition = definition as SmartPipeDefinition<TInput, TOutput>
            ?? throw new ArgumentException(
                "The registered typed SmartPipe definition is not supported.",
                nameof(definition));
        _healthMonitor = healthMonitor;
    }

    /// <inheritdoc />
    public async Task<PipelineRun<TOutput>> StartAsync(CancellationToken ct = default)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var inner = _definition.Start(scope.ServiceProvider, ct);
            var scopedRun = new ScopedPipelineRun<TOutput>(inner, scope);
            var completion = scopedRun.CompleteAndDisposeAsync();
            _healthMonitor?.Track(inner);
            var run = new PipelineRun<TOutput>(
                inner.Outputs,
                completion,
                () => inner.State,
                inner.CancelAsync,
                inner.DrainAsync,
                inner.AbortAsync,
                scopedRun.DisposeAsync);
            return run;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public PipelineRun<TOutput> Start(CancellationToken ct = default) =>
        StartAsync(ct).GetAwaiter().GetResult();
}

internal sealed class ScopedPipelineRun<T> : IAsyncDisposable
{
    private readonly PipelineRun<T> _inner;
    private readonly AsyncServiceScope _scope;
    private int _disposed;

    public ScopedPipelineRun(PipelineRun<T> inner, AsyncServiceScope scope)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _scope = scope;
    }

    public PipelineRun<T> Inner => _inner;

    public async Task CompleteAndDisposeAsync()
    {
        try
        {
            await _inner.Completion.ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _inner.DisposeAsync().ConfigureAwait(false);
        await _scope.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ScopedPipelineSource<T> : IPipelineSource<T>, IPipelineComponentDescriptor
{
    private readonly IPipelineSource<T> _inner;

    public ScopedPipelineSource(IPipelineSource<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public PipelineComponentLifetime Lifetime => PipelineComponentLifetime.SingletonExternal;

    public bool OwnsResources => false;

    public ValueTask InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    public IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(CancellationToken ct = default) =>
        _inner.ReadEnvelopesAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ScopedPipelineTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>,
        IPipelineComponentDescriptor
{
    private readonly IPipelineTransformer<TInput, TOutput> _inner;

    public ScopedPipelineTransformer(IPipelineTransformer<TInput, TOutput> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public PipelineComponentLifetime Lifetime => PipelineComponentLifetime.SingletonExternal;

    public bool OwnsResources => false;

    public ValueTask InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default) => _inner.TransformAsync(envelope, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ScopedPipelineSink<T> : IPipelineSink<T>, IPipelineComponentDescriptor
{
    private readonly IPipelineSink<T> _inner;

    public ScopedPipelineSink(IPipelineSink<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public PipelineComponentLifetime Lifetime => PipelineComponentLifetime.SingletonExternal;

    public bool OwnsResources => false;

    public ValueTask InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default) =>
        _inner.WriteAsync(envelope, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
