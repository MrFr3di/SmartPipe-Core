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
        
        var result = await composite.TransformAsync(new ProcessingContext<int>(5));
        
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
        
        var result = await composite.TransformAsync(new ProcessingContext<int>(5));
        
        result.IsSuccess.Should().BeFalse(); // t2 fails
    }

    [Fact]
    public async Task Composite_ShouldPreserveTraceIdAcrossTransforms()
    {
        var observedTraceIds = new List<ulong>();
        var t1 = new ObservingTransform(x => x * 2, observedTraceIds);
        var t2 = new ObservingTransform(x => x + 1, observedTraceIds);
        var composite = new CompositeTransform<int>(t1, t2);
        var context = new ProcessingContext<int>(5);

        var result = await composite.TransformAsync(context);

        result.IsSuccess.Should().BeTrue();
        result.TraceId.Should().Be(context.TraceId);
        observedTraceIds.Should().Equal(context.TraceId, context.TraceId);
    }

    private class TestTransform : ITransformer<int, int>
    {
        private readonly Func<int, int> _f;
        public TestTransform(Func<int, int> f) => _f = f;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask<ProcessingResult<int>> TransformAsync(ProcessingContext<int> ctx, CancellationToken ct = default)
            => ValueTask.FromResult(ProcessingResult<int>.Success(_f(ctx.Payload), ctx.TraceId));
        public Task DisposeAsync() => Task.CompletedTask;
    }

    private class FailTransform<T> : ITransformer<T, T>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
            => ValueTask.FromResult(ProcessingResult<T>.Failure(new SmartPipeError("Fail", ErrorType.Permanent), ctx.TraceId));
        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class ObservingTransform : ITransformer<int, int>
    {
        private readonly Func<int, int> _f;
        private readonly List<ulong> _traceIds;

        public ObservingTransform(Func<int, int> f, List<ulong> traceIds)
        {
            _f = f;
            _traceIds = traceIds;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<ProcessingResult<int>> TransformAsync(
            ProcessingContext<int> ctx,
            CancellationToken ct = default)
        {
            _traceIds.Add(ctx.TraceId);
            return ValueTask.FromResult(ProcessingResult<int>.Success(_f(ctx.Payload), ctx.TraceId));
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
