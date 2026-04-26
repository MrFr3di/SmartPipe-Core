using FluentAssertions;
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
        var ctx = new ProcessingContext<TestInput>(new TestInput { Name = "John", Age = 30 });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("John");
        result.Value.Age.Should().Be(30);
    }
}
