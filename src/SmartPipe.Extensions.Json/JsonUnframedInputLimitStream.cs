using System.Text.Json;

namespace SmartPipe.Extensions;

internal sealed class JsonUnframedInputLimitStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxUnframedInputSizeBytes;
    private readonly string _path;
    private readonly long _initialPosition;
    private long _bytesRead;

    public JsonUnframedInputLimitStream(Stream inner, long maxUnframedInputSizeBytes, string path)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxUnframedInputSizeBytes = maxUnframedInputSizeBytes;
        _path = path;
        _initialPosition = inner.CanSeek ? inner.Position : 0;
        _bytesRead = _initialPosition;
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
            if (value == _initialPosition)
                _bytesRead = _initialPosition;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Count(read);
        return read;
    }

    private void Count(int read)
    {
        _bytesRead += read;
        if (_bytesRead > _maxUnframedInputSizeBytes)
            throw new JsonException($"Unframed JSON input '{_path}' exceeds the configured {_maxUnframedInputSizeBytes}-byte limit.");
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);
        if (position == _initialPosition)
            _bytesRead = _initialPosition;
        return position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
