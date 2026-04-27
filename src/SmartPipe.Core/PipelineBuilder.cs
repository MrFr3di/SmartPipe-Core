namespace SmartPipe.Core;

/// <summary>Fluent API for declarative pipeline construction.</summary>
public static class PipelineBuilder
{
    /// <summary>Start building from a source.</summary>
    public static PipelineBuilder<T> From<T>(ISource<T> source) => new(source);
}

/// <summary>Pipeline builder with input type.</summary>
public class PipelineBuilder<TInput>
{
    private readonly ISource<TInput> _source;

    internal PipelineBuilder(ISource<TInput> source) => _source = source;

    /// <summary>Add a transformer (ITransformer).</summary>
    public PipelineBuilder<TInput, TOutput> Transform<TOutput>(ITransformer<TInput, TOutput> transformer)
    {
        var channel = new SmartPipeChannel<TInput, TOutput>();
        channel.AddSource(_source);
        channel.AddTransformer(transformer);
        return new PipelineBuilder<TInput, TOutput>(channel);
    }

    /// <summary>Add a lightweight middleware (Func<T, T>). Same input/output type.</summary>
    public PipelineBuilder<TInput, TInput> Transform(Func<TInput, TInput> middleware)
    {
        return Transform(new MiddlewareTransformer<TInput>(middleware));
    }
}

/// <summary>Pipeline builder with input and output types.</summary>
public class PipelineBuilder<TInput, TOutput>
{
    private readonly SmartPipeChannel<TInput, TOutput> _channel;

    internal PipelineBuilder(SmartPipeChannel<TInput, TOutput> channel) => _channel = channel;

    /// <summary>Add another transformer (same types).</summary>
    public PipelineBuilder<TInput, TOutput> Pipe(ITransformer<TInput, TOutput> transformer)
    {
        _channel.AddTransformer(transformer);
        return this;
    }

    /// <summary>Configure channel options.</summary>
    public PipelineBuilder<TInput, TOutput> WithOptions(Action<SmartPipeChannelOptions> configure)
    {
        configure(_channel.Options);
        return this;
    }

    /// <summary>Add a sink and run the pipeline.</summary>
    public async Task To(ISink<TOutput> sink, CancellationToken ct = default)
    {
        _channel.AddSink(sink);
        await _channel.RunAsync(ct);
    }
}
