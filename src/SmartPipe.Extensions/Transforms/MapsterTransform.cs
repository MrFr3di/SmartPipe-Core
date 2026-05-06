using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Object-to-object mapping transformer using Mapster.
/// Maps <typeparamref name="TInput"/> to <typeparamref name="TOutput"/> with high-performance code generation.
/// Implements <see cref="ITransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="TInput">The source type to map from.</typeparam>
/// <typeparam name="TOutput">The destination type to map to.</typeparam>
public class MapsterTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    private readonly TypeAdapterConfig? _config;

    /// <summary>
    /// Initializes a new instance of <see cref="MapsterTransform{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="config">Optional Mapster configuration for custom mapping rules.</param>
    public MapsterTransform(TypeAdapterConfig? config = null)
    {
        _config = config;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
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
        catch (InvalidOperationException ex)
        {
            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError($"Mapster mapping error: {ex.Message}", ErrorType.Permanent, "Mapping", ex),
                    ctx.TraceId));
        }
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}
