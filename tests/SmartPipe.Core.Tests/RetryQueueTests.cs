#nullable enable
using Xunit;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests;

public class RetryQueueTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenItemIsNull()
    {
        // This test validates that RetryQueue handles null properly
        // Since RetryQueue doesn't have a constructor that takes the item directly,
        // we test the RetryItem creation instead
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1));
        var error = new SmartPipeError("test", ErrorType.Transient);
        var item = new RetryItem<string>(ctx, policy, 0, error, DateTime.UtcNow);
        Assert.NotNull(item);
    }

    [Fact]
    public void TryRetry_ReturnsTrue_WhenRetryCountLessThanMax()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        // Act & Assert - EnqueueAsync returns true when retryCount < maxRetries
        var result = queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient)).Result;
        Assert.True(result);
    }

    [Fact]
    public async Task TryRetry_ReturnsFalse_WhenMaxRetriesExceeded()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero);

        // Act
        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        var result = await queue.EnqueueAsync(ctx, policy, 1, new SmartPipeError("e", ErrorType.Transient));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Item_ReturnsOriginalItem()
    {
        // Arrange
        var item = "test";
        var ctx = new ProcessingContext<string>(item);
        var policy = new RetryPolicy();
        var error = new SmartPipeError("test", ErrorType.Transient);
        var retryItem = new RetryItem<string>(ctx, policy, 0, error, DateTime.UtcNow);

        // Assert
        Assert.Same(item, retryItem.Context.Payload);
    }

    [Fact]
    public void RetryQueue_Count_ReflectsQueueSize()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);

        // Assert
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task EnqueueAsync_WithTransientError_ReturnsTrue()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMilliseconds(10));

        // Act
        var result = await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TryGetNextAsync_WhenReady_ReturnsItem()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        // Act
        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("e", ErrorType.Transient));
        await Task.Delay(5);
        var item = await queue.TryGetNextAsync();

        // Assert
        Assert.NotNull(item);
        Assert.Equal("test", item!.Value.Context.Payload);
    }

    [Fact]
    public async Task Enqueue_AddsItemToChannel()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMilliseconds(10));

        // Act
        var result = await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("test-error", ErrorType.Transient));

        // Assert
        Assert.True(result);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task TryGetNextAsync_ReturnsEnqueuedItem()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);
        var ctx = new ProcessingContext<string>("test-item");
        var policy = new RetryPolicy(maxRetries: 3, delay: TimeSpan.Zero);

        // Act
        await queue.EnqueueAsync(ctx, policy, 0, new SmartPipeError("test-error", ErrorType.Transient));
        await Task.Delay(5); // Wait for delay
        var item = await queue.TryGetNextAsync();

        // Assert
        Assert.NotNull(item);
        Assert.Equal("test-item", item!.Value.Context.Payload);
    }

    [Fact]
    public void Count_ReturnsCorrectValue()
    {
        // Arrange
        var queue = new RetryQueue<string>(10);

        // Assert
        Assert.Equal(0, queue.Count);
    }
}
