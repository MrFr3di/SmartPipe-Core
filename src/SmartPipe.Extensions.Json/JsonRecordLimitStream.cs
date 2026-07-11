using System.Text.Json;

namespace SmartPipe.Extensions;

internal sealed class JsonRecordLimitStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxRecordSizeBytes;
    private readonly string _path;
    private long _recordIndex = 1;
    private int _recordBytes;

    public JsonRecordLimitStream(Stream inner, int maxRecordSizeBytes, string path)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxRecordSizeBytes = maxRecordSizeBytes;
        _path = path;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set
        {
            _inner.Position = value;
            _recordIndex = 1;
            _recordBytes = 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inspect(buffer.Span[..read]);
        return read;
    }

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                _recordIndex++;
                _recordBytes = 0;
                continue;
            }

            _recordBytes++;
            if (_recordBytes > _maxRecordSizeBytes)
                throw new JsonException(
                    $"JSON record {_recordIndex} in '{_path}' exceeds the {_maxRecordSizeBytes}-byte limit.");
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);
        _recordIndex = 1;
        _recordBytes = 0;
        return position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Ownership remains with the caller.
        base.Dispose(disposing);
    }
}
