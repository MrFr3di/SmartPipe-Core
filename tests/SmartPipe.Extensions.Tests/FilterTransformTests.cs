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
    public async Task And_ShouldCombinePredicates()
    {
        var f1 = new FilterTransform<int>(x => x > 5);
        var f2 = new FilterTransform<int>(x => x < 20);
        var combined = f1.And(f2);

        (await combined.TransformAsync(new ProcessingContext<int>(10))).IsSuccess.Should().BeTrue();
        (await combined.TransformAsync(new ProcessingContext<int>(3))).IsSuccess.Should().BeFalse();
        (await combined.TransformAsync(new ProcessingContext<int>(25))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task And_ShouldCombineAsyncPredicates()
    {
        var f1 = new FilterTransform<int>(x => Task.FromResult(x > 5));
        var f2 = new FilterTransform<int>(x => Task.FromResult(x < 20));
        var combined = f1.And(f2);

        (await combined.TransformAsync(new ProcessingContext<int>(10))).IsSuccess.Should().BeTrue();
        (await combined.TransformAsync(new ProcessingContext<int>(3))).IsSuccess.Should().BeFalse();
        (await combined.TransformAsync(new ProcessingContext<int>(25))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Or_ShouldCombineAsyncPredicates()
    {
        var f1 = new FilterTransform<int>(x => Task.FromResult(x < 5));
        var f2 = new FilterTransform<int>(x => Task.FromResult(x > 20));
        var combined = f1.Or(f2);

        (await combined.TransformAsync(new ProcessingContext<int>(3))).IsSuccess.Should().BeTrue();
        (await combined.TransformAsync(new ProcessingContext<int>(25))).IsSuccess.Should().BeTrue();
        (await combined.TransformAsync(new ProcessingContext<int>(10))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Not_ShouldInvertPredicate()
    {
        var filter = new FilterTransform<int>(x => x > 5);
        var inverted = filter.Not();

        (await inverted.TransformAsync(new ProcessingContext<int>(3))).IsSuccess.Should().BeTrue();
        (await inverted.TransformAsync(new ProcessingContext<int>(10))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Not_ShouldInvertAsyncPredicate()
    {
        var filter = new FilterTransform<int>(x => Task.FromResult(x > 5));
        var inverted = filter.Not();

        (await inverted.TransformAsync(new ProcessingContext<int>(3))).IsSuccess.Should().BeTrue();
        (await inverted.TransformAsync(new ProcessingContext<int>(10))).IsSuccess.Should().BeFalse();
    }
}
