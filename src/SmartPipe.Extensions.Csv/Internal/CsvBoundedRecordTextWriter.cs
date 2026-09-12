using System.Buffers;
using System.Text;

namespace SmartPipe.Extensions.Csv.Internal;

/// <summary>Retains one CsvHelper output record in pooled, character-bounded chunks.</summary>
internal sealed class CsvBoundedRecordTextWriter : TextWriter
{
    private const int ChunkSize = 4 * 1024;
    private readonly int _maximumLength;
    private readonly Encoding _encoding;
    private readonly List<char[]> _chunks = [];
    private char[]? _currentChunk;
    private int _currentChunkLength;
    private int _length;
    private bool _exceeded;
    private bool _disposed;

    public CsvBoundedRecordTextWriter(int maximumLength, Encoding encoding, string newLine)
    {
        if (maximumLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(newLine);

        _maximumLength = maximumLength;
        _encoding = encoding;
        NewLine = newLine;
    }

    public override Encoding Encoding => _encoding;

    internal int Length => _length;

    internal bool Exceeded => _exceeded;

    public override void Write(char value)
    {
        ThrowIfDisposed();
        if (!EnsureCapacity(1))
            return;
        Append(value);
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        ThrowIfDisposed();
        if (!EnsureCapacity(buffer.Length))
            return;
        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            if (_currentChunk is null || _currentChunkLength == _currentChunk.Length)
                RentChunk();

            var copyLength = Math.Min(remaining.Length, _currentChunk!.Length - _currentChunkLength);
            remaining[..copyLength].CopyTo(_currentChunk.AsSpan(_currentChunkLength));
            _currentChunkLength += copyLength;
            _length += copyLength;
            remaining = remaining[copyLength..];
        }
    }

    public override Task WriteAsync(char[] buffer, int index, int count)
    {
        Write(buffer, index, count);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return Task.CompletedTask;
    }

    internal void Reset()
    {
        ThrowIfDisposed();
        ReturnChunks();
    }

    internal string ToStringAndReset()
    {
        ThrowIfDisposed();
        if (_exceeded)
        {
            ReturnChunks();
            throw new InvalidDataException("CSV output record exceeded the configured character limit.");
        }

        if (_length == 0)
        {
            ReturnChunks();
            return string.Empty;
        }

        var result = string.Create(_length, this, static (destination, writer) => writer.CopyTo(destination));
        ReturnChunks();
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            ReturnChunks();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private bool EnsureCapacity(int additionalLength)
    {
        if (additionalLength < 0 || additionalLength > _maximumLength - _length)
        {
            _exceeded = true;
            return false;
        }

        return true;
    }

    private void Append(char value)
    {
        if (_currentChunk is null || _currentChunkLength == _currentChunk.Length)
            RentChunk();

        _currentChunk![_currentChunkLength++] = value;
        _length++;
    }

    private void RentChunk()
    {
        _currentChunk = ArrayPool<char>.Shared.Rent(Math.Min(ChunkSize, _maximumLength - _length));
        _chunks.Add(_currentChunk);
        _currentChunkLength = 0;
    }

    private void ReturnChunks()
    {
        foreach (var chunk in _chunks)
            ArrayPool<char>.Shared.Return(chunk);
        _chunks.Clear();
        _currentChunk = null;
        _currentChunkLength = 0;
        _length = 0;
        _exceeded = false;
    }

    private void CopyTo(Span<char> destination)
    {
        var copied = 0;
        var remaining = _length;
        foreach (var chunk in _chunks)
        {
            var count = Math.Min(remaining, chunk.Length);
            chunk.AsSpan(0, count).CopyTo(destination[copied..]);
            copied += count;
            remaining -= count;
            if (remaining == 0)
                break;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
