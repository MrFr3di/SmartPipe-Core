using System.Text;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonStreamProbeTests
{
    [Fact]
    public async Task Probe_EmptyStream_ReturnsNull()
    {
        await using var stream = new MemoryStream();
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Null(result.FirstSignificantByte);
        Assert.Equal(0, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_BomOnly_ReturnsNull()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetPreamble());
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Null(result.FirstSignificantByte);
        Assert.Equal(3, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_BomAndWhitespace_ReturnsNull()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(" \t\r\n"))
            .ToArray();
        await using var stream = new MemoryStream(bytes);
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Null(result.FirstSignificantByte);
        Assert.Equal(3, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_BomWhitespaceArray_ReturnsOpeningBracket()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(" \n["))
            .ToArray();
        await using var stream = new MemoryStream(bytes);
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal((byte)'[', result.FirstSignificantByte);
        Assert.Equal(3, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_BomWhitespaceJson_ReturnsContentStartAfterBom()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("  {\"x\":1}"))
            .ToArray();
        await using var stream = new MemoryStream(bytes);
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal((byte)'{', result.FirstSignificantByte);
        Assert.Equal(3, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_WhitespaceJsonWithoutBom_ReturnsContentStartZero()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("  {\"x\":1}"));
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal((byte)'{', result.FirstSignificantByte);
        Assert.Equal(0, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_OneByteReads_Works()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(" [{"))
            .ToArray();
        await using var stream = new OneByteReadStream(bytes);
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal((byte)'[', result.FirstSignificantByte);
        Assert.Equal(3, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_RestoresConfiguredPosition()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"x\":1}]"));
        stream.Position = 2;
        _ = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public async Task Probe_CancellationIsPreserved()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("   {"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => JsonStreamProbe.ProbeAsync(stream, cts.Token).AsTask());
    }

    [Fact]
    public async Task Probe_CancellationDuringRead_PropagatesCancellation()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(" "))
            .ToArray();
        await using var stream = new CancellableBlockingStream(bytes);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var probeTask = JsonStreamProbe.ProbeAsync(stream, cts.Token).AsTask();
        await stream.FirstRead.Task.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
    }

    [Fact]
    public async Task Probe_InvalidPartialBom_IsTreatedAsData()
    {
        await using var stream = new MemoryStream([(byte)0xEF, (byte)0xBB, (byte)'x']);
        var result = await JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal((byte)0xEF, result.FirstSignificantByte);
        Assert.Equal(0, result.ContentStartOffset);
    }

    [Fact]
    public async Task Probe_NullStream_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => JsonStreamProbe.ProbeAsync(null!, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Probe_NonReadableStream_ThrowsNotSupportedException()
    {
        await using var stream = new NonReadableStream();
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("readable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_NonSeekableStream_ThrowsNotSupportedException()
    {
        await using var stream = new NonSeekableStream();
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => JsonStreamProbe.ProbeAsync(stream, TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("seekable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class CancellableBlockingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _readCount;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _readCount);
            if (count == 1)
            {
                FirstRead.TrySetResult();
                return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
