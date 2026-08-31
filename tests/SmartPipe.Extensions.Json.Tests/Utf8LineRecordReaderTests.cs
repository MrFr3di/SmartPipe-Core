using System.Text;
using SmartPipe.Shared.JsonFraming;

namespace SmartPipe.Extensions.Tests;

public sealed class Utf8LineRecordReaderTests
{
    [Fact]
    public async Task TruncatedBomPrefixBeforeLf_IsReturnedForJsonValidation()
    {
        await using var stream = new MemoryStream([0xEF, (byte)'\n']);

        var record = Assert.Single(await ReadAllAsync(stream, 4));

        Assert.False(record.TooLarge);
        Assert.Equal([0xEF], record.Bytes);
    }

    [Fact]
    public async Task TruncatedBomPrefixAtEof_IsReturnedForJsonValidation()
    {
        await using var stream = new MemoryStream([0xEF, 0xBB]);

        var record = Assert.Single(await ReadAllAsync(stream, 4));

        Assert.False(record.TooLarge);
        Assert.Equal([0xEF, 0xBB], record.Bytes);
    }

    [Theory]
    [InlineData("12345\n", 5, false)]
    [InlineData("123456\n", 5, true)]
    public async Task RecordSizeBoundary_IsEnforced(string content, int limit, bool tooLarge)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var record = Assert.Single(await ReadAllAsync(stream, limit));
        Assert.Equal(tooLarge, record.TooLarge);
    }

    [Fact]
    public async Task ExactLimitRecord_WithCrLf_IsNotTooLarge()
    {
        var bytes = "\"abc\"\r\n"u8.ToArray();
        await using var stream = new MemoryStream(bytes);

        var records = await ReadAllAsync(stream, maxRecordSizeBytes: 5);

        var record = Assert.Single(records);
        Assert.False(record.TooLarge);
        Assert.Equal("\"abc\""u8.ToArray(), record.Bytes);
    }

    [Fact]
    public async Task OversizedWhitespaceOnlyLine_IsNotARecord()
    {
        await using var stream = new MemoryStream("          \r\n\"ok\"\n"u8.ToArray());

        var records = await ReadAllAsync(stream, maxRecordSizeBytes: 4);

        var record = Assert.Single(records);
        Assert.False(record.TooLarge);
        Assert.Equal("\"ok\""u8.ToArray(), record.Bytes);
    }

    [Fact]
    public async Task BomPlusOversizedWhitespaceFirstLine_IsNotARecord()
    {
        await using var stream = new MemoryStream("\uFEFF          \r\n\"ok\"\n"u8.ToArray());

        var records = await ReadAllAsync(stream, maxRecordSizeBytes: 4);

        var record = Assert.Single(records);
        Assert.False(record.TooLarge);
        Assert.Equal("\"ok\""u8.ToArray(), record.Bytes);
    }

    [Fact]
    public async Task Cancellation_InterruptsPendingRead()
    {
        await using var stream = new BlockingReadStream();
        using var cts = new CancellationTokenSource();
        await using var enumerator = Utf8LineRecordReader.ReadAsync(stream, 16, cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
    }

    [Fact]
    public async Task OversizedRecord_IsDiscardedThroughLf_AndNextRecordIsReturned()
    {
        await using var stream = new MemoryStream("123456789\nnext\n"u8.ToArray());
        var records = await ReadAllAsync(stream, 4);
        Assert.True(records[0].TooLarge);
        Assert.Equal("1234"u8.ToArray(), records[0].Bytes);
        Assert.False(records[1].TooLarge);
        Assert.Equal("next"u8.ToArray(), records[1].Bytes);
    }

    [Fact]
    public async Task FinalRecordWithoutLf_IsReturned()
    {
        await using var stream = new MemoryStream("final"u8.ToArray());
        var record = Assert.Single(await ReadAllAsync(stream, 5));
        Assert.False(record.TooLarge);
        Assert.Equal("final"u8.ToArray(), record.Bytes);
    }

    [Fact]
    public async Task PartialMultibyteUtf8_IsPreservedWithoutDecoding()
    {
        var expected = Encoding.UTF8.GetBytes("\"ёж\"");
        await using var stream = new OneByteReadStream([.. expected, (byte)'\n']);
        var record = Assert.Single(await ReadAllAsync(stream, expected.Length));
        Assert.False(record.TooLarge);
        Assert.Equal(expected, record.Bytes);
    }

    [Fact]
    public async Task BomBlankLineAndCrLfSplitAcrossReads_AreFramedCorrectly()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat("  \r\n\"first\"\r\n\"second\"\n"u8.ToArray())
            .ToArray();
        await using var stream = new OneByteReadStream(bytes);

        var records = await ReadAllAsync(stream, maxRecordSizeBytes: 16);

        Assert.Equal(2, records.Count);
        Assert.Equal("\"first\""u8.ToArray(), records[0].Bytes);
        Assert.Equal("\"second\""u8.ToArray(), records[1].Bytes);
        Assert.All(records, static record => Assert.False(record.TooLarge));
    }

    private static async Task<List<Utf8LineRecord>> ReadAllAsync(Stream stream, int maxRecordSizeBytes)
    {
        var records = new List<Utf8LineRecord>();
        await foreach (var record in Utf8LineRecordReader.ReadAsync(
            stream,
            maxRecordSizeBytes,
            TestContext.Current.CancellationToken))
        {
            records.Add(record);
        }
        return records;
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(
                static _ => 0,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default));
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
