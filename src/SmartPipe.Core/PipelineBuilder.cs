namespace SmartPipe.Core;

/// <summary>Fluent API for declarative pipeline construction.</summary>
public static class PipelineBuilder
{
    /// <summary>Start building from a source.</summary>
    /// <typeparam name="T">Source element type.</typeparam>
    /// <param name="source">Data source.</param>
    /// <returns>Pipeline builder for the given source type.</returns>
    public static PipelineBuilder<T> From<T>(ISource<T> source) => new(source);
}

/// <summary>Pipeline builder with input type.</summary>
/// <typeparam name="TInput">Input element type.</typeparam>
public class PipelineBuilder<TInput>
{
    private readonly ISource<TInput> _source;

    internal PipelineBuilder(ISource<TInput> source) => _source = source;

    /// <summary>Add a transformer to the pipeline.</summary>
    /// <typeparam name="TOutput">Output element type.</typeparam>
    /// <param name="transformer">Transformer to add.</param>
    /// <returns>Builder with both input and output types.</returns>
    public PipelineBuilder<TInput, TOutput> Transform<TOutput>(ITransformer<TInput, TOutput> transformer)
    {
        var channel = new SmartPipeChannel<TInput, TOutput>();
        channel.AddSource(_source);
        channel.AddTransformer(transformer);
        return new PipelineBuilder<TInput, TOutput>(channel);
    }
}

/// <summary>Pipeline builder with input and output types.</summary>
/// <typeparam name="TInput">Input element type.</typeparam>
/// <typeparam name="TOutput">Output element type.</typeparam>
public class PipelineBuilder<TInput, TOutput>
{
    private readonly SmartPipeChannel<TInput, TOutput> _channel;

    internal PipelineBuilder(SmartPipeChannel<TInput, TOutput> channel) => _channel = channel;

    /// <summary>Add another transformer (same types).</summary>
    /// <param name="transformer">Transformer to add.</param>
    /// <returns>This builder for chaining.</returns>
    public PipelineBuilder<TInput, TOutput> Pipe(ITransformer<TInput, TOutput> transformer)
    {
        _channel.AddTransformer(transformer);
        return this;
    }

    /// <summary>Configure channel options.</summary>
    /// <param name="configure">Options configuration delegate.</param>
    /// <returns>This builder for chaining.</returns>
    public PipelineBuilder<TInput, TOutput> WithOptions(Action<SmartPipeChannelOptions> configure)
    {
        configure(_channel.Options);
        return this;
    }

    /// <summary>Add a sink and run the pipeline.</summary>
    /// <param name="sink">Data sink.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task To(ISink<TOutput> sink, CancellationToken ct = default)
    {
        _channel.AddSink(sink);
        await _channel.RunAsync(ct);
    }
}
