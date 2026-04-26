using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Delayed retry queue with jitter, time budgeting, and overflow protection.
/// Based on modern resilience patterns (Polly v8, exponential backoff + jitter).</summary>
/// <typeparam name="T">Type of payload being retried.</typeparam>
public class RetryQueue<T>
{
    private readonly Channel<RetryItem<T>> _channel;
    private readonly int _maxQueueSize;
    private readonly Random _jitterRng = new();

    /// <summary>Current queue size.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Create a retry queue with bounded capacity.</summary>
    /// <param name="capacity">Maximum queue size. Default: 10000.</param>
    public RetryQueue(int capacity = 10000)
    {
        _maxQueueSize = capacity;
        _channel = Channel.CreateBounded<RetryItem<T>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>Enqueue an item for retry with jittered delay.</summary>
    /// <param name="ctx">Original processing context.</param>
    /// <param name="policy">Retry policy to apply.</param>
    /// <param name="retryCount">Current retry attempt number (0-based).</param>
    /// <param name="error">Error that triggered the retry.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<bool> EnqueueAsync(
        ProcessingContext<T> ctx, RetryPolicy policy,
        int retryCount, SmartPipeError error, CancellationToken ct = default)
    {
        if (retryCount >= policy.MaxRetries)
            return false;

        var baseDelay = policy.GetDelay(retryCount + 1);
        var jitteredDelay = ApplyJitter(baseDelay);
        var retryAt = DateTime.UtcNow + jitteredDelay;

        var item = new RetryItem<T>(ctx, policy, retryCount + 1, error, retryAt);
        await _channel.Writer.WriteAsync(item, ct);
        return true;
    }

    /// <summary>Try to get the next ready item (non-blocking).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next ready item, or null if none are ready.</returns>
    public async ValueTask<RetryItem<T>?> TryGetNextAsync(CancellationToken ct = default)
    {
        if (_channel.Reader.Count == 0)
        {
            await Task.Delay(50, ct);
            return null;
        }

        if (_channel.Reader.TryRead(out var item))
        {
            if (item.RetryAt <= DateTime.UtcNow)
                return item;
            // Not ready yet — put it back
            await _channel.Writer.WriteAsync(item, ct);
        }
        return null;
    }

    private TimeSpan ApplyJitter(TimeSpan baseDelay)
    {
        double jitterFactor = 0.75 + (_jitterRng.NextDouble() * 0.25);
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * jitterFactor));
    }
}

/// <summary>An item in the retry queue.</summary>
/// <typeparam name="T">Type of payload.</typeparam>
/// <param name="Context">Original processing context.</param>
/// <param name="Policy">Retry policy to apply.</param>
/// <param name="RetryCount">Current retry attempt number.</param>
/// <param name="Error">Error that triggered the retry.</param>
/// <param name="RetryAt">UTC time when the retry should be attempted.</param>
public readonly record struct RetryItem<T>(
    ProcessingContext<T> Context,
    RetryPolicy Policy,
    int RetryCount,
    SmartPipeError Error,
    DateTime RetryAt
);
