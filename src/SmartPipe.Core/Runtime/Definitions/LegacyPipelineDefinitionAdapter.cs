#nullable enable

namespace SmartPipe.Core;

internal sealed class LegacyPipelineDefinitionAdapter<TInput, TOutput>
{
    private readonly Func<PipelineKey, PipelineDefinitionBuilder<TInput, TOutput>> _createBuilder;
    private readonly PipelineRuntimeOptionsSnapshot _runtimeOptions;
    private readonly PipelineObserverRegistration[] _observers;
    private readonly ComponentOwnershipOptions _ownershipOptions;
    private readonly PipelineStartClaim _startClaim;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string? _pipelineId;
    private readonly int _stageCount;

    private LegacyPipelineDefinitionAdapter(
        Func<PipelineKey, PipelineDefinitionBuilder<TInput, TOutput>> createBuilder,
        PipelineRuntimeOptionsSnapshot runtimeOptions,
        PipelineObserverRegistration[] observers,
        ComponentOwnershipOptions ownershipOptions,
        IServiceProvider? serviceProvider,
        string? pipelineId,
        int stageCount)
    {
        _createBuilder = createBuilder;
        _runtimeOptions = runtimeOptions;
        _observers = observers;
        _ownershipOptions = ownershipOptions;
        _serviceProvider = serviceProvider;
        _pipelineId = pipelineId;
        _stageCount = stageCount;
        _startClaim = new PipelineStartClaim();
    }

    internal static LegacyPipelineDefinitionAdapter<TInput, TInput> FromInstance(
        IPipelineSource<TInput> source,
        ComponentOwnershipOptions? ownershipOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ownership = CopyOwnershipOptions(ownershipOptions);
        var descriptor = CreateInstanceDescriptor(source, ownership);
        return new(
            key => PipelineDefinitionBuilder.From(key, descriptor).AsTyped(),
            PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions()),
            [],
            ownership,
            serviceProvider: null,
            pipelineId: null,
            stageCount: 0);
    }

    internal static LegacyPipelineDefinitionAdapter<TInput, TInput> FromFactory(
        Func<IServiceProvider?, IPipelineSource<TInput>> sourceFactory,
        IServiceProvider? serviceProvider,
        ComponentOwnershipOptions? ownershipOptions = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        var ownership = CopyOwnershipOptions(ownershipOptions);
        var descriptor = CreateFactoryDescriptor(sourceFactory, serviceProvider);
        return new(
            key => PipelineDefinitionBuilder.From(key, descriptor).AsTyped(),
            PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions()),
            [],
            ownership,
            serviceProvider,
            pipelineId: null,
            stageCount: 0);
    }

    internal LegacyPipelineDefinitionAdapter<TInput, TNext> TransformInstance<TNext>(
        IPipelineTransformer<TOutput, TNext> transformer,
        StageFailureOptions? failureOptions,
        StageDeadLetterOptions<TOutput>? deadLetterOptions)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        var stageKey = new PipelineStageKey($"stage-{_stageCount + 1}");
        var descriptor = CreateInstanceDescriptor(transformer, _ownershipOptions);
        var failureSnapshot = StageFailureOptionsSnapshot.Create(
            failureOptions ?? StageFailureOptions.Default);
        var stageName = transformer.GetType().Name;

        return Next<TNext>(
            key => _createBuilder(key).Transform(
                stageKey,
                descriptor,
                failureSnapshot.Materialize(),
                deadLetterOptions,
                stageName));
    }

    internal LegacyPipelineDefinitionAdapter<TInput, TNext> TransformFactory<TNext>(
        Func<IServiceProvider?, IPipelineTransformer<TOutput, TNext>> transformerFactory)
    {
        ArgumentNullException.ThrowIfNull(transformerFactory);
        var stageKey = new PipelineStageKey($"stage-{_stageCount + 1}");
        var descriptor = CreateFactoryDescriptor(transformerFactory, _serviceProvider);

        return Next<TNext>(
            key => _createBuilder(key).Transform(
                stageKey,
                descriptor,
                failureOptions: null,
                deadLetterOptions: null,
                stageName: stageKey.Value));
    }

    internal LegacyPipelineDefinitionAdapter<TInput, TOutput> WithObserver(
        PipelineObserverRegistration observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var observers = new PipelineObserverRegistration[_observers.Length + 1];
        Array.Copy(_observers, observers, _observers.Length);
        observers[^1] = observer;
        return Clone(observers: observers);
    }

    internal LegacyPipelineDefinitionAdapter<TInput, TOutput> WithPipelineId(string pipelineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        return Clone(pipelineId: pipelineId);
    }

    internal LegacyPipelineDefinitionAdapter<TInput, TOutput> WithRuntimeOptions(
        PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Clone(runtimeOptions: PipelineRuntimeOptionsSnapshot.Create(options));
    }

    internal PipelineRun<TOutput> Start(CancellationToken cancellationToken) =>
        StartCore(sink: null, cancellationToken);

    internal PipelineRun<TOutput> Start(
        IPipelineSink<TOutput> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return StartCore(CreateInstanceDescriptor(sink, _ownershipOptions), cancellationToken);
    }

    internal PipelineRun<TOutput> Start(
        Func<IServiceProvider?, IPipelineSink<TOutput>> sinkFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sinkFactory);
        return StartCore(
            CreateFactoryDescriptor(sinkFactory, _serviceProvider),
            cancellationToken);
    }

    private PipelineRun<TOutput> StartCore(
        PipelineComponent<IPipelineSink<TOutput>>? sink,
        CancellationToken cancellationToken)
    {
        var key = new PipelineKey(_pipelineId ?? $"pipeline-{Guid.NewGuid():N}");
        var builder = _createBuilder(key)
            .WithRuntimeOptions(_runtimeOptions.Materialize())
            .WithForcePipelineId(_pipelineId is not null);
        foreach (var observer in _observers)
        {
            builder = builder.WithObserver(
                observer.Observer,
                observer.Reliability,
                observer.FailurePolicy);
        }

        var definition = sink is null
            ? builder.Build(_startClaim)
            : builder.To(sink, _startClaim);
        var context = new PipelineActivationContext(
            key,
            Guid.NewGuid(),
            _serviceProvider);
        return definition.StartDeferred(context, cancellationToken).Run;
    }

    private LegacyPipelineDefinitionAdapter<TInput, TNext> Next<TNext>(
        Func<PipelineKey, PipelineDefinitionBuilder<TInput, TNext>> createBuilder) =>
        new(
            createBuilder,
            _runtimeOptions,
            Copy(_observers),
            _ownershipOptions,
            _serviceProvider,
            _pipelineId,
            _stageCount + 1);

    private LegacyPipelineDefinitionAdapter<TInput, TOutput> Clone(
        PipelineRuntimeOptionsSnapshot? runtimeOptions = null,
        PipelineObserverRegistration[]? observers = null,
        string? pipelineId = null) =>
        new(
            _createBuilder,
            runtimeOptions ?? _runtimeOptions,
            observers ?? Copy(_observers),
            _ownershipOptions,
            _serviceProvider,
            pipelineId ?? _pipelineId,
            _stageCount);

    private static PipelineComponent<TComponent> CreateFactoryDescriptor<TComponent>(
        Func<IServiceProvider?, TComponent> factory,
        IServiceProvider? serviceProvider)
        where TComponent : class =>
        PipelineComponent.RuntimeOwned<TComponent>(
            (_, _) => ValueTask.FromResult(factory(serviceProvider)));

    private static PipelineComponent<TComponent> CreateInstanceDescriptor<TComponent>(
        TComponent instance,
        ComponentOwnershipOptions ownershipOptions)
        where TComponent : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var runtimeOwnsInstance =
            instance is not IPipelineComponentDescriptor descriptor
            || descriptor.Lifetime != PipelineComponentLifetime.SingletonExternal
            || ownershipOptions.DisposeExternalComponents;

        return new(
            runtimeOwnsInstance
                ? PipelineComponentOwnership.RuntimeOwned
                : PipelineComponentOwnership.ExternallyOwned,
            initialize: true,
            isPerRun: false,
            (_, _) => ValueTask.FromResult(instance));
    }

    private static ComponentOwnershipOptions CopyOwnershipOptions(
        ComponentOwnershipOptions? options) =>
        new()
        {
            DisposeExternalComponents =
                (options ?? ComponentOwnershipOptions.Default).DisposeExternalComponents,
        };

    private static T[] Copy<T>(T[] values)
    {
        var copy = new T[values.Length];
        Array.Copy(values, copy, values.Length);
        return copy;
    }
}
