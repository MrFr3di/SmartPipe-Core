#nullable enable

using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartPipe.Core;

/// <summary>Thread-safe retry queue with cryptographically secure jitter.</summary>
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
    private readonly ConcurrentQueue<RetryItem<T>> _preservedNotReadyItems = new();
    private int _pendingCount;

    /// <summary>Default timeout for polling when no items are available. Configurable via constructor.</summary>
    private readonly int _pollTimeoutMs;

    /// <summary>Gets the number of items waiting for retry.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Gets the number of accepted retry items that have not been returned or removed permanently.</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>Gets a value indicating whether the retry queue has accepted retry items still pending.</summary>
    public bool HasPendingItems => PendingCount > 0;

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
                return await EnqueueWaitAsync(item, ct).ConfigureAwait(false);

            case RetryQueueOverflowPolicy.FailFast:
                return TryEnqueueNonLossy(item);

            case RetryQueueOverflowPolicy.DeadLetter:
                if (TryEnqueueNonLossy(item))
                    return true;
                await WriteDeadLetterAsync(ctx, error, ct).ConfigureAwait(false);
                return false;

            case RetryQueueOverflowPolicy.DropNewest:
                EnqueueLossy(item, incrementWhenFull: false); // DropWrite may drop incoming item.
                return true; // Lossy by design.

            case RetryQueueOverflowPolicy.DropOldest:
                EnqueueLossy(item, incrementWhenFull: false); // DropOldest replaces an existing item when full.
                return true; // Lossy by design.

            default:
                return TryEnqueueNonLossy(item);
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
        if (_preservedNotReadyItems.TryDequeue(out var preservedItem))
            return await HandleDequeuedItemAsync(preservedItem, ct).ConfigureAwait(false);

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
            return await HandleDequeuedItemAsync(item, ct).ConfigureAwait(false);

        return null;
    }

    private async ValueTask<RetryItem<T>?> HandleDequeuedItemAsync(
        RetryItem<T> item,
        CancellationToken ct
    )
    {
        if (item.RetryAt <= _clock.UtcNow)
        {
            DecrementPending();
            return item;
        }

        // Not-ready items remain logically pending while they are requeued.
        // Overflow policy does not apply to internal scheduler preservation.
        if (!await RequeueNotReadyItemAsync(item, ct).ConfigureAwait(false))
            DecrementPending();

        return null;
    }

    private async ValueTask<bool> EnqueueWaitAsync(RetryItem<T> item, CancellationToken ct)
    {
        while (await _channel.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
        {
            IncrementPending();
            if (_channel.Writer.TryWrite(item))
                return true;
            DecrementPending();
        }
        return false;
    }

    private bool TryEnqueueNonLossy(RetryItem<T> item)
    {
        IncrementPending();
        if (_channel.Writer.TryWrite(item))
            return true;
        DecrementPending();
        return false;
    }

    private void EnqueueLossy(RetryItem<T> item, bool incrementWhenFull)
    {
        var wasFull = PendingCount >= _capacity;
        if (!wasFull || incrementWhenFull)
            IncrementPending();

        if (_channel.Writer.TryWrite(item))
            return;

        if (!wasFull || incrementWhenFull)
            DecrementPending();
    }

    private ValueTask<bool> RequeueNotReadyItemAsync(
        RetryItem<T> item,
        CancellationToken ct
    )
    {
        if (_channel.Writer.TryWrite(item))
            return ValueTask.FromResult(true);

        if (ct.IsCancellationRequested)
        {
            _logger?.LogDebug("Retry queue requeue cancelled for item {TraceId}", item.Context.TraceId);
            return ValueTask.FromResult(false);
        }

        // If concurrent enqueues fill the bounded channel between TryRead and TryWrite,
        // preserve the already-accepted delayed item outside the channel. Waiting for
        // channel capacity here can deadlock because this retry loop is also the reader
        // that would free the capacity.
        _preservedNotReadyItems.Enqueue(item);
        return ValueTask.FromResult(true);
    }

    private async ValueTask WriteDeadLetterAsync(
        ProcessingContext<T> ctx,
        SmartPipeError error,
        CancellationToken ct
    )
    {
        if (_deadLetterSink == null)
            return;

        var deadResult = ProcessingResult<object>.Failure(error, ctx.TraceId);
        await _deadLetterSink.WriteAsync(deadResult, ct).ConfigureAwait(false);
    }

    private void IncrementPending() => Interlocked.Increment(ref _pendingCount);

    private void DecrementPending()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _pendingCount);
            if (current == 0)
                return;
        } while (Interlocked.CompareExchange(ref _pendingCount, current - 1, current) != current);
    }

    private static TimeSpan ApplyJitter(TimeSpan baseDelay)
    {
        return ApplyJitter(baseDelay, RandomNumberGenerator.GetInt32(0, 101));
    }

    internal static TimeSpan ApplyJitter(TimeSpan baseDelay, int jitterBucket)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jitterBucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterBucket, 100);
        double jitterFactor = 0.75 + (jitterBucket / 100.0 * 0.5);
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
