using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class CompositeTransformTests
{
    [Fact]
    public async Task Composite_ShouldApplyAllTransforms()
    {
        var t1 = new TestTransform(x => x * 2);
        var t2 = new TestTransform(x => x + 1);
        var composite = new CompositeTransform<int>(t1, t2);

        var result = await composite.TransformAsync(ProcessingEnvelope<int>.Create(5));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(11); // (5*2)+1
    }

    [Fact]
    public async Task Composite_ShouldStopOnFirstFailure()
    {
        var t1 = new TestTransform(x => x * 2);
        var t2 = new FailTransform<int>();
        var t3 = new TestTransform(x => x + 1);
        var composite = new CompositeTransform<int>(t1, t2, t3);

        var result = await composite.TransformAsync(ProcessingEnvelope<int>.Create(5));

        result.IsSuccess.Should().BeFalse(); // t2 fails
    }

    [Fact]
    public async Task Composite_ShouldPreserveTraceIdAcrossTransforms()
    {
        var observedTraceIds = new List<ulong>();
        var t1 = new ObservingTransform(x => x * 2, observedTraceIds);
        var t2 = new ObservingTransform(x => x + 1, observedTraceIds);
        var composite = new CompositeTransform<int>(t1, t2);
        var envelope = ProcessingEnvelope<int>.Create(5);

        var result = await composite.TransformAsync(envelope);

        result.IsSuccess.Should().BeTrue();
        observedTraceIds.Should().Equal(envelope.TraceId, envelope.TraceId);
    }

    private class TestTransform : IPipelineTransformer<int, int>
    {
        private readonly Func<int, int> _f;
        public TestTransform(Func<int, int> f) => _f = f;
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
            => ValueTask.FromResult(StageResult<int>.Success(_f(envelope.Payload)));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class FailTransform<T> : IPipelineTransformer<T, T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<T>> TransformAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
            => ValueTask.FromResult(StageResult<T>.Failure(new SmartPipeError("Fail", ErrorType.Permanent)));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservingTransform : IPipelineTransformer<int, int>
    {
        private readonly Func<int, int> _f;
        private readonly List<ulong> _traceIds;

        public ObservingTransform(Func<int, int> f, List<ulong> traceIds)
        {
            _f = f;
            _traceIds = traceIds;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            _traceIds.Add(envelope.TraceId);
            return ValueTask.FromResult(StageResult<int>.Success(_f(envelope.Payload)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
