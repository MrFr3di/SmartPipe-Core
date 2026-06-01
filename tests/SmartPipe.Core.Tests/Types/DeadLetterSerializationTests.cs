using FluentAssertions;
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
}

public sealed record DeadLetterAotPayload(int Id, string Name);

[JsonSerializable(typeof(DeadLetterAotPayload))]
[JsonSerializable(typeof(DeadLetterEnvelope<DeadLetterAotPayload>))]
internal sealed partial class DeadLetterSerializationTestJsonContext : JsonSerializerContext;
