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
                    var completed = CompleteRecord(record, tooLarge, ref firstRecord);
                    if (completed.HasValue)
                        yield return completed.Value;
                    record.SetLength(0);
                    tooLarge = false;
                    continue;
                }

                if (record.Length < maxRecordSizeBytes)
                    record.WriteByte(value);
                else
                    tooLarge = true;
            }
        }

        var final = CompleteRecord(record, tooLarge, ref firstRecord);
        if (final.HasValue)
            yield return final.Value;
    }

    private static Utf8LineRecord? CompleteRecord(
        MemoryStream record,
        bool tooLarge,
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
        if (content.IsEmpty && !tooLarge)
            return null;
        return new Utf8LineRecord(content.ToArray(), tooLarge);
    }

    private static bool IsHorizontalWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r';
}
