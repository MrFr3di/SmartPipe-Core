using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Combines multiple <see cref="IPipelineTransformer{TInput, TOutput}"/> instances into a single transform.
/// Transforms are applied sequentially; if any transform fails, the failure is returned immediately.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class CompositeTransform<T> : IPipelineTransformer<T, T>
{
    private readonly IPipelineTransformer<T, T>[] _transforms;

    /// <summary>
    /// Initializes a new instance of <see cref="CompositeTransform{T}"/> with the specified transforms.
    /// </summary>
    /// <param name="transforms">The transforms to apply sequentially.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transforms"/> is null.</exception>
    public CompositeTransform(params IPipelineTransformer<T, T>[] transforms) =>
        _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));

    /// <inheritdoc/>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        foreach (var t in _transforms)
            await t.InitializeAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default
    )
    {
        var current = envelope;
        foreach (var t in _transforms)
        {
            var result = await t.TransformAsync(current, ct);
            if (!result.IsSuccess)
                return result;
            current = current with
            {
                Payload = result.Value!,
            };
        }
        return StageResult<T>.Success(current.Payload);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var t in _transforms)
            await t.DisposeAsync();
    }
}
