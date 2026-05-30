#nullable enable

using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SmartPipe.Core;

/// <summary>Lock-free retry queue with cryptographically secure jitter.</summary>
/// <typeparam name="T">Type of payload.</typeparam>
/// <remarks>
/// Applies cryptographic jitter to retry delays to prevent thundering herd.
/// Uses <see cref="RetryQueueOverflowPolicy"/> to control overflow behavior.
/// </remarks>
public class RetryQueue<T>
{
    private readonly Channel<RetryItem<T>> _channel;
    private readonly ILogger<RetryQueue<T>>? _logger;
    private readonly ISink<object>? _deadLetterSink;
    private readonly IClock _clock;
    private readonly RetryQueueOverflowPolicy _overflowPolicy;
    private readonly int _capacity;

    /// <summary>Default timeout for polling when no items are available. Configurable via constructor.</summary>
    private readonly int _pollTimeoutMs;

    /// <summary>Gets the number of items waiting for retry.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Creates a new retry queue.</summary>
    /// <param name="capacity">Maximum queue capacity.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="deadLetterSink">Sink for exhausted retries.</param>
    /// <param name="clock">Optional clock for testability (defaults to TimeProviderClock()).</param>
    /// <param name="overflowPolicy">Policy for handling queue overflow (defaults to Wait).</param>
    /// <param name="pollTimeoutMs">Timeout in milliseconds for polling when no items are ready. Default 100ms.</param>
    public RetryQueue(
        int capacity = 10000,
        ILogger<RetryQueue<T>>? logger = null,
        ISink<object>? deadLetterSink = null,
        IClock? clock = null,
        RetryQueueOverflowPolicy overflowPolicy = RetryQueueOverflowPolicy.Wait,
        int pollTimeoutMs = 100
    )
    {
        _overflowPolicy = overflowPolicy;
        _capacity = capacity;
        var fullMode = overflowPolicy switch
        {
            RetryQueueOverflowPolicy.Wait => BoundedChannelFullMode.Wait,
            RetryQueueOverflowPolicy.DropOldest => BoundedChannelFullMode.DropOldest,
            RetryQueueOverflowPolicy.DropNewest => BoundedChannelFullMode.DropWrite,
            RetryQueueOverflowPolicy.FailFast => BoundedChannelFullMode.Wait,
            RetryQueueOverflowPolicy.DeadLetter => BoundedChannelFullMode.Wait,
            _ => BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<RetryItem<T>>(
            new BoundedChannelOptions(capacity) { FullMode = fullMode }
        );
        _logger = logger;
        _deadLetterSink = deadLetterSink;
        _clock = clock ?? new TimeProviderClock();
        _pollTimeoutMs = pollTimeoutMs;
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
        ProcessingContext<T> ctx,
        RetryPolicy policy,
        int retryCount,
        SmartPipeError error,
        CancellationToken ct = default,
        int? retryBudget = null
    )
    {
        var effectiveBudget = retryBudget ?? policy.MaxRetries;
        if (retryCount >= effectiveBudget)
        {
            _logger?.LogWarning(
                "Retry budget exhausted for item {TraceId}, retry count {RetryCount} >= budget {Budget}",
                ctx.TraceId,
                retryCount,
                effectiveBudget
            );
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

        switch (_overflowPolicy)
        {
            case RetryQueueOverflowPolicy.Wait:
                await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
                return true;

            case RetryQueueOverflowPolicy.FailFast:
                if (Count >= _capacity)
                    return false;
                _channel.Writer.TryWrite(item);
                return true;

            case RetryQueueOverflowPolicy.DeadLetter:
                if (Count >= _capacity)
                {
                    if (_deadLetterSink != null)
                    {
                        var deadResult = ProcessingResult<object>.Failure(error, ctx.TraceId);
                        await _deadLetterSink.WriteAsync(deadResult, ct).ConfigureAwait(false);
                    }
                    return false;
                }
                _channel.Writer.TryWrite(item);
                return true;

            case RetryQueueOverflowPolicy.DropNewest:
                _channel.Writer.TryWrite(item); // Will drop via BoundedChannelFullMode.DropWrite
                return true; // Always returns true — dropped items are silently lost (documented as lossy)

            case RetryQueueOverflowPolicy.DropOldest:
                _channel.Writer.TryWrite(item); // Will drop via BoundedChannelFullMode.DropOldest
                return true; // Always returns true

            default:
                _channel.Writer.TryWrite(item);
                return true;
        }
    }

    /// <summary>Tries to get the next retry item that is ready.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Retry item if ready; null if none available or not yet time.</returns>
    /// <remarks>
    /// Uses WaitToReadAsync with a single CancellationTokenSource per call, with configurable timeout.
    /// Items not yet ready are re-queued.
    /// </remarks>
    public async ValueTask<RetryItem<T>?> TryGetNextAsync(CancellationToken ct = default)
    {
        using var cts = new CancellationTokenSource(_pollTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        try
        {
            // Always wait — removes the race condition between Count check and WaitToReadAsync
            await _channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            return null;
        }

        if (_channel.Reader.TryRead(out var item))
        {
            if (item.RetryAt <= _clock.UtcNow)
                return item;
            // Not ready yet — attempt re-queue with policy awareness
            if (_overflowPolicy == RetryQueueOverflowPolicy.FailFast || _overflowPolicy == RetryQueueOverflowPolicy.DeadLetter)
            {
                // Cannot re-queue — treat as terminal failure
                if (_deadLetterSink != null && _overflowPolicy == RetryQueueOverflowPolicy.DeadLetter)
                {
                    var result = ProcessingResult<object>.Failure(item.Error, item.Context.TraceId);
                    await _deadLetterSink.WriteAsync(result, ct).ConfigureAwait(false);
                }
                return null;
            }
            _channel.Writer.TryWrite(item); // Re-queue
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
