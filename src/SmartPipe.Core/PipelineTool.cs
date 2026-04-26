namespace SmartPipe.Core;

/// <summary>Wrapper for using a pipeline as a tool in AI agent frameworks (Semantic Kernel, AutoGen).
/// Implements Microsoft Agent Framework integration pattern.</summary>
/// <typeparam name="TInput">Input type for the tool.</typeparam>
/// <typeparam name="TOutput">Output type from the tool.</typeparam>
public class PipelineTool<TInput, TOutput>
{
    private readonly SmartPipeChannel<TInput, TOutput> _channel;

    /// <summary>Tool name for agent registration.</summary>
    public string Name { get; }

    /// <summary>Tool description for agent documentation.</summary>
    public string Description { get; }

    /// <summary>Create a pipeline tool.</summary>
    /// <param name="name">Tool name.</param>
    /// <param name="description">Tool description.</param>
    /// <param name="channel">Underlying pipeline channel.</param>
    public PipelineTool(string name, string description, SmartPipeChannel<TInput, TOutput> channel)
    {
        Name = name;
        Description = description;
        _channel = channel;
    }

    /// <summary>Execute the tool with a single input (semantic function signature).</summary>
    /// <param name="input">Input data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    public async Task<ProcessingResult<TOutput>> ExecuteAsync(TInput input, CancellationToken ct = default)
    {
        var ctx = new ProcessingContext<TInput>(input);
        return await _channel.ProcessSingleAsync(ctx, ct);
    }
}
