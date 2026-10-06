using System.IO.Compression;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Supported byte-array compression algorithms.</summary>
public enum CompressionAlgorithm
{
    /// <summary>Brotli compression.</summary>
    Brotli,

    /// <summary>GZip compression.</summary>
    GZip,
}

/// <summary>Compresses byte arrays using Brotli or GZip.</summary>
public class CompressionTransform : IPipelineTransformer<byte[], byte[]>
{
    private readonly CompressionAlgorithm _algorithm;
    private readonly CompressionLevel _level;

    /// <summary>Initializes a byte-array compression transform.</summary>
    public CompressionTransform(
        CompressionAlgorithm algorithm = CompressionAlgorithm.Brotli,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        if (!Enum.IsDefined(algorithm))
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level));

        _algorithm = algorithm;
        _level = level;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<StageResult<byte[]>> TransformAsync(
        ProcessingEnvelope<byte[]> envelope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var output = new MemoryStream();
            using (Stream compressor = _algorithm switch
            {
                CompressionAlgorithm.Brotli => new BrotliStream(output, _level),
                CompressionAlgorithm.GZip => new GZipStream(output, _level),
                _ => throw new ArgumentOutOfRangeException(nameof(_algorithm)),
            })
            {
                compressor.Write(envelope.Payload);
            }

            return ValueTask.FromResult(StageResult<byte[]>.Success(output.ToArray()));
        }
        catch (IOException error)
        {
            return ValueTask.FromResult(StageResult<byte[]>.Failure(
                new SmartPipeError($"Compression IO error: {error.Message}", ErrorType.Transient, "Compression", error)));
        }
        catch (NotSupportedException error)
        {
            return ValueTask.FromResult(StageResult<byte[]>.Failure(
                new SmartPipeError($"Compression not supported: {error.Message}", ErrorType.Permanent, "Compression", error)));
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
