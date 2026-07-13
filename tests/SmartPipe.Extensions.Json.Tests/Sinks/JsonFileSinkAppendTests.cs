using System.Text;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sinks;

public sealed class JsonFileSinkAppendTests
{
    [Theory]
    [InlineData("", "[{\"Value\":\"new\"}]\n")]
    [InlineData("old\n", "old\n[{\"Value\":\"new\"}]\n")]
    [InlineData("old", "old\n[{\"Value\":\"new\"}]\n")]
    [InlineData("old\r", "old\r\n[{\"Value\":\"new\"}]\n")]
    public async Task BatchJsonLines_Append_EnsuresExactlyOneBoundary(string existing, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, existing, TestContext.Current.CancellationToken);
            await WritePathAsync(path, JsonFileFormat.BatchJsonLines, "new");
            Assert.Equal(expected, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Ndjson_Append_FileWithoutLfAddsSingleSeparator()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "partial", TestContext.Current.CancellationToken);
            await WritePathAsync(path, JsonFileFormat.Ndjson, "new");
            Assert.Equal("partial\n{\"Value\":\"new\"}\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Append_SeparatorAndRecordRollbackTogether_ThenRetryDoesNotDuplicateSeparator()
    {
        await using var stream = new FailFirstAppendWriteStream("partial");
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);

        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(Envelope("new")).AsTask());
        Assert.Equal("partial", stream.Text);
        await sink.DisposeAsync();

        Assert.Equal("partial\n[{\"Value\":\"new\"}]\n", stream.Text);
    }

    [Fact]
    public async Task Append_ReopenDoesNotDuplicateSeparator()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "partial", TestContext.Current.CancellationToken);
            await WritePathAsync(path, JsonFileFormat.BatchJsonLines, "one");
            await WritePathAsync(path, JsonFileFormat.BatchJsonLines, "two");
            Assert.Equal("partial\n[{\"Value\":\"one\"}]\n[{\"Value\":\"two\"}]\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Append_InjectedStreamWithoutReadAndSeek_FailsFast()
    {
        await using var stream = new WriteOnlyStream();
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.InitializeAsync().AsTask());
        Assert.Contains("readable and seekable", exception.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.WriteAsync(Envelope("new")).AsTask());
    }

    [Fact]
    public async Task Append_InjectedReadOnlySeekableStream_FailsDuringInitializationBeforeWrite()
    {
        var existing = Encoding.UTF8.GetBytes("existing");
        await using var stream = new MemoryStream(existing, writable: false);
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("writable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, stream.ToArray());
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task AppendFraming_PreservesOriginalPosition()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("existing"));
        stream.Position = 3;
        Assert.True(await AppendFraming.RequiresLineSeparatorAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public async Task AppendFraming_BomOnly_IsSemanticallyEmpty()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetPreamble());
        Assert.False(await AppendFraming.RequiresLineSeparatorAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendFraming_BomOnly_WithPartialReads_DoesNotRequireSeparator()
    {
        await using var stream = new OneByteReadStream(Encoding.UTF8.GetPreamble());
        var originalPosition = stream.Position;
        var result = await AppendFraming.RequiresLineSeparatorAsync(stream, TestContext.Current.CancellationToken);
        Assert.False(result);
        Assert.Equal(originalPosition, stream.Position);
    }

    [Fact]
    public async Task AppendFraming_ReadFailure_RestoresOriginalPosition()
    {
        await using var stream = new ReadFailingStream(Encoding.UTF8.GetBytes("existing")) { Position = 2 };
        await Assert.ThrowsAsync<IOException>(() => AppendFraming.RequiresLineSeparatorAsync(stream, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public async Task Append_BomOnly_DoesNotPrefixLineFeed()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble(), TestContext.Current.CancellationToken);
            await WritePathAsync(path, JsonFileFormat.BatchJsonLines, "new");
            Assert.Equal(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[{\"Value\":\"new\"}]\n")), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Append_CancellationAfterPartialPayload_RollsBackAndPreservesCancellation()
    {
        await using var stream = new TransactionFailureStream("partial", failWriteOrdinal: 2, new OperationCanceledException("cancelled"));
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);
        await Assert.ThrowsAsync<OperationCanceledException>(() => sink.WriteAsync(Envelope("new")).AsTask());
        Assert.Equal("partial", stream.Text);
        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public async Task Append_FlushFailure_RollsBackPayload()
    {
        await using var stream = new TransactionFailureStream("partial", failFlush: true);
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);
        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(Envelope("new")).AsTask());
        Assert.Equal("partial", stream.Text);
    }

    [Fact]
    public async Task Append_RollbackFailure_RetainsOriginalAndRollbackErrors()
    {
        await using var stream = new TransactionFailureStream("partial", failWriteOrdinal: 2, new IOException("payload failed"), failRollback: true);
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);
        var exception = await Assert.ThrowsAsync<AggregateException>(() => sink.WriteAsync(Envelope("new")).AsTask());
        Assert.Contains(exception.InnerExceptions, e => e.Message.Contains("payload failed", StringComparison.Ordinal));
        Assert.Contains(exception.InnerExceptions, e => e.Message.Contains("rollback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Append_ExternalStream_RemainsOpen_AndBoundaryIsCheckedOnce()
    {
        await using var stream = new CountingReadStream("partial") { Position = 2 };
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);
        await sink.WriteAsync(Envelope("one"));
        await sink.WriteAsync(Envelope("two"));
        await sink.DisposeAsync();
        Assert.True(stream.CanRead);
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(1, stream.AsyncReadCount);
    }

    [Fact]
    public async Task Append_DisposeWaitsForInFlightWrite_ThenCommitsExactlyOneBoundary()
    {
        await using var stream = new GatedWriteStream("partial");
        var sink = new JsonFileSink<Item>("dummy.json", stream, flushInterval: 1);
        var write = sink.WriteAsync(Envelope("new"), TestContext.Current.CancellationToken).AsTask();
        await stream.WriteEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dispose = sink.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        stream.ReleaseWrite.TrySetResult();

        await write;
        await dispose;
        Assert.Equal("partial\n[{\"Value\":\"new\"}]\n", stream.Text);
        Assert.True(stream.CanRead);
    }

    private static async Task WritePathAsync(string path, JsonFileFormat format, string value)
    {
        var sink = new JsonFileSink<Item>(path, new JsonFileSinkOptions { Format = format, OpenMode = JsonFileOpenMode.Append, FlushInterval = 1 });
        await sink.WriteAsync(Envelope(value));
        await sink.DisposeAsync();
    }

    private static ProcessingEnvelope<Item> Envelope(string value) => ProcessingEnvelope<Item>.Create(new Item { Value = value });
    private sealed class Item { public string? Value { get; set; } }

    private sealed class FailFirstAppendWriteStream : MemoryStream
    {
        private bool _failed;
        public FailFirstAppendWriteStream(string text) { var bytes = Encoding.UTF8.GetBytes(text); Write(bytes); Position = 0; }
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                base.Write(buffer.Span[..Math.Min(2, buffer.Length)]);
                throw new IOException("deterministic partial write");
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ReadFailingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("read failed");
    }

    private sealed class TransactionFailureStream : MemoryStream
    {
        private readonly int _failWriteOrdinal;
        private readonly Exception? _writeFailure;
        private readonly bool _failFlush;
        private readonly bool _failRollback;
        private int _writes;
        public TransactionFailureStream(string existing, int failWriteOrdinal = 0, Exception? writeFailure = null, bool failFlush = false, bool failRollback = false)
        {
            _failWriteOrdinal = failWriteOrdinal; _writeFailure = writeFailure; _failFlush = failFlush; _failRollback = failRollback;
            Write(Encoding.UTF8.GetBytes(existing)); Position = 0;
        }
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes == _failWriteOrdinal)
            {
                base.Write(buffer.Span[..Math.Min(2, buffer.Length)]);
                return ValueTask.FromException(_writeFailure!);
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => _failFlush ? Task.FromException(new IOException("flush failed")) : base.FlushAsync(cancellationToken);
        public override void SetLength(long value) { if (_failRollback) throw new IOException("rollback failed"); base.SetLength(value); }
    }

    private sealed class CountingReadStream : MemoryStream
    {
        public CountingReadStream(string existing) { Write(Encoding.UTF8.GetBytes(existing)); Position = 0; }
        public int AsyncReadCount { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { AsyncReadCount++; return base.ReadAsync(buffer, cancellationToken); }
    }

    private sealed class GatedWriteStream : MemoryStream
    {
        private bool _gated;
        public GatedWriteStream(string existing) { Write(Encoding.UTF8.GetBytes(existing)); Position = 0; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_gated)
            {
                _gated = true;
                WriteEntered.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
