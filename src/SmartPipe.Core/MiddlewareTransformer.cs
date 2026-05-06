#nullable enable

namespace SmartPipe.Core;

/// <summary>
/// Lightweight transformer that wraps a <see cref="Func{T, T}"/> delegate.
/// Provides middleware-style simplicity without sacrificing resilience.
/// Implements <see cref="ITransformer{T, T}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class MiddlewareTransformer<T> : ITransformer<T, T>
{
    private readonly Func<T, T> _func;

    /// <summary>Create from a delegate.</summary>
    public MiddlewareTransformer(Func<T, T> func) => _func = func ?? throw new ArgumentNullException(nameof(func));

    /// <summary>
    /// Initializes the middleware transformer.
    /// This implementation completes immediately as no initialization is required.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Transforms the input payload using the configured delegate.
    /// </summary>
    /// <param name="ctx">Processing context containing the input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ValueTask containing the processing result with the transformed output.</returns>
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        try
        {
            var result = _func(ctx.Payload);
            return ValueTask.FromResult(ProcessingResult<T>.Success(result, ctx.TraceId));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(
                ProcessingResult<T>.Failure(
                    new SmartPipeError(ex.Message, ErrorType.Permanent, "Middleware", ex), ctx.TraceId));
        }
    }

    /// <summary>
    /// Disposes the middleware transformer.
    /// This implementation completes immediately as no disposal is required.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task DisposeAsync() => Task.CompletedTask;
}
