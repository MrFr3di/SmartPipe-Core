using FluentAssertions;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class PollyResilienceTransformTests
{
    [Fact]
    public async Task Transform_WithRetryPipeline_ShouldSucceed()
    {
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 2 })
            .Build();

        var transform = new PollyResilienceTransform<string>(pipeline);
        var ctx = new ProcessingContext<string>("test");

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("test");
    }

    [Fact(Skip = "Timeout test requires async delay in transform")]
    public async Task Transform_WithTimeoutPipeline_ShouldFailOnTimeout()
    {
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromMilliseconds(1) })
            .Build();

        var transform = new PollyResilienceTransform<string>(pipeline);
        var ctx = new ProcessingContext<string>("test");

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Resilience");
    }
}
