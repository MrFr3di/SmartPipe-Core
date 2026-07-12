#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

#pragma warning disable CS8620 // Suppress nullable mismatch with Moq ILogger verification

namespace SmartPipe.Extensions.Tests;

[Trait("Category", "CorrectnessRegression")]
public class DeadLetterSinkTests
{
    [Fact]
    public async Task WriteAsync_NullEnvelopePayload_ShouldNotStore()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            await sink.WriteAsync(ProcessingEnvelope<DeadLetterEnvelope<string>>.Create(null!));
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(tempFile);
            content.Should().BeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteAsync_DeadLetterEnvelope_ShouldStoreReplayContext()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            var deadLetter = CreateDeadLetter("failed payload", "test error", 42UL);

            await sink.WriteAsync(Envelope(deadLetter));
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(tempFile);
            var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            root.GetProperty("SchemaVersion").GetInt32().Should().Be(1);
            root.GetProperty("PipelineId").GetString().Should().Be("pipe");
            root.GetProperty("RunId").GetString().Should().Be("run");
            root.GetProperty("TraceId").GetUInt64().Should().Be(42UL);
            root.GetProperty("StageId").GetString().Should().Be("stage");
            root.GetProperty("OriginalPayload").GetString().Should().Be("failed payload");
            root.GetProperty("Error").GetProperty("Message").GetString().Should().Be("test error");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteAsync_WithSourceGeneratedTypeInfo_ShouldStoreDeadLetterEnvelope()
    {
        await using var memoryStream = new MemoryStream();
        var sink = new DeadLetterSink<AotDeadLetterItem>(
            "dummy.json",
            DeadLetterSinkTestJsonContext.Default.DeadLetterEnvelopeAotDeadLetterItem,
            logger: null,
            stream: memoryStream);

        var deadLetter = CreateDeadLetter(new AotDeadLetterItem(7, "seven"), "source-gen failure", 99UL);

        await sink.WriteAsync(Envelope(deadLetter));
        await sink.DisposeAsync();

        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream, Encoding.UTF8, leaveOpen: true);
        var content = await reader.ReadToEndAsync();

        content.Should().Contain("source-gen failure");
        content.Should().Contain("\"TraceId\":99");
        content.Should().Contain("\"OriginalPayload\"");
        content.Should().Contain("\"Name\":\"seven\"");
    }

    [Fact]
    public async Task WriteAsync_IoException_ThrowsAfterExhaustedAttemptsByDefault()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();
        loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var lineWriter = new FaultInjectingDeadLetterLineWriter(
            new IOException("first write failed"),
            new IOException("second write failed"),
            new IOException("third write failed"),
            new IOException("fourth write failed"));
        var sink = new DeadLetterSink<string>("dummy.json", loggerMock.Object, lineWriter);

        var exception = await Assert.ThrowsAsync<DeadLetterWriteException>(async () =>
            await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "test error", 1UL))));
        await sink.DisposeAsync();

        exception.Path.Should().Be("dummy.json");
        exception.Attempts.Should().Be(DeadLetterSink<string>.MaxAttempts);
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("attempt")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to write")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        lineWriter.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_IoException_LogAndDropSkipsAfterExhaustedAttempts()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();
        loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var lineWriter = new FaultInjectingDeadLetterLineWriter(
            new IOException("first write failed"),
            new IOException("second write failed"),
            new IOException("third write failed"),
            new IOException("fourth write failed"));
        var sink = new DeadLetterSink<string>("dummy.json", loggerMock.Object, lineWriter)
        {
            FailureMode = DeadLetterWriteFailureMode.LogAndDrop,
        };

        await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "test error", 1UL)));
        await sink.DisposeAsync();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to write")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        lineWriter.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_IoException_RecoversOnRetry()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();
        loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var lineWriter = new FaultInjectingDeadLetterLineWriter(
            new IOException("first write failed"),
            new IOException("second write failed"));
        var sink = new DeadLetterSink<string>("dummy.json", loggerMock.Object, lineWriter);

        await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "recoverable error", 99UL)));
        await sink.DisposeAsync();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("attempt")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));

        var writtenContent = lineWriter.Lines.Single();
        writtenContent.Should().Contain("recoverable error");
        writtenContent.Should().Contain("\"TraceId\":99");
    }

    [Fact]
    public async Task InitializeAsync_PathBackedWriter_AppendsWithoutTruncatingExistingFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "existing\n");
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "append error", 123UL)));
            await sink.DisposeAsync();

            var lines = (await File.ReadAllLinesAsync(tempFile))
                .Where(line => line.Length > 0)
                .ToArray();
            lines.Should().HaveCount(2);
            lines[0].Should().Be("existing");
            lines[1].Should().Contain("\"TraceId\":123");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteAsync_DefaultFlushEachWrite_FlushesSuccessfulWrite()
    {
        await using var stream = new FlushCountingStream();
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);

        await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "flush error", 124UL)));
        await sink.DisposeAsync();

        stream.FlushCount.Should().BePositive();
    }

    [Fact]
    public async Task WriteAsync_SeekableStreamFailure_TruncatesCheckpointBeforeRetry()
    {
        await using var stream = new FailFirstWriteAfterPartialStream();
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);

        await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "rollback error", 125UL)));
        await sink.DisposeAsync();

        stream.Position = 0;
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        using var document = JsonDocument.Parse(lines[0]);
        document.RootElement.GetProperty("TraceId").GetUInt64().Should().Be(125UL);
    }

    [Fact]
    public async Task WriteAsync_NonSeekableAppendStream_FailsFastWithoutWriting()
    {
        await using var stream = new NonSeekablePartialFailureStream();
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, stream: stream);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "partial", 126UL))));
        await sink.DisposeAsync();

        exception.Message.Should().Contain("readable and seekable");
        stream.WriteAttempts.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_DrainsRemainingItems()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            for (var i = 0; i < 10; i++)
            {
                await sink.WriteAsync(Envelope(CreateDeadLetter($"payload {i}", $"error {i}", (ulong)i)));
            }

            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(tempFile);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(10);

            for (var i = 0; i < 10; i++)
            {
                using var document = JsonDocument.Parse(lines[i]);
                document.RootElement.GetProperty("TraceId").GetUInt64().Should().Be((ulong)i);
                document.RootElement.GetProperty("Error").GetProperty("Message").GetString().Should().Be($"error {i}");
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DisposeAsync_DisposesWriterOnce()
    {
        var lineWriter = new FaultInjectingDeadLetterLineWriter();
        var sink = new DeadLetterSink<string>("dummy.json", null, lineWriter);

        await sink.DisposeAsync();
        await sink.DisposeAsync();

        lineWriter.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCalls_ShareOneCompletion()
    {
        var lineWriter = new FaultInjectingDeadLetterLineWriter();
        var sink = new DeadLetterSink<string>("dummy.json", null, lineWriter);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sink.DisposeAsync().AsTask()));

        lineWriter.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task WriteAsync_CustomSerializer_IsInvokedOnceAcrossRetry()
    {
        var serializer = new CountingDeadLetterSerializer();
        var lineWriter = new FaultInjectingDeadLetterLineWriter(new IOException("retry"));
        var sink = new DeadLetterSink<string>(
            "dummy.json",
            serializer,
            new DeadLetterSinkOptions(),
            logger: null,
            lineWriter);

        await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "error", 42UL)));
        await sink.DisposeAsync();

        serializer.WriteCount.Should().Be(1);
        lineWriter.Lines.Should().ContainSingle().Which.Should().Contain("custom");
    }

    private static ProcessingEnvelope<DeadLetterEnvelope<T>> Envelope<T>(DeadLetterEnvelope<T> deadLetter) =>
        ProcessingEnvelope<DeadLetterEnvelope<T>>.Create(
            deadLetter,
            deadLetter.PipelineId,
            deadLetter.RunId,
            deadLetter.TraceId);

    private static DeadLetterEnvelope<T> CreateDeadLetter<T>(T payload, string message, ulong traceId) =>
        new()
        {
            SchemaVersion = 1,
            PipelineId = "pipe",
            RunId = "run",
            TraceId = traceId,
            StageId = "stage",
            StageName = "Stage",
            OriginalPayload = payload,
            Metadata = MetadataBag.Empty,
            Error = new SmartPipeError(message, ErrorType.Permanent),
            Attempt = 3,
            FailedAtUtc = new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero),
        };

    private sealed class FaultInjectingDeadLetterLineWriter : IDeadLetterLineWriter
    {
        private readonly Queue<Exception> _writeFailures;
        private readonly List<string> _lines = [];

        public FaultInjectingDeadLetterLineWriter(params Exception[] writeFailures)
        {
            _writeFailures = new Queue<Exception>(writeFailures);
        }

        public IReadOnlyList<string> Lines => _lines;

        public int DisposeCount { get; private set; }

        public ValueTask WriteRecordAsync(ReadOnlyMemory<byte> record, bool flushEachWrite, CancellationToken ct)
        {
            if (_writeFailures.Count > 0)
                throw _writeFailures.Dequeue();

            _lines.Add(Encoding.UTF8.GetString(record.Span).TrimEnd('\n'));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDeadLetterSerializer : IDeadLetterSerializer<string>
    {
        public int WriteCount { get; private set; }

        public async ValueTask WriteAsync(
            DeadLetterEnvelope<string> envelope,
            Stream stream,
            CancellationToken ct = default)
        {
            WriteCount++;
            await stream.WriteAsync("{\"custom\":true}\n"u8.ToArray(), ct);
        }

        public async IAsyncEnumerable<DeadLetterEnvelope<string>> ReadAsync(
            Stream stream,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }

    private sealed class FailFirstWriteAfterPartialStream : MemoryStream
    {
        private bool _failed;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                base.Write(buffer.Span[..Math.Min(3, buffer.Length)]);
                throw new IOException("partial write failed");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class NonSeekablePartialFailureStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public int WriteAttempts { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            _inner.Write(buffer.Span[..Math.Min(3, buffer.Length)]);
            throw new IOException("partial non-seekable write");
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

public sealed record AotDeadLetterItem(int Id, string Name);

[JsonSerializable(typeof(AotDeadLetterItem))]
[JsonSerializable(typeof(DeadLetterEnvelope<AotDeadLetterItem>))]
internal sealed partial class DeadLetterSinkTestJsonContext : JsonSerializerContext;
