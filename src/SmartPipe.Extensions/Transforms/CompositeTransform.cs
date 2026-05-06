using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Combines multiple <see cref="ITransformer{T, T}"/> instances into a single transform.
/// Transforms are applied sequentially; if any transform fails, the failure is returned immediately.
/// Implements <see cref="ITransformer{T, T}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class CompositeTransform<T> : ITransformer<T, T>
{
    private readonly ITransformer<T, T>[] _transforms;

    /// <summary>
    /// Initializes a new instance of <see cref="CompositeTransform{T}"/> with the specified transforms.
    /// </summary>
    /// <param name="transforms">The transforms to apply sequentially.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transforms"/> is null.</exception>
    public CompositeTransform(params ITransformer<T, T>[] transforms) => _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        foreach (var t in _transforms) await t.InitializeAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        var current = ctx;
        foreach (var t in _transforms)
        {
            var result = await t.TransformAsync(current, ct);
            if (!result.IsSuccess) return result;
            current = new ProcessingContext<T>(result.Value!) { Metadata = current.Metadata };
        }
        return ProcessingResult<T>.Success(current.Payload, ctx.TraceId);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        foreach (var t in _transforms) await t.DisposeAsync();
    }
}
