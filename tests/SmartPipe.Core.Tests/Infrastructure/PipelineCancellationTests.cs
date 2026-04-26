using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class PipelineCancellationTests
{
    [Fact]
    public void CreateTimeout_ShouldConfigureTimeout()
    {
        var cts = PipelineCancellation.CreateTimeout(TimeSpan.FromSeconds(1));
        cts.Token.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task WithTimeoutAsync_WithinTimeout_ShouldSucceed()
    {
        var task = new ValueTask<ProcessingResult<int>>(
            ProcessingResult<int>.Success(42, 1UL));

        var result = await task.WithTimeoutAsync(TimeSpan.FromSeconds(1), 1UL);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task WithTimeoutAsync_Timeout_ShouldReturnFailure()
    {
        var task = new ValueTask<ProcessingResult<int>>(
            Task.Delay(2000).ContinueWith(_ => ProcessingResult<int>.Success(42, 1UL)));

        var result = await task.WithTimeoutAsync(TimeSpan.FromMilliseconds(10), 1UL);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Timeout");
    }
}
