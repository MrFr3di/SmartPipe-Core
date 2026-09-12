namespace SmartPipe.Extensions.Csv.Internal;

/// <summary>
/// Adapts one logical-record framer to the asynchronous TextReader API used by CsvHelper.
/// </summary>
/// <remarks>
/// A single reader instance is intended to live for one activated run. It never starts a background
/// pump and keeps cancellation in the framer so CsvHelper's token-less parser read cannot lose it.
/// </remarks>
internal sealed class CsvBoundedRecordTextReader : TextReader
{
    private readonly CsvLogicalRecordFramer _framer;
    private CancellationToken _cancellationToken;
    private string? _currentRecord;
    private int _currentOffset;
    private bool _endOfInput;
    private bool _disposed;

    public CsvBoundedRecordTextReader(CsvLogicalRecordFramer framer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(framer);
        _framer = framer;
        _cancellationToken = cancellationToken;
    }

    internal int AsyncReadCalls { get; private set; }

    internal int SyncReadCalls { get; private set; }

    internal void SetDefaultCancellationToken(CancellationToken cancellationToken) =>
        _cancellationToken = cancellationToken;

    public override int Read(char[] buffer, int index, int count)
    {
        SyncReadCalls++;
        throw new InvalidOperationException("Synchronous CSV bridge reads are not supported.");
    }

    public override int Read(Span<char> buffer)
    {
        SyncReadCalls++;
        throw new InvalidOperationException("Synchronous CSV bridge reads are not supported.");
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(index, count), CancellationToken.None).AsTask();
    }

    public override ValueTask<int> ReadAsync(
        Memory<char> buffer,
        CancellationToken cancellationToken = default)
    {
        AsyncReadCalls++;
        return ReadCoreAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken, out var effectiveCancellationToken);
        effectiveCancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0)
            return 0;

        while (!_endOfInput)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            if (_currentRecord is not null && _currentOffset < _currentRecord.Length)
            {
                var count = Math.Min(buffer.Length, _currentRecord.Length - _currentOffset);
                _currentRecord.AsMemory(_currentOffset, count).CopyTo(buffer);
                _currentOffset += count;
                return count;
            }

            _currentRecord = null;
            _currentOffset = 0;
            var record = await _framer.ReadAsync(effectiveCancellationToken).ConfigureAwait(false);
            if (!record.HasValue)
            {
                _endOfInput = true;
                return 0;
            }

            _currentRecord = record.Value.Text;
        }

        return 0;
    }

    private CancellationTokenSource? CreateLinkedCancellation(
        CancellationToken perCallCancellationToken,
        out CancellationToken effectiveCancellationToken)
    {
        if (!_cancellationToken.CanBeCanceled)
        {
            effectiveCancellationToken = perCallCancellationToken;
            return null;
        }

        if (!perCallCancellationToken.CanBeCanceled || perCallCancellationToken == _cancellationToken)
        {
            effectiveCancellationToken = _cancellationToken;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken,
            perCallCancellationToken);
        effectiveCancellationToken = linked.Token;
        return linked;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CsvBoundedRecordTextReader));
    }
}
