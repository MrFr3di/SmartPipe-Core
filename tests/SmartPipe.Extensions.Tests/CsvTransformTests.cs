using System.Globalization;
using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class CsvTransformTests
{
    private class CsvInput { public string Name { get; set; } = ""; public int Value { get; set; } }
    private class CsvOutput { public string Name { get; set; } = ""; public int Value { get; set; } }

    [Fact]
    public async Task Transform_ShouldSerializeAndDeserialize()
    {
        var transform = new CsvTransform<CsvInput, CsvOutput>(",", CultureInfo.InvariantCulture);
        var ctx = new ProcessingContext<CsvInput>(new CsvInput { Name = "Test", Value = 42 });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Test");
        result.Value.Value.Should().Be(42);
    }
}
