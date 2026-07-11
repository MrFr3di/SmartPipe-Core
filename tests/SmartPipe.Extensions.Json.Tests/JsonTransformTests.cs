using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class JsonTransformTests
{
    private class TestInput { public string Name { get; set; } = ""; public int Age { get; set; } }
    private class TestOutput { public string Name { get; set; } = ""; public int Age { get; set; } }

    [Fact]
    public async Task Transform_ValidObject_ShouldSerializeAndDeserialize()
    {
        var transform = new JsonTransform<TestInput, TestOutput>();
        var ctx = ProcessingEnvelope<TestInput>.Create(new TestInput { Name = "John", Age = 30 });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("John");
        result.Value.Age.Should().Be(30);
    }

    [Fact]
    public async Task Transform_WithSourceGeneratedJsonTypeInfo_ShouldSerializeAndDeserialize()
    {
        var transform = new JsonTransform<AotJsonInput, AotJsonOutput>(
            JsonTransformTestJsonContext.Default.AotJsonInput,
            JsonTransformTestJsonContext.Default.AotJsonOutput);
        var ctx = ProcessingEnvelope<AotJsonInput>.Create(new AotJsonInput("Jane", 31));

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new AotJsonOutput("Jane", 31));
    }

    [Fact]
    public async Task Transform_CancelledBeforeWork_ThrowsCancellation()
    {
        var transform = new JsonTransform<TestInput, TestOutput>();
        var envelope = ProcessingEnvelope<TestInput>.Create(new TestInput());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = async () => await transform.TransformAsync(envelope, cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Transform_NotSupportedByConverter_ReturnsPermanentFailure()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UnsupportedInputConverter());
        var transform = new JsonTransform<TestInput, TestOutput>(options);
        var envelope = ProcessingEnvelope<TestInput>.Create(new TestInput());

        var result = await transform.TransformAsync(envelope);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.InnerException.Should().BeOfType<NotSupportedException>();
    }

    private sealed class UnsupportedInputConverter : JsonConverter<TestInput>
    {
        public override TestInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("read is unsupported");

        public override void Write(Utf8JsonWriter writer, TestInput value, JsonSerializerOptions options) =>
            throw new NotSupportedException("write is unsupported");
    }
}

public sealed record AotJsonInput(string Name, int Age);

public sealed record AotJsonOutput(string Name, int Age);

[JsonSerializable(typeof(AotJsonInput))]
[JsonSerializable(typeof(AotJsonOutput))]
internal sealed partial class JsonTransformTestJsonContext : JsonSerializerContext;
