using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Applies transform only when condition is met. Otherwise passes item through unchanged.</summary>
public class ConditionalTransform<T> : ITransformer<T, T>
{
    private readonly Func<T, bool> _condition;
    private readonly ITransformer<T, T> _transform;

    public ConditionalTransform(Func<T, bool> condition, ITransformer<T, T> transform)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
    }

    public Task InitializeAsync(CancellationToken ct = default) => _transform.InitializeAsync(ct);

    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        if (_condition(ctx.Payload))
            return await _transform.TransformAsync(ctx, ct);
        return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);
    }

    public Task DisposeAsync() => _transform.DisposeAsync();
}
