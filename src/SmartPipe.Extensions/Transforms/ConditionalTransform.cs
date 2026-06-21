using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Conditionally applies a transform based on a predicate.
/// If the condition is met, the transform is applied; otherwise the item passes through unchanged.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class ConditionalTransform<T> : IPipelineTransformer<T, T>
{
    private readonly Func<T, bool> _condition;
    private readonly IPipelineTransformer<T, T> _transform;

    /// <summary>
    /// Initializes a new instance of <see cref="ConditionalTransform{T}"/>.
    /// </summary>
    /// <param name="condition">The predicate to determine if the transform should be applied.</param>
    /// <param name="transform">The transform to apply when the condition is true.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="condition"/> or <paramref name="transform"/> is null.</exception>
    public ConditionalTransform(Func<T, bool> condition, IPipelineTransformer<T, T> transform)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => _transform.InitializeAsync(ct);

    /// <inheritdoc/>
    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default
    )
    {
        if (_condition(envelope.Payload))
            return await _transform.TransformAsync(envelope, ct);
        return StageResult<T>.Success(envelope.Payload);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _transform.DisposeAsync();
}
