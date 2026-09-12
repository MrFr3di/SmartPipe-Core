using System.Buffers;

namespace SmartPipe.Extensions.Csv.Internal;

/// <summary>
/// Frames the fixed RFC4180-style CSV profile into bounded logical records.
/// </summary>
/// <remarks>
/// This type only recognizes field boundaries and quoted newlines. CsvHelper remains responsible for
/// headers, conversion, mapping, and business validation.
/// </remarks>
internal sealed class CsvLogicalRecordFramer
{
    internal const string FailureKindDataKey = "SmartPipe.CsvFailureKind";
    internal const string LimitFailureKind = "limit";
    internal const string UnrecoverableFailureKind = "unrecoverable";
    private const int ReadBufferLength = 256;
    private const int RetentionChunkLength = 4 * 1024;

    private readonly TextReader _reader;
    private readonly char _delimiter;
    private readonly char _quote;
    private readonly int _maxRecordSizeCharacters;
    private readonly int _maxFieldSizeCharacters;
    private readonly int _maxColumnCount;
    private readonly CancellationToken _defaultCancellationToken;
    private readonly char[] _readBuffer = new char[ReadBufferLength];

    private int _readBufferOffset;
    private int _readBufferCount;
    private bool _hasLookahead;
    private char _lookahead;
    private bool _reachedEndOfInput;
    private bool _unrecoverable;
    private int _peakRetainedCharacters;

    public CsvLogicalRecordFramer(
        TextReader reader,
        char delimiter,
        char quote,
        int maxRecordSizeCharacters,
        int maxFieldSizeCharacters,
        int maxColumnCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (delimiter == quote)
            throw new ArgumentException("CSV delimiter and quote must be distinct.", nameof(delimiter));
        if (maxRecordSizeCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRecordSizeCharacters));
        if (maxFieldSizeCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFieldSizeCharacters));
        if (maxFieldSizeCharacters > maxRecordSizeCharacters)
            throw new ArgumentException(
                "The field character limit cannot exceed the record character limit.",
                nameof(maxFieldSizeCharacters));
        if (maxColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxColumnCount));

        _reader = reader;
        _delimiter = delimiter;
        _quote = quote;
        _maxRecordSizeCharacters = maxRecordSizeCharacters;
        _maxFieldSizeCharacters = maxFieldSizeCharacters;
        _maxColumnCount = maxColumnCount;
        _defaultCancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the largest number of record characters retained by the framer at one time.
    /// </summary>
    internal int PeakRetainedCharacters => _peakRetainedCharacters;

    /// <summary>Reads one complete logical record, including its line terminator when present.</summary>
    public ValueTask<CsvLogicalRecord?> ReadAsync() => ReadAsync(_defaultCancellationToken);

    /// <summary>Reads one complete logical record using the supplied cancellation token for every read.</summary>
    public async ValueTask<CsvLogicalRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_unrecoverable)
            throw CreateUnrecoverableException();
        if (_reachedEndOfInput && !_hasLookahead)
            return null;

        var record = new PooledRecordBuffer(_maxRecordSizeCharacters, this);
        var atFieldStart = true;
        var inQuotedField = false;
        var pendingQuote = false;
        var fieldLength = 0;
        var columnCount = 1;
        var limitFailure = CsvLimitFailure.None;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await ReadCharacterAsync(cancellationToken).ConfigureAwait(false);
            if (!next.HasValue)
            {
                if (record.Length == 0 && limitFailure == CsvLimitFailure.None)
                {
                    record.Release();
                    return null;
                }

                if (inQuotedField && !pendingQuote)
                {
                    _unrecoverable = true;
                    record.Release();
                    throw CreateUnrecoverableException();
                }

                if (limitFailure != CsvLimitFailure.None)
                {
                    record.Release();
                    throw CreateLimitException(limitFailure);
                }

                var text = record.ToStringAndRelease();
                return new CsvLogicalRecord(text, columnCount);
            }

            var character = next.Value;

            if (pendingQuote)
            {
                if (character == _quote)
                {
                    AppendFieldCharacter(record, character, ref fieldLength, ref limitFailure);
                    pendingQuote = false;
                    continue;
                }

                // A non-doubled quote closes the quoted field. Process this character using
                // ordinary field/record rules without consuming another character.
                pendingQuote = false;
                inQuotedField = false;
            }

            if (inQuotedField)
            {
                AppendFieldCharacter(record, character, ref fieldLength, ref limitFailure);
                if (character == _quote)
                    pendingQuote = true;
                continue;
            }

            if (atFieldStart && character == _quote)
            {
                AppendFieldCharacter(record, character, ref fieldLength, ref limitFailure);
                atFieldStart = false;
                inQuotedField = true;
                continue;
            }

            if (character == _delimiter)
            {
                if (columnCount >= _maxColumnCount)
                    limitFailure = MarkLimitFailure(record, limitFailure, CsvLimitFailure.Columns);
                else
                    columnCount++;

                AppendRecordCharacter(record, character, ref limitFailure);
                fieldLength = 0;
                atFieldStart = true;
                continue;
            }

            if (character == '\r')
            {
                AppendRecordCharacter(record, character, ref limitFailure);
                var following = await ReadCharacterAsync(cancellationToken).ConfigureAwait(false);
                if (following == '\n')
                {
                    AppendRecordCharacter(record, following.Value, ref limitFailure);
                }
                else if (following.HasValue)
                {
                    _hasLookahead = true;
                    _lookahead = following.Value;
                }

                return CompleteRecord(record, columnCount, limitFailure);
            }

            if (character == '\n')
            {
                AppendRecordCharacter(record, character, ref limitFailure);
                return CompleteRecord(record, columnCount, limitFailure);
            }

            AppendFieldCharacter(record, character, ref fieldLength, ref limitFailure);
            atFieldStart = false;
        }
    }

    private CsvLogicalRecord CompleteRecord(
        PooledRecordBuffer record,
        int columnCount,
        CsvLimitFailure limitFailure)
    {
        if (limitFailure != CsvLimitFailure.None)
        {
            record.Release();
            throw CreateLimitException(limitFailure);
        }

        return new CsvLogicalRecord(record.ToStringAndRelease(), columnCount);
    }

    private async ValueTask<char?> ReadCharacterAsync(CancellationToken cancellationToken)
    {
        if (_hasLookahead)
        {
            _hasLookahead = false;
            return _lookahead;
        }

        if (_readBufferOffset >= _readBufferCount)
        {
            _readBufferOffset = 0;
            _readBufferCount = await _reader
                .ReadAsync(_readBuffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (_readBufferCount == 0)
            {
                _reachedEndOfInput = true;
                return null;
            }
        }

        return _readBuffer[_readBufferOffset++];
    }

    private void AppendRecordCharacter(
        PooledRecordBuffer record,
        char character,
        ref CsvLimitFailure limitFailure)
    {
        if (limitFailure == CsvLimitFailure.None && !record.TryAppend(character))
            limitFailure = CsvLimitFailure.Record;
    }

    private void AppendFieldCharacter(
        PooledRecordBuffer record,
        char character,
        ref int fieldLength,
        ref CsvLimitFailure limitFailure)
    {
        if (limitFailure == CsvLimitFailure.None && fieldLength >= _maxFieldSizeCharacters)
            limitFailure = MarkLimitFailure(record, limitFailure, CsvLimitFailure.Field);

        if (limitFailure == CsvLimitFailure.None)
        {
            AppendRecordCharacter(record, character, ref limitFailure);
            if (limitFailure == CsvLimitFailure.None)
                fieldLength++;
        }
    }

    private CsvLimitFailure MarkLimitFailure(
        PooledRecordBuffer record,
        CsvLimitFailure current,
        CsvLimitFailure failure)
    {
        if (current == CsvLimitFailure.None)
            record.Release();
        return current == CsvLimitFailure.None ? failure : current;
    }

    private static InvalidDataException CreateLimitException(CsvLimitFailure failure)
    {
        var exception = new InvalidDataException(
            failure switch
            {
                CsvLimitFailure.Field => "CSV field exceeded the configured character limit.",
                CsvLimitFailure.Columns => "CSV logical record exceeded the configured column limit.",
                _ => "CSV logical record exceeded the configured character limit.",
            });
        exception.Data[FailureKindDataKey] = LimitFailureKind;
        return exception;
    }

    private static InvalidDataException CreateUnrecoverableException()
    {
        var exception = new InvalidDataException("CSV input ended inside an open quoted field.");
        exception.Data[FailureKindDataKey] = UnrecoverableFailureKind;
        return exception;
    }

    private enum CsvLimitFailure
    {
        None,
        Record,
        Field,
        Columns,
    }

    private sealed class PooledRecordBuffer
    {
        private readonly int _maximumLength;
        private readonly CsvLogicalRecordFramer _owner;
        private readonly List<char[]> _chunks = [];
        private char[]? _currentChunk;
        private int _currentChunkLength;
        private bool _released;

        public PooledRecordBuffer(int maximumLength, CsvLogicalRecordFramer owner)
        {
            _maximumLength = maximumLength;
            _owner = owner;
        }

        public int Length { get; private set; }

        public bool TryAppend(char character)
        {
            if (_released || Length >= _maximumLength)
                return false;

            if (_currentChunk is null || _currentChunkLength == _currentChunk.Length)
            {
                _currentChunk = ArrayPool<char>.Shared.Rent(Math.Min(RetentionChunkLength, _maximumLength - Length));
                _chunks.Add(_currentChunk);
                _currentChunkLength = 0;
            }

            _currentChunk[_currentChunkLength++] = character;
            Length++;
            if (Length > _owner._peakRetainedCharacters)
                _owner._peakRetainedCharacters = Length;
            return true;
        }

        public string ToStringAndRelease()
        {
            if (Length == 0)
            {
                Release();
                return string.Empty;
            }

            var result = string.Create(Length, this, static (destination, state) => state.CopyTo(destination));
            Release();
            return result;
        }

        public void Release()
        {
            if (_released)
                return;

            foreach (var chunk in _chunks)
                ArrayPool<char>.Shared.Return(chunk);
            _chunks.Clear();
            _currentChunk = null;
            _currentChunkLength = 0;
            _released = true;
        }

        private void CopyTo(Span<char> destination)
        {
            var copied = 0;
            var remaining = Length;
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
    }
}

internal readonly record struct CsvLogicalRecord(string Text, int ColumnCount);
