using FluentAssertions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class DeadLetterSerializationTests
{
    [Fact]
    public async Task JsonLinesSerializer_ShouldRoundTripEnvelope()
    {
        var serializer = new JsonLinesDeadLetterSerializer<string>();
        var envelope = new DeadLetterEnvelope<string>
        {
            SchemaVersion = 1,
            PipelineId = "pipeline",
            RunId = "run",
            TraceId = 42,
            StageId = "stage",
            StageName = "Transform",
            OriginalPayload = "payload",
            Metadata = MetadataBag.Empty.Set("key", "value"),
            Error = new SmartPipeError("failed", ErrorType.Permanent, "Test"),
            Attempt = 2,
            FailedAtUtc = DateTimeOffset.UnixEpoch,
        };

        using var stream = new MemoryStream();

        await serializer.WriteAsync(envelope, stream);
        stream.Position = 0;
        var read = new List<DeadLetterEnvelope<string>>();
        await foreach (var item in serializer.ReadAsync(stream))
            read.Add(item);

        read.Should().ContainSingle();
        read[0].OriginalPayload.Should().Be("payload");
        read[0].Metadata.GetString("key").Should().Be("value");
        read[0].Error.Message.Should().Be("failed");
    }

    [Fact]
    public async Task JsonLinesSerializer_WithSourceGeneratedTypeInfo_ShouldRoundTripEnvelope()
    {
        var serializer = new JsonLinesDeadLetterSerializer<DeadLetterAotPayload>(
            DeadLetterSerializationTestJsonContext.Default.DeadLetterEnvelopeDeadLetterAotPayload);
        var envelope = new DeadLetterEnvelope<DeadLetterAotPayload>
        {
            SchemaVersion = 1,
            PipelineId = "pipeline",
            RunId = "run",
            TraceId = 43,
            StageId = "stage",
            StageName = "Transform",
            OriginalPayload = new DeadLetterAotPayload(10, "payload"),
            Metadata = MetadataBag.Empty.Set("key", "value"),
            Error = new SmartPipeError("failed", ErrorType.Permanent, "Test"),
            Attempt = 1,
            FailedAtUtc = DateTimeOffset.UnixEpoch,
        };

        using var stream = new MemoryStream();

        await serializer.WriteAsync(envelope, stream);
        stream.Position = 0;
        var read = new List<DeadLetterEnvelope<DeadLetterAotPayload>>();
        await foreach (var item in serializer.ReadAsync(stream))
            read.Add(item);

        read.Should().ContainSingle();
        read[0].OriginalPayload.Should().Be(new DeadLetterAotPayload(10, "payload"));
        read[0].Metadata.GetString("key").Should().Be("value");
        read[0].Error.Message.Should().Be("failed");
    }

    [Fact]
    public async Task JsonLinesSerializer_ShouldAppendExactlyOneNewLinePerWrite()
    {
        var serializer = new JsonLinesDeadLetterSerializer<string>();
        using var stream = new MemoryStream();

        await serializer.WriteAsync(CreateEnvelope("payload"), stream);

        var jsonl = Encoding.UTF8.GetString(stream.ToArray());
        jsonl.Should().EndWith("\n");
        jsonl.Count(c => c == '\n').Should().Be(1);
    }

    [Fact]
    public async Task JsonLinesSerializer_ShouldProduceOneJsonLinePerWrite()
    {
        var serializer = new JsonLinesDeadLetterSerializer<string>();
        using var stream = new MemoryStream();

        await serializer.WriteAsync(CreateEnvelope("first"), stream);
        await serializer.WriteAsync(CreateEnvelope("second"), stream);

        var jsonl = Encoding.UTF8.GetString(stream.ToArray());
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("first");
        lines[1].Should().Contain("second");
    }

    [Fact]
    public async Task JsonLinesSerializer_WithSourceGeneratedTypeInfo_ShouldAppendExactlyOneNewLinePerWrite()
    {
        var serializer = new JsonLinesDeadLetterSerializer<DeadLetterAotPayload>(
            DeadLetterSerializationTestJsonContext.Default.DeadLetterEnvelopeDeadLetterAotPayload);
        using var stream = new MemoryStream();

        await serializer.WriteAsync(CreateAotEnvelope(new DeadLetterAotPayload(10, "payload")), stream);

        var jsonl = Encoding.UTF8.GetString(stream.ToArray());
        jsonl.Should().EndWith("\n");
        jsonl.Count(c => c == '\n').Should().Be(1);
    }

    [Fact]
    public async Task JsonLinesSerializer_ShouldDefensivelyCopySerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        var serializer = new JsonLinesDeadLetterSerializer<string>(options);
        options.WriteIndented = true;
        using var stream = new MemoryStream();

        await serializer.WriteAsync(CreateEnvelope("payload"), stream);

        Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\n').Should().NotContain("\n");
    }

    [Fact]
    public async Task JsonLinesSerializer_ReadsLegacyRootArrayWithoutBufferingJsonLinesContract()
    {
        var serializer = new JsonLinesDeadLetterSerializer<string>();
        var json = JsonSerializer.Serialize(new[] { CreateEnvelope("first"), CreateEnvelope("second") });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var payloads = new List<string>();

        await foreach (var envelope in serializer.ReadAsync(stream))
            payloads.Add(envelope.OriginalPayload);

        payloads.Should().Equal("first", "second");
    }

    [Fact]
    public async Task JsonLinesSerializer_ReadsJsonLinesFromNonSeekableStream()
    {
        var serializer = new JsonLinesDeadLetterSerializer<string>();
        using var bytes = new MemoryStream();
        await serializer.WriteAsync(CreateEnvelope("first"), bytes);
        await serializer.WriteAsync(CreateEnvelope("second"), bytes);
        await using var stream = new NonSeekableReadStream(bytes.ToArray());
        var payloads = new List<string>();

        await foreach (var envelope in serializer.ReadAsync(stream))
            payloads.Add(envelope.OriginalPayload);

        payloads.Should().Equal("first", "second");
    }

    [Fact]
    public async Task JsonLinesSerializer_ReadsBomWhenNonSeekableStreamReturnsPartialPrefixReads()
    {
        var serializer = new JsonLinesDeadLetterSerializer<string>();
        using var json = new MemoryStream();
        await serializer.WriteAsync(CreateEnvelope("payload"), json);
        var bytes = Encoding.UTF8.GetPreamble().Concat(json.ToArray()).ToArray();
        await using var stream = new NonSeekableReadStream(bytes, maxReadSize: 1);
        var payloads = new List<string>();

        await foreach (var envelope in serializer.ReadAsync(stream))
            payloads.Add(envelope.OriginalPayload);

        payloads.Should().Equal("payload");
    }

    private static DeadLetterEnvelope<string> CreateEnvelope(string payload) => new()
    {
        SchemaVersion = 1,
        PipelineId = "pipeline",
        RunId = "run",
        TraceId = 42,
        StageId = "stage",
        StageName = "Transform",
        OriginalPayload = payload,
        Metadata = MetadataBag.Empty,
        Error = new SmartPipeError("failed", ErrorType.Permanent, "Test"),
        Attempt = 1,
        FailedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static DeadLetterEnvelope<DeadLetterAotPayload> CreateAotEnvelope(DeadLetterAotPayload payload) => new()
    {
        SchemaVersion = 1,
        PipelineId = "pipeline",
        RunId = "run",
        TraceId = 42,
        StageId = "stage",
        StageName = "Transform",
        OriginalPayload = payload,
        Metadata = MetadataBag.Empty,
        Error = new SmartPipeError("failed", ErrorType.Permanent, "Test"),
        Attempt = 1,
        FailedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class NonSeekableReadStream(byte[] data, int maxReadSize = int.MaxValue) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..System.Math.Min(buffer.Length, maxReadSize)], cancellationToken);
    }
}

public sealed record DeadLetterAotPayload(int Id, string Name);

[JsonSerializable(typeof(DeadLetterAotPayload))]
[JsonSerializable(typeof(DeadLetterEnvelope<DeadLetterAotPayload>))]
internal sealed partial class DeadLetterSerializationTestJsonContext : JsonSerializerContext;
