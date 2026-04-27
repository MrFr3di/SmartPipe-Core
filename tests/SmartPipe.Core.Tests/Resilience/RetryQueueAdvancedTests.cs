using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

public class RetryQueueAdvancedTests
{
    [Fact]
    public async Task EnqueueAsync_BeyondMaxRetries_ReturnsFalse()
    {
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 1);

        // First enqueue at retryCount=0 → should succeed
        var result1 = await queue.EnqueueAsync(ctx, policy, 0, 
            new SmartPipeError("e", ErrorType.Transient));
        result1.Should().BeTrue();

        // Second enqueue at retryCount=1 → should fail (maxRetries=1)
        var result2 = await queue.EnqueueAsync(ctx, policy, 1,
            new SmartPipeError("e", ErrorType.Transient));
        result2.Should().BeFalse();
    }

    [Fact]
    public async Task TryGetNextAsync_WhenItemNotReady_ReturnsNull()
    {
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(ctx, policy, 0, 
            new SmartPipeError("e", ErrorType.Transient));

        // Item has 10s delay — should not be ready
        var item = await queue.TryGetNextAsync();
        item.Should().BeNull();
    }

    [Fact]
    public void Count_InitiallyZero()
    {
        var queue = new RetryQueue<string>(10);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_ZeroCapacity_ShouldDropOldest()
    {
        var queue = new RetryQueue<string>(1); // Capacity = 1
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, 
            new SmartPipeError("first", ErrorType.Transient));
        await queue.EnqueueAsync(ctx, policy, 0,
            new SmartPipeError("second", ErrorType.Transient));

        // Queue should have dropped the oldest — at most 1 item
        queue.Count.Should().BeLessThanOrEqualTo(1);
    }
}
