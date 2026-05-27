using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class PipelineBuilderTests
{
    [Fact]
    public void From_Source_ShouldReturnBuilder()
    {
        var source = new SimpleSource<int>(42);
        var builder = PipelineBuilder.From(source);
        builder.Should().NotBeNull();
    }

    [Fact]
    public async Task Transform_And_To_ShouldComplete()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();

        var builder = PipelineBuilder.From(source);
        await builder.Transform(transformer).To(sink);

        sink.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task WithOptions_ShouldApplyOptions()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();

        var builder = PipelineBuilder.From(source);
        await builder
            .Transform(transformer)
            .WithOptions(o =>
            {
                o.MaxDegreeOfParallelism = 2;
            })
            .To(sink);

        sink.Results.Should().HaveCount(3);
    }

    [Fact]
    public void PipelineBuilder_Generic_ShouldBeChainable()
    {
        var source = new SimpleSource<int>(42);
        var builder = PipelineBuilder.From(source);
        builder.Should().NotBeNull();
    }

    [Fact]
    public async Task Pipe_And_WithOptions_ShouldBeChainable()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer1 = new PassthroughTransformer<int>();
        var transformer2 = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();

        var builder = PipelineBuilder.From(source);
        await builder
            .Transform(transformer1)
            .Pipe(transformer2)
            .WithOptions(o =>
            {
                o.BoundedCapacity = 500;
            })
            .To(sink);

        sink.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pipe_WithMultipleSameTypeTransforms_ShouldExecuteInOrder()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var sink = new CollectionSink<int>();

        await PipelineBuilder
            .From(source)
            .Transform(new FuncTransformer<int>(x => x * 2))
            .Pipe(new FuncTransformer<int>(x => x + 1))
            .To(sink);

        sink.Results.Should().Equal(3, 5, 7);
    }

    private sealed class FuncTransformer<T> : ITransformer<T, T>
    {
        private readonly Func<T, T> _transform;

        public FuncTransformer(Func<T, T> transform)
        {
            _transform = transform;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<ProcessingResult<T>> TransformAsync(
            ProcessingContext<T> ctx,
            CancellationToken ct = default
        )
        {
            return ValueTask.FromResult(
                ProcessingResult<T>.Success(_transform(ctx.Payload), ctx.TraceId)
            );
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
