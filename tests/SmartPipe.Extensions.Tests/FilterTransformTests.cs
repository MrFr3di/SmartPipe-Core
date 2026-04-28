using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class FilterTransformTests
{
    [Fact]
    public async Task Filter_ShouldPassMatchingItems()
    {
        var filter = new FilterTransform<int>(x => x > 5);
        var result = await filter.TransformAsync(new ProcessingContext<int>(10));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(10);
    }

    [Fact]
    public async Task Filter_ShouldBlockNonMatchingItems()
    {
        var filter = new FilterTransform<int>(x => x > 5);
        var result = await filter.TransformAsync(new ProcessingContext<int>(3));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Filtered");
    }

    [Fact]
    public void And_ShouldCombinePredicates()
    {
        var f1 = new FilterTransform<int>(x => x > 5);
        var f2 = new FilterTransform<int>(x => x < 20);
        var combined = f1.And(f2);

        combined.TransformAsync(new ProcessingContext<int>(10)).Result.IsSuccess.Should().BeTrue();
        combined.TransformAsync(new ProcessingContext<int>(3)).Result.IsSuccess.Should().BeFalse();
        combined.TransformAsync(new ProcessingContext<int>(25)).Result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Not_ShouldInvertPredicate()
    {
        var filter = new FilterTransform<int>(x => x > 5);
        var inverted = filter.Not();

        inverted.TransformAsync(new ProcessingContext<int>(3)).Result.IsSuccess.Should().BeTrue();
        inverted.TransformAsync(new ProcessingContext<int>(10)).Result.IsSuccess.Should().BeFalse();
    }
}
