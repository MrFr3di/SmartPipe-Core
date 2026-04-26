using System.IO.Compression;
using System.Text;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Compression/decompression transformer using Brotli or GZip.
/// Supports both text and binary payloads.
/// </summary>
public enum CompressionAlgorithm { Brotli, GZip }

public class CompressionTransform : ITransformer<byte[], byte[]>
{
    private readonly CompressionAlgorithm _algorithm;
    private readonly CompressionLevel _level;

    public CompressionTransform(
        CompressionAlgorithm algorithm = CompressionAlgorithm.Brotli,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        _algorithm = algorithm;
        _level = level;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<byte[]>> TransformAsync(ProcessingContext<byte[]> ctx, CancellationToken ct = default)
    {
        try
        {
            using var output = new MemoryStream();
            using (var compressor = CreateCompressor(output))
                compressor.Write(ctx.Payload, 0, ctx.Payload.Length);

            return ValueTask.FromResult(
                ProcessingResult<byte[]>.Success(output.ToArray(), ctx.TraceId));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(
                ProcessingResult<byte[]>.Failure(
                    new SmartPipeError($"Compression failed: {ex.Message}", ErrorType.Permanent, "Compression", ex),
                    ctx.TraceId));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Stream CreateCompressor(Stream output) => _algorithm switch
    {
        CompressionAlgorithm.Brotli => new BrotliStream(output, _level),
        CompressionAlgorithm.GZip => new GZipStream(output, _level),
        _ => throw new ArgumentOutOfRangeException(nameof(_algorithm))
    };
}
