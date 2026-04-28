using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Combines multiple transforms into one. Applies them sequentially.</summary>
public class CompositeTransform<T> : ITransformer<T, T>
{
    private readonly ITransformer<T, T>[] _transforms;

    public CompositeTransform(params ITransformer<T, T>[] transforms) => _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        foreach (var t in _transforms) await t.InitializeAsync(ct);
    }

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

    public async Task DisposeAsync()
    {
        foreach (var t in _transforms) await t.DisposeAsync();
    }
}
