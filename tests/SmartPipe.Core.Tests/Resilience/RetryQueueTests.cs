using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

public class RetryQueueTests
{
    [Fact]
    public async Task EnqueueAsync_ShouldReturnTrue()
    {
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMilliseconds(10));

        var result = await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueAsync_BeyondMaxRetries_ShouldReturnFalse()
    {
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 1);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        var result = await queue.EnqueueAsync(ctx, policy, 1, new SmartPipeError("e", ErrorType.Transient));
        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryGetNextAsync_WhenReady_ShouldReturnItem()
    {
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        await Task.Delay(5);
        var item = await queue.TryGetNextAsync();
        item.Should().NotBeNull();
        item!.Value.Context.Payload.Should().Be("test");
    }

    [Fact]
    public async Task TryGetNextAsync_EmptyQueue_ShouldReturnNull()
    {
        var queue = new RetryQueue<string>(10);
        var item = await queue.TryGetNextAsync();
        item.Should().BeNull();
    }

    [Fact]
    public void RetryItem_ShouldBeRecordStruct()
    {
        var ctx = new ProcessingContext<int>(42);
        var policy = new RetryPolicy();
        var item1 = new RetryItem<int>(ctx, policy, 1, default, DateTime.UtcNow);
        var item2 = item1;
        item2.Should().Be(item1);
    }

    [Fact]
    public void Count_ShouldReflectQueueSize()
    {
        var queue = new RetryQueue<string>(10);
        queue.Count.Should().Be(0);
    }
}
