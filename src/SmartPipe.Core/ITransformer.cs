using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Transforms input data into output data. Uses ValueTask for minimal allocations in hot path.</summary>
/// <typeparam name="TInput">Input element type.</typeparam>
/// <typeparam name="TOutput">Output element type.</typeparam>
public interface ITransformer<TInput, TOutput>
{
    /// <summary>Async initialization (load model, dictionary, etc.).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Transform a single element. Returns ProcessingResult (no exceptions thrown).</summary>
    /// <param name="ctx">Input context with payload and metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ProcessingResult<TOutput>> TransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default);

    /// <summary>Release resources.</summary>
    Task DisposeAsync();
}
