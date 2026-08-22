using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Applies an owned child transform when a synchronous predicate matches.</summary>
public class ConditionalTransform<T> : IPipelineTransformer<T, T>
{
    private readonly Func<T, bool> _condition;
    private readonly IPipelineTransformer<T, T> _transform;

    /// <summary>Initializes a conditional transform.</summary>
    public ConditionalTransform(Func<T, bool> condition, IPipelineTransformer<T, T> transform)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => _transform.InitializeAsync(ct);

    /// <inheritdoc />
    public ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default) =>
        _condition(envelope.Payload)
            ? _transform.TransformAsync(envelope, ct)
            : ValueTask.FromResult(StageResult<T>.Success(envelope.Payload));

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _transform.DisposeAsync();
}
