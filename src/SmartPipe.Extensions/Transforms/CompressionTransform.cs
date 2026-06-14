using System.IO.Compression;
using System.Text;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Compression transformer using Brotli or GZip algorithms.
/// Compresses <see cref="byte"/> arrays for efficient storage or transmission.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration (T = byte[]).
/// </summary>
public enum CompressionAlgorithm
{
    /// <summary>
    /// Brotli compression algorithm - offers good compression ratio with moderate speed.
    /// </summary>
    Brotli,

    /// <summary>
    /// GZip compression algorithm - widely supported with good compression.
    /// </summary>
    GZip,
}

/// <summary>
/// Transformer that compresses byte array payloads using Brotli or GZip algorithms.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration with <see cref="byte"/>[] input and output.
/// </summary>
public class CompressionTransform : IPipelineTransformer<byte[], byte[]>
{
    private readonly CompressionAlgorithm _algorithm;
    private readonly CompressionLevel _level;

    /// <summary>
    /// Initializes a new instance of <see cref="CompressionTransform"/>.
    /// </summary>
    /// <param name="algorithm">The compression algorithm to use. Defaults to <see cref="CompressionAlgorithm.Brotli"/>.</param>
    /// <param name="level">The compression level. Defaults to <see cref="CompressionLevel.Optimal"/>.</param>
    public CompressionTransform(
        CompressionAlgorithm algorithm = CompressionAlgorithm.Brotli,
        CompressionLevel level = CompressionLevel.Optimal
    )
    {
        _algorithm = algorithm;
        _level = level;
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<StageResult<byte[]>> TransformAsync(
        ProcessingEnvelope<byte[]> envelope,
        CancellationToken ct = default
    )
    {
        try
        {
            using var output = new MemoryStream();
            using (var compressor = CreateCompressor(output))
                compressor.Write(envelope.Payload, 0, envelope.Payload.Length);

            return ValueTask.FromResult(
                StageResult<byte[]>.Success(output.ToArray())
            );
        }
        catch (IOException ex)
        {
            return ValueTask.FromResult(
                StageResult<byte[]>.Failure(
                    new SmartPipeError(
                        $"Compression IO error: {ex.Message}",
                        ErrorType.Transient,
                        "Compression",
                        ex
                    )
                )
            );
        }
        catch (NotSupportedException ex)
        {
            return ValueTask.FromResult(
                StageResult<byte[]>.Failure(
                    new SmartPipeError(
                        $"Compression not supported: {ex.Message}",
                        ErrorType.Permanent,
                        "Compression",
                        ex
                    )
                )
            );
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Creates a compression stream for the specified algorithm.
    /// </summary>
    /// <param name="output">The output stream to write compressed data to.</param>
    /// <returns>A compression stream wrapper.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="CompressionAlgorithm"/> is unknown.</exception>
    private Stream CreateCompressor(Stream output) =>
        _algorithm switch
        {
            CompressionAlgorithm.Brotli => new BrotliStream(output, _level),
            CompressionAlgorithm.GZip => new GZipStream(output, _level),
            _ => throw new ArgumentOutOfRangeException(nameof(_algorithm)),
        };
}
