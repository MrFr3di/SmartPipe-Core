using System.Text.Json;

namespace SmartPipe.Extensions.Http.Json;

/// <summary>Requires an array root, consumes an optional UTF-8 BOM and preserves bounded read sizes.</summary>
/// <remarks>The response owns the inner stream; disposing this guard does not dispose it.</remarks>
internal sealed class HttpJsonArrayReadStream(Stream inner) : Stream
{
    private bool _atStart = true;
    private bool _arrayStarted;
    private int _bomBytes;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            var read = inner.Read(buffer);
            var skipped = ValidatePrefix(buffer[..read], buffer.Length != 0);
            if (read == 0 || read > skipped)
            {
                buffer.Slice(skipped, read - skipped).CopyTo(buffer);
                return read - skipped;
            }
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var skipped = ValidatePrefix(buffer.Span[..read], buffer.Length != 0);
            if (read == 0 || read > skipped)
            {
                buffer.Span.Slice(skipped, read - skipped).CopyTo(buffer.Span);
                return read - skipped;
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int ValidatePrefix(ReadOnlySpan<byte> bytes, bool detectEnd)
    {
        if (_arrayStarted)
            return 0;
        if (bytes.IsEmpty && detectEnd && _bomBytes is 1 or 2)
            throw InvalidRoot();

        var skipped = 0;
        foreach (var value in bytes)
        {
            if (_bomBytes is 1 or 2)
            {
                if (value != (_bomBytes == 1 ? (byte)0xBB : (byte)0xBF))
                    throw InvalidRoot();
                _bomBytes++;
                skipped++;
                continue;
            }

            if (_atStart && value == 0xEF)
            {
                _atStart = false;
                _bomBytes = 1;
                skipped++;
                continue;
            }

            _atStart = false;
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            if (value != (byte)'[')
                throw InvalidRoot();

            _arrayStarted = true;
            return skipped;
        }
        return skipped;
    }

    private static JsonException InvalidRoot() => new("The HTTP JSON response must contain a root array.");

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
