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
        var result = await transform.TransformAsync(ProcessingEnvelope<int>.Create(10));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(20); // 10 * 2
    }

    [Fact]
    public async Task WhenConditionFalse_ShouldPassThrough()
    {
        var transform = new ConditionalTransform<int>(x => x > 5, new DoubleTransform());
        var result = await transform.TransformAsync(ProcessingEnvelope<int>.Create(3));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3); // unchanged
    }

    private class DoubleTransform : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
            => ValueTask.FromResult(StageResult<int>.Success(envelope.Payload * 2));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
