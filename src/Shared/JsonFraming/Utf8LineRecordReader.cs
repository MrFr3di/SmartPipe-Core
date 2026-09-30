namespace SmartPipe.Shared.JsonFraming;

internal readonly record struct Utf8LineRecord(byte[] Bytes, bool TooLarge);

internal static class Utf8LineRecordReader
{
    public static IAsyncEnumerable<Utf8LineRecord> ReadAsync(
        Stream stream,
        int maxRecordSizeBytes,
        CancellationToken ct) => ReadAsync(stream, maxRecordSizeBytes, ct, null);

    // File and dead-letter consumers drain the complete record before applying their policy.
    // HTTP Throw can stop once the byte limit and nonblank content are both certain.
    public static async IAsyncEnumerable<Utf8LineRecord> ReadAsync(
        Stream stream,
        int maxRecordSizeBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        Func<Exception>? earlyOversizeExceptionFactory)
    {
        var readBuffer = new byte[8192];
        using var record = new MemoryStream(Math.Min(maxRecordSizeBytes, 8192));
        var tooLarge = false;
        var firstRecord = true;
        var hasNonWhitespace = false;
        var pendingCarriageReturn = false;
        var firstRecordByteCount = 0;
        var bomPrefixMatches = true;

        while (true)
        {
            var read = await stream.ReadAsync(readBuffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            for (var index = 0; index < read; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    pendingCarriageReturn = false;
                    var semanticHasNonWhitespace = hasNonWhitespace
                        || (firstRecord && bomPrefixMatches && firstRecordByteCount is > 0 and < 3);
                    var completed = CompleteRecord(record, tooLarge, semanticHasNonWhitespace, ref firstRecord);
                    if (completed.HasValue)
                        yield return completed.Value;
                    record.SetLength(0);
                    tooLarge = false;
                    hasNonWhitespace = false;
                    continue;
                }

                if (pendingCarriageReturn)
                    Append((byte)'\r');
                pendingCarriageReturn = value == (byte)'\r';
                if (!pendingCarriageReturn)
                    Append(value);
            }
        }

        if (pendingCarriageReturn)
            Append((byte)'\r');
        var finalHasNonWhitespace = hasNonWhitespace
            || (firstRecord && bomPrefixMatches && firstRecordByteCount is > 0 and < 3);
        var final = CompleteRecord(record, tooLarge, finalHasNonWhitespace, ref firstRecord);
        if (final.HasValue)
            yield return final.Value;

        void Append(byte value)
        {
            var isBomByte = firstRecord
                && bomPrefixMatches
                && firstRecordByteCount < 3
                && value == (firstRecordByteCount switch
                {
                    0 => (byte)0xEF,
                    1 => (byte)0xBB,
                    _ => (byte)0xBF,
                });
            if (firstRecord && firstRecordByteCount < 3 && bomPrefixMatches)
            {
                firstRecordByteCount++;
                if (!isBomByte)
                {
                    if (firstRecordByteCount > 1 || !IsHorizontalWhitespace(value))
                        hasNonWhitespace = true;
                    bomPrefixMatches = false;
                }
            }
            else
            {
                if (firstRecord && firstRecordByteCount < 3)
                    firstRecordByteCount++;
                if (!IsHorizontalWhitespace(value))
                    hasNonWhitespace = true;
            }
            if (record.Length < maxRecordSizeBytes)
                record.WriteByte(value);
            else
                tooLarge = true;
            if (tooLarge && hasNonWhitespace && earlyOversizeExceptionFactory is not null)
                throw earlyOversizeExceptionFactory();
        }
    }

    private static Utf8LineRecord? CompleteRecord(
        MemoryStream record,
        bool tooLarge,
        bool hasNonWhitespace,
        ref bool firstRecord)
    {
        var stripBom = firstRecord;
        firstRecord = false;
        if (!hasNonWhitespace)
            return null;
        if (tooLarge)
            return new Utf8LineRecord(Array.Empty<byte>(), true);

        // Inspect the retained buffer before making the one owned output copy.
        record.TryGetBuffer(out var buffer);
        var bytes = buffer.AsSpan();
        var start = stripBom && bytes.StartsWith("\uFEFF"u8) ? 3 : 0;
        var end = bytes.Length;
        while (start < end && IsHorizontalWhitespace(bytes[start]))
            start++;
        while (end > start && IsHorizontalWhitespace(bytes[end - 1]))
            end--;
        if (start == end)
            return null;
        return new Utf8LineRecord(bytes[start..end].ToArray(), false);
    }

    private static bool IsHorizontalWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r';
}
