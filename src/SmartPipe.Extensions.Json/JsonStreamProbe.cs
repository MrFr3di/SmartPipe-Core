using System.Text;

namespace SmartPipe.Extensions;

internal static class JsonStreamProbe
{
    internal static async ValueTask<JsonStreamProbeResult> ProbeAsync(
        Stream stream,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
            throw new NotSupportedException("JSON probing requires a readable stream.");

        if (!stream.CanSeek)
            throw new NotSupportedException("JSON probing requires a seekable stream.");

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;

            var contentStartOffset = 0L;
            if (stream.Length >= 3)
            {
                var bom = new byte[3];
                await stream.ReadExactlyAsync(bom.AsMemory(), ct).ConfigureAwait(false);
                if (bom.AsSpan().SequenceEqual(Encoding.UTF8.Preamble))
                    contentStartOffset = 3;
            }

            stream.Position = contentStartOffset;

            var buffer = new byte[1];
            while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 1)
            {
                if (buffer[0] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    return new JsonStreamProbeResult(buffer[0], contentStartOffset);
            }

            return new JsonStreamProbeResult(null, contentStartOffset);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}

internal readonly record struct JsonStreamProbeResult(byte? FirstSignificantByte, long ContentStartOffset);
