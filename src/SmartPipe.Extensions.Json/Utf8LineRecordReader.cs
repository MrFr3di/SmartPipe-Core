namespace SmartPipe.Extensions;

internal readonly record struct Utf8LineRecord(byte[] Bytes, bool TooLarge);

internal static class Utf8LineRecordReader
{
    public static async IAsyncEnumerable<Utf8LineRecord> ReadAsync(
        Stream stream,
        int maxRecordSizeBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var readBuffer = new byte[8192];
        using var record = new MemoryStream(Math.Min(maxRecordSizeBytes, 8192));
        var tooLarge = false;
        var firstRecord = true;
        var hasNonWhitespace = false;
        var pendingCarriageReturn = false;

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
                    var completed = CompleteRecord(record, tooLarge, hasNonWhitespace, ref firstRecord);
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
        var final = CompleteRecord(record, tooLarge, hasNonWhitespace, ref firstRecord);
        if (final.HasValue)
            yield return final.Value;

        void Append(byte value)
        {
            if (!IsHorizontalWhitespace(value))
                hasNonWhitespace = true;
            if (record.Length < maxRecordSizeBytes)
                record.WriteByte(value);
            else
                tooLarge = true;
        }
    }

    private static Utf8LineRecord? CompleteRecord(
        MemoryStream record,
        bool tooLarge,
        bool hasNonWhitespace,
        ref bool firstRecord)
    {
        var bytes = record.ToArray();
        if (firstRecord)
        {
            firstRecord = false;
            if (bytes.AsSpan().StartsWith("\uFEFF"u8))
                bytes = bytes[3..];
        }

        var start = 0;
        var end = bytes.Length;
        while (start < end && IsHorizontalWhitespace(bytes[start]))
            start++;
        while (end > start && IsHorizontalWhitespace(bytes[end - 1]))
            end--;
        var content = bytes.AsSpan(start, end - start);
        if (!hasNonWhitespace || (content.IsEmpty && !tooLarge))
            return null;
        return new Utf8LineRecord(content.ToArray(), tooLarge);
    }

    private static bool IsHorizontalWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r';
}
