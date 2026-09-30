using System.Buffers;
using System.Text;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv.Tests;

public sealed class CsvBoundedRecordTextWriterTests
{
    [Fact]
    public void EncodeAndReset_FallbackAfterGrowth_LeavesCallerOwningTheGrownBuffer()
    {
        var encoding = new UTF8Encoding(false, true);
        var encoder = encoding.GetEncoder();
        using var writer = new CsvBoundedRecordTextWriter(1 << 16, encoding, "\r\n");
        var initial = ArrayPool<byte>.Shared.Rent(16);
        byte[]? buffer = initial;
        try
        {
            // 600 two-byte characters need at least 1,200 bytes, so the 16-byte buffer must grow
            // before the unpaired high surrogate at the very end is reached.
            writer.Write(new string('é', 600) + "\uD800");

            Assert.Throws<EncoderFallbackException>(() => writer.EncodeAndReset(encoder, ref buffer));

            Assert.NotNull(buffer);
            Assert.NotSame(initial, buffer);
            Assert.True(buffer!.Length >= 1_200);

            // The writer and encoder remain usable, and the grown buffer is reused as is.
            const string valid = "Дмитрий,😀,41\r\n";
            var grown = buffer;
            writer.Write(valid);
            var written = writer.EncodeAndReset(encoder, ref buffer);

            Assert.Same(grown, buffer);
            Assert.Equal(encoding.GetBytes(valid), buffer.AsSpan(0, written).ToArray());
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public void EncodeAndReset_ExceededRecord_ThrowsAndKeepsBufferUntouched()
    {
        var encoding = new UTF8Encoding(false, true);
        using var writer = new CsvBoundedRecordTextWriter(8, encoding, "\r\n");
        byte[]? buffer = null;

        writer.Write("more than eight characters");

        Assert.Throws<InvalidDataException>(() => writer.EncodeAndReset(encoding.GetEncoder(), ref buffer));
        Assert.Null(buffer);
    }
}
