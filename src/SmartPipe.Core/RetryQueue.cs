#nullable enable

using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SmartPipe.Core;

/// <summary>Lock-free retry queue with cryptographically secure jitter.</summary>
/// <typeparam name="T">Type of payload.</typeparam>
/// <remarks>
/// Uses <see cref="BoundedChannelFullMode.DropOldest"/> when capacity is reached.
/// Applies cryptographic jitter to retry delays to prevent thundering herd.
/// </remarks>
public class RetryQueue<T>
{
    private readonly Channel<RetryItem<T>> _channel;
    private readonly ILogger<RetryQueue<T>>? _logger;
    private readonly ISink<object>? _deadLetterSink;
    private readonly IClock _clock;

    /// <summary>Gets the number of items waiting for retry.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Creates a new retry queue.</summary>
    /// <param name="capacity">Maximum queue capacity.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="deadLetterSink">Sink for exhausted retries.</param>
    /// <param name="clock">Optional clock for testability (defaults to TimeProviderClock()).</param>
    public RetryQueue(int capacity = 10000, ILogger<RetryQueue<T>>? logger = null, ISink<object>? deadLetterSink = null, IClock? clock = null)
    {
        _channel = Channel.CreateBounded<RetryItem<T>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _logger = logger;
        _deadLetterSink = deadLetterSink;
        _clock = clock ?? new TimeProviderClock();
    }

    /// <summary>Enqueues an item for retry with jittered delay.</summary>
    /// <param name="ctx">Processing context to retry.</param>
    /// <param name="policy">Retry policy to apply.</param>
    /// <param name="retryCount">Current retry attempt count.</param>
    /// <param name="error">Error that caused the retry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="retryBudget">Optional per-item retry budget (-1 = use policy default).</param>
    /// <returns>True if enqueued; false if budget exhausted.</returns>
    /// <remarks>Routes to dead letter sink when retry budget is exhausted.</remarks>
    public async ValueTask<bool> EnqueueAsync(
        ProcessingContext<T> ctx, RetryPolicy policy,
        int retryCount, SmartPipeError error, CancellationToken ct = default, int? retryBudget = null)
    {
        var effectiveBudget = retryBudget ?? policy.MaxRetries;
        if (retryCount >= effectiveBudget)
        {
            _logger?.LogWarning("Retry budget exhausted for item {TraceId}, retry count {RetryCount} >= budget {Budget}", ctx.TraceId, retryCount, effectiveBudget);
            if (_deadLetterSink != null)
            {
                var result = ProcessingResult<object>.Failure(error, ctx.TraceId);
                await _deadLetterSink.WriteAsync(result, ct).ConfigureAwait(false);
            }
            return false;
        }

        var baseDelay = policy.GetDelay(retryCount + 1);
        var jitteredDelay = ApplyJitter(baseDelay);
        var retryAt = _clock.UtcNow + jitteredDelay;
        var item = new RetryItem<T>(ctx, policy, retryCount + 1, error, retryAt, retryBudget ?? -1);
        await _channel.Writer.WriteAsync(item, ct);
        return true;
    }

    /// <summary>Tries to get the next retry item that is ready.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Retry item if ready; null if none available or not yet time.</returns>
    /// <remarks>Items not yet ready are re-queued.</remarks>
    public async ValueTask<RetryItem<T>?> TryGetNextAsync(CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            cts.CancelAfter(50);

            if (_channel.Reader.Count == 0)
            {
                await _channel.Reader.WaitToReadAsync(cts.Token);
                return null;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            cts.Dispose();
        }

        if (_channel.Reader.TryRead(out var item))
        {
            if (item.RetryAt <= _clock.UtcNow) return item;
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

/// <summary>Represents an item in the retry queue.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Context">Processing context to retry.</param>
/// <param name="Policy">Retry policy to apply.</param>
/// <param name="RetryCount">Current retry attempt count.</param>
/// <param name="Error">Error that caused the retry.</param>
/// <param name="RetryAt">When to execute the retry.</param>
/// <param name="RetryBudget">Optional per-item retry budget (-1 = use policy default).</param>
public readonly record struct RetryItem<T>(
    ProcessingContext<T> Context,
    RetryPolicy Policy,
    int RetryCount,
    SmartPipeError Error,
    DateTime RetryAt,
    int RetryBudget = -1
)
{
    /// <summary>Per-item retry budget. Defaults to Policy.MaxRetries if not explicitly set.</summary>
    public int EffectiveRetryBudget => RetryBudget == -1 ? Policy.MaxRetries : RetryBudget;
}
