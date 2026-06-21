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
    public async Task WriteAsync_IoException_RetriesAndSkips()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();
        await using var memoryStream = new MemoryStream();
        var sink = new DeadLetterSink<string>(path: "dummy.json", logger: loggerMock.Object, stream: memoryStream);
        sink.SetTestExceptionForTesting([true, true, true]);

        await sink.WriteAsync(Envelope(CreateDeadLetter("payload", "test error", 1UL)));

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("attempt")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to write")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task WriteAsync_IoException_RecoversOnRetry()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();
        await using var memoryStream = new MemoryStream();
        var sink = new DeadLetterSink<string>(path: "dummy.json", logger: loggerMock.Object, stream: memoryStream);
        sink.SetTestExceptionForTesting([true, true, false]);

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

        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream);
        var writtenContent = reader.ReadToEnd();
        writtenContent.Should().Contain("recoverable error");
        writtenContent.Should().Contain("\"TraceId\":99");
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
}

public sealed record AotDeadLetterItem(int Id, string Name);

[JsonSerializable(typeof(AotDeadLetterItem))]
[JsonSerializable(typeof(DeadLetterEnvelope<AotDeadLetterItem>))]
internal sealed partial class DeadLetterSinkTestJsonContext : JsonSerializerContext;
