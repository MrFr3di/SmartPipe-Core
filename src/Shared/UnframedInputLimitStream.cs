#nullable enable

namespace SmartPipe.Shared;

internal sealed class UnframedInputLimitExceededException : IOException
{
    public UnframedInputLimitExceededException(long maximumBytes)
        : base($"Input exceeds the configured {maximumBytes}-byte limit.")
    {
        MaximumBytes = maximumBytes;
    }

    public long MaximumBytes { get; }
}

internal sealed class UnframedInputLimitStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly long _initialPosition;
    private readonly Func<long, Exception>? _limitExceededFactory;
    private long _bytesRead;
    private bool _limitExceeded;

    public UnframedInputLimitStream(
        Stream inner,
        long maximumBytes,
        Func<long, Exception>? limitExceededFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
        _limitExceededFactory = limitExceededFactory;
        _initialPosition = inner.CanSeek ? inner.Position : 0;
        _bytesRead = _initialPosition;
        _limitExceeded = _bytesRead > _maximumBytes;
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
            ResetIfAtInitialPosition(value);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        var permitted = GetPermittedReadCount(count);
        if (permitted == 0)
            return 0;

        var read = _inner.Read(buffer, offset, permitted);
        Count(read, permitted);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var permitted = GetPermittedReadCount(buffer.Length);
        if (permitted == 0)
            return 0;

        var read = _inner.Read(buffer[..permitted]);
        Count(read, permitted);
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) => ReadMemoryAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadMemoryAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var permitted = GetPermittedReadCount(buffer.Length);
        if (permitted == 0)
            return 0;

        var read = await _inner.ReadAsync(buffer[..permitted], cancellationToken).ConfigureAwait(false);
        Count(read, permitted);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return ReadLegacyAsync(buffer, offset, count, cancellationToken);
    }

    private async Task<int> ReadLegacyAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var permitted = GetPermittedReadCount(count);
        if (permitted == 0)
            return 0;

        var read = await _inner.ReadAsync(buffer, offset, permitted, cancellationToken).ConfigureAwait(false);
        Count(read, permitted);
        return read;
    }

    private int GetPermittedReadCount(int requested)
    {
        if (requested == 0)
            return 0;
        if (_limitExceeded || _bytesRead > _maximumBytes)
            throw CreateLimitException();

        var remaining = _maximumBytes - _bytesRead;
        var permitted = remaining >= int.MaxValue
            ? requested
            : (int)Math.Min(requested, remaining + 1);
        return permitted;
    }

    private void Count(int read, int permitted)
    {
        if (read < 0 || read > permitted)
            throw new IOException("The input stream returned an invalid byte count.");
        if (read == 0)
            return;

        if (_bytesRead > _maximumBytes || read > _maximumBytes - _bytesRead)
        {
            _limitExceeded = true;
            throw CreateLimitException();
        }

        _bytesRead += read;
    }

    private Exception CreateLimitException() =>
        _limitExceededFactory?.Invoke(_maximumBytes) ?? new UnframedInputLimitExceededException(_maximumBytes);

    private void ResetIfAtInitialPosition(long position)
    {
        if (position != _initialPosition)
            return;

        _bytesRead = _initialPosition;
        _limitExceeded = _bytesRead > _maximumBytes;
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("Offset and count exceed the buffer bounds.");
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);
        ResetIfAtInitialPosition(position);
        return position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
