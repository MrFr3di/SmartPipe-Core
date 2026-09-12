using System.Text;

namespace SmartPipe.Extensions.Csv.Internal;

internal static class CsvStrictEncoding
{
    private const int MaximumBomLength = 4;

    internal readonly record struct ProbeResult(Encoding Encoding, int ContentOffset);

    public static async ValueTask<StreamReader> OpenReaderAsync(
        string path,
        CsvSourceOptionsSnapshot options,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var probe = await ProbeAsync(
                stream,
                options.Encoding,
                options.DetectEncodingFromByteOrderMarks,
                cancellationToken).ConfigureAwait(false);
            stream.Position = probe.ContentOffset;
            return new StreamReader(
                stream,
                probe.Encoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: options.BufferSize,
                leaveOpen: false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async ValueTask<ProbeResult> ProbeAsync(
        Stream stream,
        Encoding configuredEncoding,
        bool detectEncodingFromByteOrderMarks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(configuredEncoding);

        var prefix = new byte[MaximumBomLength];
        var count = 0;
        while (count < prefix.Length)
        {
            var read = await stream.ReadAsync(
                prefix.AsMemory(count, prefix.Length - count),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            count += read;
        }

        var options = new CsvSourceOptionsSnapshot(
            ',',
            '"',
            System.Globalization.CultureInfo.InvariantCulture,
            configuredEncoding,
            detectEncodingFromByteOrderMarks,
            true,
            CsvInvalidRecordBehavior.Throw,
            CsvMissingFieldBehavior.Throw,
            CsvHeaderValidationBehavior.Throw,
            true,
            true,
            1,
            1,
            1,
            1);
        var (encoding, contentOffset) = SelectEncoding(prefix, count, options);
        return new ProbeResult(encoding, contentOffset);
    }

    internal static byte[] GetPreamble(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var preamble = encoding.GetPreamble();
        if (preamble.Length != 0)
            return preamble;

        return encoding.CodePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true).GetPreamble(),
            1200 => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true).GetPreamble(),
            1201 => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true).GetPreamble(),
            12000 => new UTF32Encoding(false, true, true).GetPreamble(),
            12001 => new UTF32Encoding(true, true, true).GetPreamble(),
            _ => [],
        };
    }

    private static (Encoding Encoding, int ContentOffset) SelectEncoding(
        byte[] prefix,
        int count,
        CsvSourceOptionsSnapshot options)
    {
        if (!options.DetectEncodingFromByteOrderMarks)
            return (options.Encoding, 0);

        if (count >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
            return (new UTF32Encoding(false, false, true), 4);
        if (count >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)
            return (new UTF32Encoding(true, false, true), 4);
        if (count >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 3);
        if (count >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), 2);
        if (count >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), 2);

        return (options.Encoding, 0);
    }
}
