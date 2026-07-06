using Mapster;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Object-to-object mapping transformer using Mapster.
/// Maps <typeparamref name="TInput"/> to <typeparamref name="TOutput"/> with high-performance code generation.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="TInput">The source type to map from.</typeparam>
/// <typeparam name="TOutput">The destination type to map to.</typeparam>
[RequiresUnreferencedCode(
    "MapsterTransform uses Mapster runtime mapping metadata, which is not trimming-safe. Use a hand-written mapper, a source-generated mapper, or PipelineTransformer.FromFunc for trimming."
)]
[RequiresDynamicCode(
    "MapsterTransform uses Mapster runtime expression compilation, which is not NativeAOT-safe. Use a hand-written mapper, a source-generated mapper, or PipelineTransformer.FromFunc for NativeAOT."
)]
public class MapsterTransform<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
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
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default
    )
    {
        try
        {
            var result =
                _config != null
                    ? envelope.Payload.Adapt<TOutput>(_config)
                    : envelope.Payload.Adapt<TOutput>();

            return ValueTask.FromResult(StageResult<TOutput>.Success(result));
        }
        catch (CompileException ex)
        {
            return CreateMappingFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            return CreateMappingFailure(ex);
        }
    }

    private static ValueTask<StageResult<TOutput>> CreateMappingFailure(Exception ex)
    {
        return ValueTask.FromResult(
            StageResult<TOutput>.Failure(
                new SmartPipeError(
                    $"Mapster mapping error: {ex.Message}",
                    ErrorType.Permanent,
                    "Mapping",
                    ex
                )
            )
        );
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
