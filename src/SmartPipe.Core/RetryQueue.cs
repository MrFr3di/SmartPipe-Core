using System.Security.Cryptography;
using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Lock-free retry queue with cryptographically secure jitter.</summary>
/// <typeparam name="T">Type of payload.</typeparam>
public class RetryQueue<T>
{
    private readonly Channel<RetryItem<T>> _channel;

    public int Count => _channel.Reader.Count;

    public RetryQueue(int capacity = 10000)
    {
        _channel = Channel.CreateBounded<RetryItem<T>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public async ValueTask<bool> EnqueueAsync(
        ProcessingContext<T> ctx, RetryPolicy policy,
        int retryCount, SmartPipeError error, CancellationToken ct = default)
    {
        if (retryCount >= policy.MaxRetries) return false;

        var baseDelay = policy.GetDelay(retryCount + 1);
        var jitteredDelay = ApplyJitter(baseDelay);
        var retryAt = DateTime.UtcNow + jitteredDelay;
        var item = new RetryItem<T>(ctx, policy, retryCount + 1, error, retryAt);
        await _channel.Writer.WriteAsync(item, ct);
        return true;
    }

    public async ValueTask<RetryItem<T>?> TryGetNextAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(50);

            if (_channel.Reader.Count == 0)
            {
                await _channel.Reader.WaitToReadAsync(cts.Token);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (_channel.Reader.TryRead(out var item))
        {
            if (item.RetryAt <= DateTime.UtcNow) return item;
            await _channel.Writer.WriteAsync(item, ct);
        }
        return null;
    }

    private static TimeSpan ApplyJitter(TimeSpan baseDelay)
    {
        double jitterFactor = 0.75 + (RandomNumberGenerator.GetInt32(0, 101) / 100.0 * 0.25);
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * jitterFactor));
    }
}

public readonly record struct RetryItem<T>(
    ProcessingContext<T> Context,
    RetryPolicy Policy,
    int RetryCount,
    SmartPipeError Error,
    DateTime RetryAt
);
