#nullable enable

namespace SmartPipe.Core;

/// <summary>Fluent API for declarative pipeline construction.</summary>
public static class PipelineBuilder
{
    /// <summary>Start building from an envelope-aware source.</summary>
    public static PipelineBuilder<T> From<T>(IPipelineSource<T> source) => new(source);

    /// <summary>Start building from a factory that creates an envelope-aware source for each run.</summary>
    public static PipelineBuilder<T> FromFactory<T>(
        Func<IServiceProvider?, IPipelineSource<T>> sourceFactory,
        IServiceProvider? serviceProvider = null) =>
        new(sourceFactory, serviceProvider);
}

/// <summary>Pipeline builder with input type.</summary>
public class PipelineBuilder<TInput>
{
    private LegacyPipelineDefinitionAdapter<TInput, TInput> _adapter;
    private readonly bool _factoryBased;

    internal PipelineBuilder(IPipelineSource<TInput> source)
    {
        _adapter = LegacyPipelineDefinitionAdapter<TInput, TInput>.FromInstance(source);
    }

    internal PipelineBuilder(
        Func<IServiceProvider?, IPipelineSource<TInput>> sourceFactory,
        IServiceProvider? serviceProvider)
    {
        _adapter = LegacyPipelineDefinitionAdapter<TInput, TInput>.FromFactory(
            sourceFactory,
            serviceProvider);
        _factoryBased = true;
    }

    /// <summary>Configures the pipeline identifier for envelope-aware typed runs.</summary>
    public PipelineBuilder<TInput> WithPipelineId(string pipelineId)
    {
        _adapter = _adapter.WithPipelineId(pipelineId);
        return this;
    }

    /// <summary>Configures runtime options for envelope-aware typed runs.</summary>
    public PipelineBuilder<TInput> WithRuntimeOptions(PipelineRuntimeOptions options)
    {
        _adapter = _adapter.WithRuntimeOptions(options);
        return this;
    }

    /// <summary>Adds an envelope-aware transformer as the first typed stage.</summary>
    public PipelineBuilder<TInput, TOutput> Transform<TOutput>(
        IPipelineTransformer<TInput, TOutput> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TInput>? deadLetterOptions = null)
    {
        if (_factoryBased)
        {
            throw new InvalidOperationException(
                "Typed Transform is available only for instance-based envelope-aware pipelines. Use TransformFactory for factory pipelines.");
        }

        return new(_adapter.TransformInstance(
            transformer,
            failureOptions,
            deadLetterOptions), factoryBased: false);
    }

    /// <summary>Adds an envelope-aware transformer factory as the first typed stage.</summary>
    public PipelineBuilder<TInput, TOutput> TransformFactory<TOutput>(
        Func<IServiceProvider?, IPipelineTransformer<TInput, TOutput>> transformerFactory)
    {
        if (!_factoryBased)
        {
            throw new InvalidOperationException(
                "TransformFactory requires a source registered with PipelineBuilder.FromFactory.");
        }

        return new(_adapter.TransformFactory(transformerFactory), factoryBased: true);
    }

    /// <summary>Add a lightweight middleware with the same input and output type.</summary>
    public PipelineBuilder<TInput, TInput> Transform(Func<TInput, TInput> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return Transform(PipelineTransformer.FromFunc<TInput, TInput>(
            (value, _) => ValueTask.FromResult(middleware(value))));
    }
}

/// <summary>Pipeline builder with input and output types.</summary>
public class PipelineBuilder<TInput, TOutput>
{
    private readonly LegacyPipelineDefinitionAdapter<TInput, TOutput> _adapter;
    private readonly bool _factoryBased;

    internal PipelineBuilder(
        LegacyPipelineDefinitionAdapter<TInput, TOutput> adapter,
        bool factoryBased)
    {
        _adapter = adapter;
        _factoryBased = factoryBased;
    }

    /// <summary>Adds another envelope-aware typed stage.</summary>
    public PipelineBuilder<TInput, TNext> Transform<TNext>(
        IPipelineTransformer<TOutput, TNext> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TOutput>? deadLetterOptions = null)
    {
        if (_factoryBased)
        {
            throw new InvalidOperationException(
                "Typed Transform is available only for instance-based envelope-aware pipelines. Use TransformFactory for factory pipelines.");
        }

        return new(
            _adapter.TransformInstance(transformer, failureOptions, deadLetterOptions),
            factoryBased: false);
    }

    /// <summary>Adds another envelope-aware typed stage using a factory invoked for each runtime.</summary>
    public PipelineBuilder<TInput, TNext> TransformFactory<TNext>(
        Func<IServiceProvider?, IPipelineTransformer<TOutput, TNext>> transformerFactory)
    {
        if (!_factoryBased)
        {
            throw new InvalidOperationException(
                "TransformFactory requires a reusable pipeline created with PipelineBuilder.FromFactory. Use .Transform(instance) for instance pipelines.");
        }

        return new(_adapter.TransformFactory(transformerFactory), factoryBased: true);
    }

    /// <summary>Adds an observer to an envelope-aware typed pipeline.</summary>
    public PipelineBuilder<TInput, TOutput> WithObserver(
        IPipelineObserver observer,
        ObserverReliability reliability = ObserverReliability.BestEffort,
        ObserverFailurePolicy failurePolicy = ObserverFailurePolicy.Log)
    {
        var registration = PipelineDefinitionBuilder.CreateObserverRegistration(
            observer,
            reliability,
            failurePolicy);
        return new(_adapter.WithObserver(registration), _factoryBased);
    }

    /// <summary>Configures the pipeline identifier for an envelope-aware typed pipeline.</summary>
    public PipelineBuilder<TInput, TOutput> WithPipelineId(string pipelineId) =>
        new(_adapter.WithPipelineId(pipelineId), _factoryBased);

    /// <summary>Configures runtime options for an envelope-aware typed pipeline.</summary>
    public PipelineBuilder<TInput, TOutput> WithRuntimeOptions(PipelineRuntimeOptions options) =>
        new(_adapter.WithRuntimeOptions(options), _factoryBased);

    /// <summary>Starts the envelope-aware pipeline without an attached sink.</summary>
    public PipelineRun<TOutput> Run(CancellationToken ct = default) =>
        _adapter.Start(ct);

    /// <summary>Adds an envelope-aware sink and starts the typed pipeline.</summary>
    public PipelineRun<TOutput> To(
        IPipelineSink<TOutput> sink,
        CancellationToken ct = default) =>
        _adapter.Start(sink, ct);

    /// <summary>Adds an envelope-aware sink factory and starts the typed pipeline.</summary>
    public PipelineRun<TOutput> ToFactory(
        Func<IServiceProvider?, IPipelineSink<TOutput>> sinkFactory,
        CancellationToken ct = default)
    {
        if (!_factoryBased)
        {
            throw new InvalidOperationException(
                "ToFactory requires a reusable pipeline created with PipelineBuilder.FromFactory. Use .To(sink) for instance pipelines.");
        }

        return _adapter.Start(sinkFactory, ct);
    }
}
