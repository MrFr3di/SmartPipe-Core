#nullable enable

namespace SmartPipe.Core;

/// <summary>Fluent API for declarative pipeline construction.</summary>
public static class PipelineBuilder
{
    /// <summary>Start building from an envelope-aware source.</summary>
    /// <typeparam name="T">Payload type emitted by the source.</typeparam>
    /// <param name="source">Envelope-aware source instance.</param>
    /// <returns>A pipeline builder.</returns>
    public static PipelineBuilder<T> From<T>(IPipelineSource<T> source) => new(source);

    /// <summary>Start building from a factory that creates an envelope-aware source for each run.</summary>
    /// <typeparam name="T">Payload type emitted by the source.</typeparam>
    /// <param name="sourceFactory">Factory invoked once per runtime.</param>
    /// <param name="serviceProvider">Optional service provider passed to the factory.</param>
    /// <returns>A reusable factory-based pipeline builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sourceFactory"/> is null.</exception>
    public static PipelineBuilder<T> FromFactory<T>(
        Func<IServiceProvider?, IPipelineSource<T>> sourceFactory,
        IServiceProvider? serviceProvider = null
    ) => new(sourceFactory, serviceProvider);
}

/// <summary>Pipeline builder with input type.</summary>
/// <typeparam name="TInput">Initial source payload type.</typeparam>
public class PipelineBuilder<TInput>
{
    private readonly IPipelineSource<TInput>? _modernSource;
    private readonly Func<IServiceProvider?, IPipelineSource<TInput>>? _modernSourceFactory;
    private readonly IServiceProvider? _serviceProvider;
    private string? _pipelineId;
    private PipelineRuntimeOptions? _runtimeOptions;

    internal PipelineBuilder(IPipelineSource<TInput> source) => _modernSource = source;

    internal PipelineBuilder(
        Func<IServiceProvider?, IPipelineSource<TInput>> sourceFactory,
        IServiceProvider? serviceProvider
    )
    {
        _modernSourceFactory =
            sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _serviceProvider = serviceProvider;
    }

    /// <summary>Configures the pipeline identifier for envelope-aware typed runs.</summary>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <returns>The current builder.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pipelineId"/> is empty.</exception>
    public PipelineBuilder<TInput> WithPipelineId(string pipelineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        _pipelineId = pipelineId;
        return this;
    }

    /// <summary>Configures runtime options for envelope-aware typed runs.</summary>
    /// <param name="options">Runtime options.</param>
    /// <returns>The current builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public PipelineBuilder<TInput> WithRuntimeOptions(PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _runtimeOptions = options;
        return this;
    }

    /// <summary>Adds an envelope-aware transformer as the first typed stage.</summary>
    /// <typeparam name="TOutput">Transformer output payload type.</typeparam>
    /// <param name="transformer">Envelope-aware transformer.</param>
    /// <param name="failureOptions">Optional failure policy for this stage.</param>
    /// <param name="deadLetterOptions">Optional dead-letter persistence options for this stage.</param>
    /// <returns>A typed pipeline builder.</returns>
    public PipelineBuilder<TInput, TOutput> Transform<TOutput>(
        IPipelineTransformer<TInput, TOutput> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TInput>? deadLetterOptions = null
    )
    {
        var source =
            _modernSource
            ?? throw new InvalidOperationException(
                "A typed source is required before adding a transformer."
            );
        var spec = new TypedPipelineSpec<TInput, TInput>(
            _pipelineId ?? $"pipeline-{Guid.NewGuid():N}",
            source,
            [],
            runtimeOptions: _runtimeOptions,
            forcePipelineId: _pipelineId is not null
        );
        return new PipelineBuilder<TInput, TOutput>(
            spec.AddStage(transformer, failureOptions, deadLetterOptions));
    }

    /// <summary>Adds an envelope-aware transformer factory as the first typed stage.</summary>
    /// <typeparam name="TOutput">Transformer output payload type.</typeparam>
    /// <param name="transformerFactory">Factory invoked once per runtime.</param>
    /// <returns>A reusable typed pipeline builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transformerFactory"/> is null.</exception>
    public PipelineBuilder<TInput, TOutput> TransformFactory<TOutput>(
        Func<IServiceProvider?, IPipelineTransformer<TInput, TOutput>> transformerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(transformerFactory);
        if (_modernSourceFactory is null)
            throw new InvalidOperationException(
                "TransformFactory requires a source registered with PipelineBuilder.FromFactory."
            );

        return new PipelineBuilder<TInput, TOutput>(
            () =>
            {
                var source =
                    _modernSourceFactory(_serviceProvider)
                    ?? throw new InvalidOperationException("The source factory returned null.");
                var transformer =
                    transformerFactory(_serviceProvider)
                    ?? throw new InvalidOperationException(
                        "The transformer factory returned null."
                    );
                var spec = new TypedPipelineSpec<TInput, TInput>(
                    _pipelineId ?? $"pipeline-{Guid.NewGuid():N}",
                    source,
                    [],
                    isFactoryBased: true,
                    runtimeOptions: _runtimeOptions,
                    forcePipelineId: _pipelineId is not null
                );
                return spec.AddStage(transformer);
            },
            _serviceProvider
        );
    }

    /// <summary>Add a lightweight middleware (<see cref="Func{TInput, TInput}"/>). Same input/output type.</summary>
    /// <param name="middleware">Middleware delegate.</param>
    /// <returns>A pipeline builder with the same input and output type.</returns>
    public PipelineBuilder<TInput, TInput> Transform(Func<TInput, TInput> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return Transform(PipelineTransformer.FromFunc<TInput, TInput>(
            (value, ct) => ValueTask.FromResult(middleware(value))));
    }
}

/// <summary>Pipeline builder with input and output types.</summary>
/// <typeparam name="TInput">Initial source payload type.</typeparam>
/// <typeparam name="TOutput">Current output payload type.</typeparam>
public class PipelineBuilder<TInput, TOutput>
{
    private readonly TypedPipelineSpec<TInput, TOutput>? _typedSpec;
    private readonly Func<TypedPipelineSpec<TInput, TOutput>>? _typedSpecFactory;
    private readonly IServiceProvider? _serviceProvider;

    internal PipelineBuilder(TypedPipelineSpec<TInput, TOutput> typedSpec) =>
        _typedSpec = typedSpec;

    internal PipelineBuilder(
        Func<TypedPipelineSpec<TInput, TOutput>> typedSpecFactory,
        IServiceProvider? serviceProvider
    )
    {
        _typedSpecFactory =
            typedSpecFactory ?? throw new ArgumentNullException(nameof(typedSpecFactory));
        _serviceProvider = serviceProvider;
    }

    /// <summary>Adds another envelope-aware typed stage.</summary>
    /// <typeparam name="TNext">Next stage output payload type.</typeparam>
    /// <param name="transformer">Envelope-aware transformer.</param>
    /// <param name="failureOptions">Optional failure policy for this stage.</param>
    /// <param name="deadLetterOptions">Optional dead-letter persistence options for this stage.</param>
    /// <returns>A typed pipeline builder whose current output type is <typeparamref name="TNext"/>.</returns>
    public PipelineBuilder<TInput, TNext> Transform<TNext>(
        IPipelineTransformer<TOutput, TNext> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TOutput>? deadLetterOptions = null
    )
    {
        if (_typedSpec is null)
            throw new InvalidOperationException(
                "Typed Transform is available only for envelope-aware pipelines."
            );

        return new PipelineBuilder<TInput, TNext>(
            _typedSpec.AddStage(transformer, failureOptions, deadLetterOptions));
    }

    /// <summary>Adds another envelope-aware typed stage using a factory invoked for each runtime.</summary>
    /// <typeparam name="TNext">Next stage output payload type.</typeparam>
    /// <param name="transformerFactory">Factory invoked once per runtime.</param>
    /// <returns>A reusable typed pipeline builder whose current output type is <typeparamref name="TNext"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transformerFactory"/> is null.</exception>
    public PipelineBuilder<TInput, TNext> TransformFactory<TNext>(
        Func<IServiceProvider?, IPipelineTransformer<TOutput, TNext>> transformerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(transformerFactory);
        if (_typedSpecFactory is not null)
        {
            return new PipelineBuilder<TInput, TNext>(
                () =>
                {
                    var transformer =
                        transformerFactory(_serviceProvider)
                        ?? throw new InvalidOperationException(
                            "The transformer factory returned null."
                        );
                    return _typedSpecFactory().AddStage(transformer);
                },
                _serviceProvider
            );
        }

        throw new InvalidOperationException(
            "TransformFactory requires a reusable pipeline created with PipelineBuilder.FromFactory.");
    }

    /// <summary>Adds an observer to an envelope-aware typed pipeline.</summary>
    /// <param name="observer">Observer instance.</param>
    /// <param name="reliability">Observer reliability category.</param>
    /// <param name="failurePolicy">Policy used when the observer throws.</param>
    /// <returns>The current builder with observer configuration appended.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="observer"/> is null.</exception>
    public PipelineBuilder<TInput, TOutput> WithObserver(
        IPipelineObserver observer,
        ObserverReliability reliability = ObserverReliability.BestEffort,
        ObserverFailurePolicy failurePolicy = ObserverFailurePolicy.Log
    )
    {
        ArgumentNullException.ThrowIfNull(observer);
        var registration = new PipelineObserverRegistration(observer, reliability, failurePolicy);

        if (_typedSpecFactory is not null)
        {
            return new PipelineBuilder<TInput, TOutput>(
                () => _typedSpecFactory().WithObserver(registration),
                _serviceProvider
            );
        }

        if (_typedSpec is null)
            throw new InvalidOperationException(
                "Observers are available only for envelope-aware typed pipelines."
            );

        return new PipelineBuilder<TInput, TOutput>(_typedSpec.WithObserver(registration));
    }

    /// <summary>Configures the pipeline identifier for an envelope-aware typed pipeline.</summary>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <returns>A builder with the configured pipeline identifier.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pipelineId"/> is empty.</exception>
    public PipelineBuilder<TInput, TOutput> WithPipelineId(string pipelineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        if (_typedSpecFactory is not null)
        {
            return new PipelineBuilder<TInput, TOutput>(
                () => _typedSpecFactory().WithPipelineId(pipelineId),
                _serviceProvider
            );
        }

        if (_typedSpec is null)
            throw new InvalidOperationException(
                "Pipeline identifiers are available only for envelope-aware typed pipelines."
            );

        return new PipelineBuilder<TInput, TOutput>(_typedSpec.WithPipelineId(pipelineId));
    }

    /// <summary>Configures runtime options for an envelope-aware typed pipeline.</summary>
    /// <param name="options">Runtime options.</param>
    /// <returns>A builder with runtime options configured.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public PipelineBuilder<TInput, TOutput> WithRuntimeOptions(PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (_typedSpecFactory is not null)
        {
            return new PipelineBuilder<TInput, TOutput>(
                () => _typedSpecFactory().WithRuntimeOptions(options),
                _serviceProvider
            );
        }

        if (_typedSpec is null)
            throw new InvalidOperationException(
                "Runtime options are available only for envelope-aware typed pipelines."
            );

        return new PipelineBuilder<TInput, TOutput>(_typedSpec.WithRuntimeOptions(options));
    }

    /// <summary>Starts the envelope-aware pipeline without an attached sink.</summary>
    /// <param name="ct">Cancellation token linked to the run.</param>
    /// <returns>A single-use pipeline run handle.</returns>
    public PipelineRun<TOutput> Run(CancellationToken ct = default)
    {
        if (_typedSpec is null && _typedSpecFactory is null)
            throw new InvalidOperationException("No typed pipeline has been configured.");

        return StartTypedRun(null, sinkIsFactoryBased: false, ct);
    }

    /// <summary>Adds an envelope-aware sink and starts the typed pipeline.</summary>
    /// <param name="sink">Envelope-aware sink instance.</param>
    /// <param name="ct">Cancellation token linked to the run.</param>
    /// <returns>A single-use pipeline run handle.</returns>
    public PipelineRun<TOutput> To(IPipelineSink<TOutput> sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_typedSpec is null && _typedSpecFactory is null)
            throw new InvalidOperationException(
                "Envelope-aware sinks require an envelope-aware typed pipeline."
            );

        return StartTypedRun(sink, sinkIsFactoryBased: false, ct);
    }

    /// <summary>Adds an envelope-aware sink factory and starts the typed pipeline.</summary>
    /// <param name="sinkFactory">Factory invoked once per runtime.</param>
    /// <param name="ct">Cancellation token linked to the run.</param>
    /// <returns>A single-use pipeline run handle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sinkFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when called on a non-factory pipeline.</exception>
    public PipelineRun<TOutput> ToFactory(
        Func<IServiceProvider?, IPipelineSink<TOutput>> sinkFactory,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(sinkFactory);
        if (_typedSpec is null && _typedSpecFactory is null)
            throw new InvalidOperationException(
                "Envelope-aware sink factories require an envelope-aware typed pipeline."
            );

        if (_typedSpecFactory is null)
            throw new InvalidOperationException(
                "ToFactory requires a reusable pipeline created with PipelineBuilder.FromFactory. Use .To(sink) for instance pipelines."
            );

        var sink =
            sinkFactory(_serviceProvider)
            ?? throw new InvalidOperationException("The sink factory returned null.");
        return StartTypedRun(sink, sinkIsFactoryBased: true, ct);
    }

    private PipelineRun<TOutput> StartTypedRun(
        IPipelineSink<TOutput>? sink,
        bool sinkIsFactoryBased,
        CancellationToken ct
    )
    {
        var typedSpec =
            _typedSpec
            ?? _typedSpecFactory?.Invoke()
            ?? throw new InvalidOperationException("No typed pipeline has been configured.");
        var definition = typedSpec.CreateDefinition(sink, sinkIsFactoryBased);
        var executionPlan = PipelineExecutionPlan.Compile(definition);
        var runtime = new PipelineRuntime(executionPlan);
        var executor = new TypedPipelineExecutor<TInput, TOutput>(runtime, typedSpec, sink, ct);
        return executor.Start();
    }

}
