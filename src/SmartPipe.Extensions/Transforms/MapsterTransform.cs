using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Object-to-object mapping transformer using Mapster.
/// Maps input type to output type with high performance code generation.
/// </summary>
/// <typeparam name="TInput">Source type.</typeparam>
/// <typeparam name="TOutput">Destination type.</typeparam>
public class MapsterTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    private readonly TypeAdapterConfig? _config;

    public MapsterTransform(TypeAdapterConfig? config = null)
    {
        _config = config;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<TOutput>> TransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default)
    {
        try
        {
            var result = _config != null
                ? ctx.Payload.Adapt<TOutput>(_config)
                : ctx.Payload.Adapt<TOutput>();

            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Success(result, ctx.TraceId));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError($"Mapster mapping failed: {ex.Message}", ErrorType.Permanent, "Mapping", ex),
                    ctx.TraceId));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
