using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class MapsterTransformTests
{
    private class Source { public string Name { get; set; } = ""; public int Age { get; set; } }
    private class Destination { public string Name { get; set; } = ""; public int Age { get; set; } }

    [Fact]
    public async Task Transform_ShouldMapAllProperties()
    {
        var transform = new MapsterTransform<Source, Destination>();
        var ctx = new ProcessingContext<Source>(new Source { Name = "Alice", Age = 25 });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Alice");
        result.Value.Age.Should().Be(25);
    }
}
