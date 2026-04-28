using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class ConditionalTransformTests
{
    [Fact]
    public async Task WhenConditionTrue_ShouldApplyTransform()
    {
        var transform = new ConditionalTransform<int>(x => x > 5, new DoubleTransform());
        var result = await transform.TransformAsync(new ProcessingContext<int>(10));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(20); // 10 * 2
    }

    [Fact]
    public async Task WhenConditionFalse_ShouldPassThrough()
    {
        var transform = new ConditionalTransform<int>(x => x > 5, new DoubleTransform());
        var result = await transform.TransformAsync(new ProcessingContext<int>(3));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3); // unchanged
    }

    private class DoubleTransform : ITransformer<int, int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask<ProcessingResult<int>> TransformAsync(ProcessingContext<int> ctx, CancellationToken ct = default)
            => ValueTask.FromResult(ProcessingResult<int>.Success(ctx.Payload * 2, ctx.TraceId));
        public Task DisposeAsync() => Task.CompletedTask;
    }
}
