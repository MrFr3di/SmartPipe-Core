using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class MiddlewareTransformerTests
{
    [Fact]
    public async Task Transform_ShouldApplyFunc()
    {
        var transformer = new MiddlewareTransformer<int>(x => x * 2);
        var ctx = new ProcessingContext<int>(21);

        var result = await transformer.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task PipelineBuilder_Transform_ShouldAcceptMiddleware()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var sink = new CollectionSink<int>();

        // Middleware работает только на первом шаге (PipelineBuilder<T>.Transform)
        // или через явный MiddlewareTransformer в ITransformer
        await PipelineBuilder
            .From(source)
            .Transform<int>(new MiddlewareTransformer<int>(x => x * 2))  // Явно как ITransformer
            .To(sink);

        sink.Results.Should().Equal(2, 4, 6);
    }

    [Fact]
    public async Task PipelineBuilder_FirstStep_ShouldAcceptMiddlewareFunc()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var sink = new CollectionSink<int>();

        // Func<int,int> на первом шаге — через Transform(Func)
        await PipelineBuilder
            .From(source)
            .Transform(x => x * 10)          // Middleware на первом шаге
            .To(sink);

        sink.Results.Should().Equal(10, 20, 30);
    }

    [Fact]
    public async Task Middleware_WithException_ShouldReturnFailure()
    {
        var transformer = new MiddlewareTransformer<int>(x => throw new InvalidOperationException("Test"));
        var ctx = new ProcessingContext<int>(1);

        var result = await transformer.TransformAsync(ctx);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Message.Should().Contain("Test");
        result.Error!.Value.Category.Should().Be("Middleware");
    }
}
