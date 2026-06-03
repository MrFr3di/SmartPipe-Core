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
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.DropOldest); // Capacity = 1
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, 
            new SmartPipeError("first", ErrorType.Transient));
        await queue.EnqueueAsync(ctx, policy, 0,
            new SmartPipeError("second", ErrorType.Transient));

        // Queue should have dropped the oldest — at most 1 item
        queue.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_Wait_ShouldNotDropRetryItem_WhenCapacityAvailableLater()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.Wait);
        var ctx1 = new ProcessingContext<string>("item1");
        var ctx2 = new ProcessingContext<string>("item2");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        // Fill queue
        await queue.EnqueueAsync(ctx1, policy, 0, new SmartPipeError("e1", ErrorType.Transient));

        // Second enqueue should wait (done in background)
        var enqueueTask = queue.EnqueueAsync(ctx2, policy, 0, new SmartPipeError("e2", ErrorType.Transient));

        // Dequeue first item to free capacity
        await Task.Delay(50);
        var item1 = await queue.TryGetNextAsync();
        item1.Should().NotBeNull();

        // Second enqueue should now complete
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(enqueueTask.AsTask(), Task.Delay(Timeout.Infinite, cts.Token));
        completed.Should().Be(enqueueTask.AsTask(), "Wait policy should complete when capacity is available");
        (await enqueueTask).Should().BeTrue();
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_Wait_ShouldRespectCancellation_WhenQueueFull()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.Wait);
        var ctx = new ProcessingContext<string>("item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e1", ErrorType.Transient));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var act = async () => await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e2", ErrorType.Transient), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_FailFast_ShouldReturnFailure_WhenQueueFull()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.FailFast);
        var ctx = new ProcessingContext<string>("item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e1", ErrorType.Transient));
        var result = await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e2", ErrorType.Transient));

        result.Should().BeFalse("FailFast should return false when queue is full");
    }

    [Fact]
    public async Task RetryQueue_FailFast_ShouldReturnFalse_WhenTryWriteFailsAtCapacity()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.FailFast);
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMinutes(1));

        var first = await queue.EnqueueAsync(
            new ProcessingContext<string>("first"),
            policy,
            0,
            new SmartPipeError("first", ErrorType.Transient));
        var second = await queue.EnqueueAsync(
            new ProcessingContext<string>("second"),
            policy,
            0,
            new SmartPipeError("second", ErrorType.Transient));

        first.Should().BeTrue();
        second.Should().BeFalse("FailFast must report the actual failed write at capacity");
        queue.PendingCount.Should().Be(1);
        queue.HasPendingItems.Should().BeTrue();
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_DeadLetter_ShouldWriteToDeadLetterSink_WhenQueueFull()
    {
        var sink = new CollectingDeadLetterSink();
        var queue = new RetryQueue<string>(1, deadLetterSink: sink, overflowPolicy: RetryQueueOverflowPolicy.DeadLetter);
        var ctx = new ProcessingContext<string>("dead-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);
        var error = new SmartPipeError("overflow", ErrorType.Transient);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e1", ErrorType.Transient));
        var result = await queue.EnqueueAsync(ctx, policy, 0, error);

        result.Should().BeFalse();
        sink.Count.Should().Be(1, "overflowed item should be written to dead-letter sink exactly once");
    }

    [Fact]
    public async Task RetryQueue_DeadLetter_ShouldDeadLetter_WhenTryWriteFailsAtCapacity()
    {
        var sink = new CollectingDeadLetterSink();
        var queue = new RetryQueue<string>(
            1,
            deadLetterSink: sink,
            overflowPolicy: RetryQueueOverflowPolicy.DeadLetter);
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMinutes(1));

        var first = await queue.EnqueueAsync(
            new ProcessingContext<string>("first"),
            policy,
            0,
            new SmartPipeError("first", ErrorType.Transient));
        var second = await queue.EnqueueAsync(
            new ProcessingContext<string>("second"),
            policy,
            0,
            new SmartPipeError("second", ErrorType.Transient));

        first.Should().BeTrue();
        second.Should().BeFalse("DeadLetter policy must report the failed write at capacity");
        sink.Count.Should().Be(1, "overflowed item should be dead-lettered exactly once");
        queue.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_DeadLetter_ShouldFallbackToFailure_WhenNoDeadLetterSink()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.DeadLetter);
        var ctx = new ProcessingContext<string>("item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e1", ErrorType.Transient));
        var result = await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e2", ErrorType.Transient));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_DropNewest_ShouldDropIncomingRetry_WhenQueueFull()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.DropNewest);
        var ctx1 = new ProcessingContext<string>("old-item");
        var ctx2 = new ProcessingContext<string>("new-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        var r1 = await queue.EnqueueAsync(ctx1, policy, 0, new SmartPipeError("e1", ErrorType.Transient));
        r1.Should().BeTrue();
        var r2 = await queue.EnqueueAsync(ctx2, policy, 0, new SmartPipeError("e2", ErrorType.Transient));
        r2.Should().BeTrue();

        // Verify the item still in queue is the old one (since newest is dropped)
        await Task.Delay(5);
        var item = await queue.TryGetNextAsync();
        item.Should().NotBeNull();
        item!.Value.Context.Payload.Should().Be("old-item");
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_DropOldest_ShouldDropOldestRetry_WhenQueueFull()
    {
        var queue = new RetryQueue<string>(1, overflowPolicy: RetryQueueOverflowPolicy.DropOldest);
        var ctx1 = new ProcessingContext<string>("old-item");
        var ctx2 = new ProcessingContext<string>("new-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        var r1 = await queue.EnqueueAsync(ctx1, policy, 0, new SmartPipeError("e1", ErrorType.Transient));
        r1.Should().BeTrue();
        var r2 = await queue.EnqueueAsync(ctx2, policy, 0, new SmartPipeError("e2", ErrorType.Transient));
        r2.Should().BeTrue();

        await Task.Delay(5);
        var item = await queue.TryGetNextAsync();
        item.Should().NotBeNull();
        item!.Value.Context.Payload.Should().Be("new-item");
    }

    [Fact]
    public async Task RetryQueueOverflowPolicy_DefaultShouldNotChange_BudgetExhaustedBehavior()
    {
        var queue = new RetryQueue<string>(10, overflowPolicy: RetryQueueOverflowPolicy.Wait);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero);

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        var result = await queue.EnqueueAsync(ctx, policy, 1, new SmartPipeError("e", ErrorType.Transient));

        result.Should().BeFalse("Budget exhausted should return false regardless of overflow policy");
    }

    [Fact]
    public async Task RetryQueue_FailFast_ShouldPreserveNotReadyDelayedRetry()
    {
        // Arrange: FailFast overflow with a controllable clock — retry scheduled far in the future
        var clock = new FakeClock { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var queue = new RetryQueue<string>(
            capacity: 2,
            overflowPolicy: RetryQueueOverflowPolicy.FailFast,
            clock: clock,
            pollTimeoutMs: 5000);

        var ctx = new ProcessingContext<string>("delayed-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromHours(1));

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));

        // Act: poll — item should not be ready (1 hour delay)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var item = await queue.TryGetNextAsync(cts.Token);

        // Assert: not ready returns null but item remains in queue
        item.Should().BeNull("item with 1-hour delay should not be ready");
        queue.Count.Should().Be(1, "not-ready item must be preserved in the queue, not dropped");
        queue.PendingCount.Should().Be(1, "not-ready polling must not remove the pending schedule");

        // Now advance the clock and poll again — item should be retrievable
        clock.UtcNow = clock.UtcNow.AddHours(2);
        var readyItem = await queue.TryGetNextAsync();
        readyItem.Should().NotBeNull("item should be ready after clock advanced past its RetryAt");
        readyItem!.Value.Context.Payload.Should().Be("delayed-item");
        queue.HasPendingItems.Should().BeFalse();
    }

    [Fact]
    public async Task RetryQueue_DeadLetter_ShouldPreserveNotReadyDelayedRetry()
    {
        // Arrange: DeadLetter overflow with dead-letter sink — retry scheduled far in the future
        var clock = new FakeClock { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var sink = new CollectingDeadLetterSink();
        var queue = new RetryQueue<string>(
            capacity: 2,
            overflowPolicy: RetryQueueOverflowPolicy.DeadLetter,
            deadLetterSink: sink,
            clock: clock,
            pollTimeoutMs: 5000);

        var ctx = new ProcessingContext<string>("delayed-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromHours(1));

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));

        // Act: poll — item should not be ready
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var item = await queue.TryGetNextAsync(cts.Token);

        // Assert: not ready returns null, item stays in queue, NOT dead-lettered
        item.Should().BeNull("item with 1-hour delay should not be ready");
        queue.Count.Should().Be(1, "not-ready item must be preserved in the queue");
        queue.PendingCount.Should().Be(1, "not-ready polling must preserve pending state");
        sink.Count.Should().Be(0, "not-ready item must NOT be written to dead-letter sink");

        // Verify it can be retrieved later
        clock.UtcNow = clock.UtcNow.AddHours(2);
        var readyItem = await queue.TryGetNextAsync();
        readyItem.Should().NotBeNull("item should be ready after clock advanced");
        readyItem!.Value.Context.Payload.Should().Be("delayed-item");
        queue.HasPendingItems.Should().BeFalse();
    }

    [Fact]
    public async Task RetryQueue_NotReadyPolling_ShouldNotDeadLetterScheduledItem()
    {
        // Arrange: DeadLetter overflow — no dead-letter sink — retry scheduled in the future
        var clock = new FakeClock { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var queue = new RetryQueue<string>(
            capacity: 2,
            overflowPolicy: RetryQueueOverflowPolicy.DeadLetter,
            deadLetterSink: null,
            clock: clock,
            pollTimeoutMs: 5000);

        var ctx = new ProcessingContext<string>("scheduled-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMinutes(30));

        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        queue.Count.Should().Be(1);

        // Act: multiple poll attempts while item is not ready
        for (int i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var item = await queue.TryGetNextAsync(cts.Token);
            item.Should().BeNull("item should not be ready yet");
        }

        // Assert: item is still preserved after repeated not-ready polls
        queue.Count.Should().Be(1, "repeated not-ready polls must preserve the scheduled item");
        queue.PendingCount.Should().Be(1, "repeated not-ready polls must keep pending state");

        // Advance clock and verify item becomes available
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var readyItem = await queue.TryGetNextAsync();
        readyItem.Should().NotBeNull();
        readyItem!.Value.Context.Payload.Should().Be("scheduled-item");
        queue.HasPendingItems.Should().BeFalse();
    }

    [Fact]
    public void RetryQueue_ApplyJitter_ShouldUseSymmetricRangeAroundBaseDelay()
    {
        var baseDelay = TimeSpan.FromSeconds(1);

        var minimum = RetryQueue<string>.ApplyJitter(baseDelay, 0);
        var midpoint = RetryQueue<string>.ApplyJitter(baseDelay, 50);
        var maximum = RetryQueue<string>.ApplyJitter(baseDelay, 100);

        minimum.Should().Be(TimeSpan.FromMilliseconds(750));
        midpoint.Should().Be(baseDelay);
        maximum.Should().Be(TimeSpan.FromMilliseconds(1250));
    }
}
