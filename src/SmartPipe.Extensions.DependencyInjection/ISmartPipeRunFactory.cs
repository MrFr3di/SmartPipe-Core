using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Starts runs for one typed pipeline registration.</summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public interface ISmartPipeRunFactory<TInput, TOutput>
{
    /// <summary>Starts a new pipeline run asynchronously.</summary>
    /// <param name="cancellationToken">Cancellation token for startup.</param>
    /// <returns>The ready pipeline run.</returns>
    Task<PipelineRun<TOutput>> StartAsync(CancellationToken cancellationToken = default);
}
