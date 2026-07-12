namespace SmartPipe.Extensions.Sinks;

internal static class AppendFraming
{
    internal static void EnsureReadableAndSeekable(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek || !stream.CanWrite)
            throw new InvalidOperationException("Append destinations must be readable and seekable, and writable so the existing line boundary can be determined and records can be appended safely.");
    }

    internal static async ValueTask<bool> RequiresLineSeparatorAsync(Stream stream, CancellationToken ct)
    {
        EnsureReadableAndSeekable(stream);
        var position = stream.Position;
        try
        {
            if (stream.Length == 0)
                return false;

            if (stream.Length == 3)
            {
                stream.Position = 0;
                var bom = new byte[3];
                var bomRead = await stream.ReadAsync(bom, ct).ConfigureAwait(false);
                if (bomRead == 3 && bom.AsSpan().SequenceEqual("\uFEFF"u8))
                    return false;
            }

            stream.Position = stream.Length - 1;
            var lastByte = new byte[1];
            var read = await stream.ReadAsync(lastByte, ct).ConfigureAwait(false);
            return read != 1 || lastByte[0] != (byte)'\n';
        }
        finally
        {
            stream.Position = position;
        }
    }
}
