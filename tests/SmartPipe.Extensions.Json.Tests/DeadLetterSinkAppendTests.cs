using System.Text;
using System.Text.Json;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using Xunit;

namespace SmartPipe.Extensions.Tests;

public sealed class DeadLetterSinkAppendTests
{
    [Theory]
    [InlineData("")]
    [InlineData("old\n")]
    [InlineData("old")]
    [InlineData("old\r")]
    public async Task Append_EnsuresExactlyOneBoundary(string existing)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, existing, TestContext.Current.CancellationToken);
            await WritePathAsync(path, 1);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var prefix = existing.Length == 0 || existing.EndsWith('\n') ? existing : existing + "\n";
            Assert.StartsWith(prefix + "{", text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain(prefix + "\n{", text, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Append_SeparatorAndRecordRollbackTogether_ThenRetryDoesNotDuplicateSeparator()
    {
        await using var stream = new FailFirstAppendWriteStream("partial");
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        await sink.WriteAsync(Envelope(2));
        await sink.DisposeAsync();
        Assert.StartsWith("partial\n{", stream.Text, StringComparison.Ordinal);
        Assert.Equal(1, Count(stream.Text, "partial\n"));
    }

    [Fact]
    public async Task Append_PartialExistingRecordRemainsSeparate_AcrossReopen()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "partial", TestContext.Current.CancellationToken);
            await WritePathAsync(path, 3);
            await WritePathAsync(path, 4);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.StartsWith("partial\n{", text, StringComparison.Ordinal);
            Assert.Equal(1, Count(text, "partial\n"));
            Assert.Equal(3, text.Count(c => c == '\n'));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Append_InjectedStreamWithoutReadAndSeek_FailsFast_AndRemainsOpen()
    {
        await using var stream = new WriteOnlyStream();
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.InitializeAsync().AsTask());
        Assert.Contains("readable and seekable", exception.Message, StringComparison.OrdinalIgnoreCase);
        await sink.DisposeAsync();
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task Append_InjectedReadOnlySeekableStream_FailsDuringInitializationBeforeSerializerCallback()
    {
        var existing = Encoding.UTF8.GetBytes("existing");
        await using var stream = new MemoryStream(existing, writable: false);
        var serializer = new CountingSerializer();
        var sink = new DeadLetterSink<string>(
            "dummy.json",
            serializer,
            new DeadLetterSinkOptions(),
            logger: null,
            stream);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("writable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, serializer.WriteCount);
        Assert.Equal(existing, stream.ToArray());
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task Append_BomOnly_DoesNotPrefixLineFeed()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble(), TestContext.Current.CancellationToken);
            await WritePathAsync(path, 5);
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
            Assert.Equal((byte)'{', bytes[3]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Append_CancellationAfterPartialPayload_RollsBackAndPreservesCancellation()
    {
        await using var stream = new TransactionFailureStream("partial", 2, new OperationCanceledException("cancelled"));
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        await Assert.ThrowsAsync<OperationCanceledException>(() => sink.WriteAsync(Envelope(6)).AsTask());
        Assert.Equal("partial", stream.Text);
    }

    [Fact]
    public async Task Append_RollbackFailure_RetainsOriginalAndReportsPossiblePartialRecord()
    {
        await using var stream = new TransactionFailureStream("partial", 2, new IOException("payload failed"), failRollback: true);
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        var exception = await Assert.ThrowsAsync<DeadLetterWriteException>(() => sink.WriteAsync(Envelope(7)).AsTask());
        Assert.True(exception.MayContainPartialRecord);
        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Contains(aggregate.InnerExceptions, e => e.Message.Contains("payload failed", StringComparison.Ordinal));
        Assert.Contains(aggregate.InnerExceptions, e => e.Message.Contains("rollback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Append_PayloadWriteFailure_RollsBackAndRetriesWithoutDuplicateBoundary()
    {
        await using var stream = new TransactionFailureStream("partial", 2, new IOException("payload failed"));
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        await sink.WriteAsync(Envelope(8));
        await sink.DisposeAsync();
        Assert.StartsWith("partial\n{", stream.Text, StringComparison.Ordinal);
        Assert.Equal(2, stream.Text.Count(c => c == '\n'));
    }

    [Fact]
    public async Task Append_FlushFailure_RollsBackAndRetriesWholeRecord()
    {
        await using var stream = new FlushOnceFailingStream("partial");
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        await sink.WriteAsync(Envelope(9));
        await sink.DisposeAsync();
        Assert.StartsWith("partial\n{", stream.Text, StringComparison.Ordinal);
        Assert.Equal(2, stream.Text.Count(c => c == '\n'));
    }

    [Fact]
    public async Task Append_DisposeWaitsForInFlightWrite_ThenCommitsExactlyOneBoundary()
    {
        await using var stream = new GatedWriteStream("partial");
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        var envelope = Envelope(10);
        var write = sink.WriteAsync(envelope, TestContext.Current.CancellationToken).AsTask();
        await stream.WriteEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dispose = sink.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        stream.ReleaseWrite.TrySetResult();

        await write;
        await dispose;
        Assert.Equal("partial\n" + JsonSerializer.Serialize(envelope.Payload) + "\n", stream.Text);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Append_ExternalStream_PreservesAppendSemantics_LeavesOpen_AndChecksBoundaryOnce()
    {
        await using var stream = new CountingPositionStream("partial") { Position = 2 };
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);
        var first = Envelope(11);
        var second = Envelope(12);

        await sink.WriteAsync(first, TestContext.Current.CancellationToken);
        await sink.WriteAsync(second, TestContext.Current.CancellationToken);
        await sink.DisposeAsync();

        var expected = "partial\n" + JsonSerializer.Serialize(first.Payload) + "\n" + JsonSerializer.Serialize(second.Payload) + "\n";
        Assert.Equal(expected, stream.Text);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
        Assert.Equal(1, stream.AsyncReadCount);
        Assert.True(stream.PositionSetCount >= 3);
    }

    private static async Task WritePathAsync(string path, ulong traceId)
    {
        var sink = new DeadLetterSink<string>(path);
        await sink.WriteAsync(Envelope(traceId));
        await sink.DisposeAsync();
    }

    private static ProcessingEnvelope<DeadLetterEnvelope<string>> Envelope(ulong traceId) => ProcessingEnvelope<DeadLetterEnvelope<string>>.Create(new()
    {
        SchemaVersion = 1,
        PipelineId = "p",
        RunId = "r",
        StageId = "s",
        StageName = "s",
        TraceId = traceId,
        OriginalPayload = "payload",
        Metadata = MetadataBag.Empty,
        Error = new SmartPipeError("failed", ErrorType.Permanent),
        Attempt = 1,
        FailedAtUtc = DateTimeOffset.UnixEpoch,
    });

    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    private sealed class CountingSerializer : IDeadLetterSerializer<string>
    {
        public int WriteCount { get; private set; }

        public ValueTask WriteAsync(DeadLetterEnvelope<string> envelope, Stream stream, CancellationToken ct = default)
        {
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<DeadLetterEnvelope<string>> ReadAsync(
            Stream stream,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

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
        public bool Disposed { get; private set; }
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
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class TransactionFailureStream : MemoryStream
    {
        private readonly int _failWriteOrdinal; private readonly Exception _failure; private readonly bool _failRollback; private int _writes;
        public TransactionFailureStream(string existing, int failWriteOrdinal, Exception failure, bool failRollback = false)
        { _failWriteOrdinal = failWriteOrdinal; _failure = failure; _failRollback = failRollback; Write(Encoding.UTF8.GetBytes(existing)); Position = 0; }
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes == _failWriteOrdinal) { base.Write(buffer.Span[..Math.Min(2, buffer.Length)]); return ValueTask.FromException(_failure); }
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override void SetLength(long value) { if (_failRollback) throw new IOException("rollback failed"); base.SetLength(value); }
    }

    private sealed class FlushOnceFailingStream : MemoryStream
    {
        private bool _failed;
        public FlushOnceFailingStream(string existing) { Write(Encoding.UTF8.GetBytes(existing)); Position = 0; }
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_failed) { _failed = true; return Task.FromException(new IOException("flush failed")); }
            return base.FlushAsync(cancellationToken);
        }
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

    private sealed class CountingPositionStream : MemoryStream
    {
        public CountingPositionStream(string existing) { Write(Encoding.UTF8.GetBytes(existing)); base.Position = 0; }
        public int AsyncReadCount { get; private set; }
        public int PositionSetCount { get; private set; }
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override long Position { get => base.Position; set { PositionSetCount++; base.Position = value; } }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { AsyncReadCount++; return base.ReadAsync(buffer, cancellationToken); }
    }
}
