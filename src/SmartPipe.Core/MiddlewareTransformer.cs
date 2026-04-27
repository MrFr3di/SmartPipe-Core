namespace SmartPipe.Core;

/// <summary>
/// Lightweight transformer that wraps a Func<T,T> delegate.
/// Provides middleware-style simplicity without sacrificing resilience.
/// </summary>
/// <typeparam name="T">Data type.</typeparam>
public class MiddlewareTransformer<T> : ITransformer<T, T>
{
    private readonly Func<T, T> _func;

    /// <summary>Create from a delegate.</summary>
    public MiddlewareTransformer(Func<T, T> func) => _func = func ?? throw new ArgumentNullException(nameof(func));

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
