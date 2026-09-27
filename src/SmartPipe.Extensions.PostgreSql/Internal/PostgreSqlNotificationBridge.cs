#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Bounded, single-reader bridge between the synchronous Npgsql <c>Notification</c> callback and the enumerator of
/// one LISTEN run.
/// </summary>
/// <remarks>
/// <para>
/// The bridge owns every piece of mutable rendezvous state of a notification run: one bounded BCL channel, one
/// immutable first-cause slot and one source-private wait token. The provider callback can only call
/// <see cref="TryWrite"/>, which never blocks, never awaits and never runs user code.
/// </para>
/// <para>
/// The first terminal cause published through <see cref="TryRecordCause"/> wins for the rest of the run; every later
/// failure is secondary evidence and never replaces it. A full buffer is an explicit fault: the rejected
/// notification was not accepted, is not dropped silently and no drop mode is configured.
/// </para>
/// </remarks>
internal sealed class PostgreSqlNotificationBridge : IDisposable
{
    private readonly Channel<PostgreSqlNotification> _channel;
    private readonly CancellationTokenSource _waitCancellation = new();

    private Exception? _terminalCause;
    private int _stopRequested;
    private int _completed;
    private int _disposed;

    /// <summary>Creates the bridge with the configured bounded capacity.</summary>
    /// <param name="capacity">The exact buffer capacity; must be greater than zero.</param>
    internal PostgreSqlNotificationBridge(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<PostgreSqlNotification>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    /// <summary>Gets the source-private token that interrupts the outstanding provider wait.</summary>
    /// <remarks>
    /// Cancelling this token is pure control flow between the source and the bridge. It is never caller cancellation
    /// and must never be surfaced as one.
    /// </remarks>
    internal CancellationToken WaitToken => _waitCancellation.Token;

    /// <summary>Gets a value indicating whether the owning source asked to stop waiting and accepting.</summary>
    internal bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

    /// <summary>Gets a value indicating whether the bridge was completed and no longer accepts writes.</summary>
    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

    /// <summary>Gets the immutable first terminal cause, or <see langword="null"/> while the run is healthy.</summary>
    internal Exception? TerminalCause => Volatile.Read(ref _terminalCause);

    /// <summary>Publishes <paramref name="cause"/> as the terminal cause if no cause was published yet.</summary>
    /// <param name="cause">The failure to arbitrate.</param>
    /// <returns><see langword="true"/> when this call published the first cause; otherwise <see langword="false"/>.</returns>
    /// <remarks>The slot is immutable once set, so the winner is stable for the whole run.</remarks>
    internal bool TryRecordCause(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return Interlocked.CompareExchange(ref _terminalCause, cause, null) is null;
    }

    /// <summary>Accepts one notification without blocking.</summary>
    /// <param name="notification">The notification delivered by the provider callback.</param>
    /// <returns><see langword="true"/> when the notification was accepted; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// This is the only write path, because the Npgsql event callback cannot await. A rejected write means the
    /// bounded buffer was full: overflow becomes the terminal cause when no other cause was recorded first, the
    /// outstanding wait is interrupted so the enumerator observes it promptly, and every later delivery is rejected
    /// without being accepted.
    /// </remarks>
    internal bool TryWrite(PostgreSqlNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (Volatile.Read(ref _stopRequested) != 0 || Volatile.Read(ref _completed) != 0)
            return false;

        if (Volatile.Read(ref _terminalCause) is not null)
            return false;

        if (_channel.Writer.TryWrite(notification))
            return true;

        // The channel can also refuse a write because it was completed concurrently; that is not an overflow.
        if (Volatile.Read(ref _completed) != 0)
            return false;

        TryRecordCause(new InvalidOperationException(PostgreSqlErrorMessages.NotificationBufferOverflow));
        CancelWait();
        return false;
    }

    /// <summary>Reads one accepted notification without blocking.</summary>
    /// <param name="notification">The oldest accepted notification, in FIFO order.</param>
    /// <returns><see langword="true"/> when a notification was read; otherwise <see langword="false"/>.</returns>
    internal bool TryRead([MaybeNullWhen(false)] out PostgreSqlNotification notification) =>
        _channel.Reader.TryRead(out notification);

    /// <summary>Interrupts the outstanding provider wait without changing the accept state.</summary>
    /// <remarks>
    /// Used when a cause was already recorded elsewhere (a provider or connection fault) and the enumerator must not
    /// stay parked in <c>WaitAsync</c>. Cancelling here is source-private control flow.
    /// </remarks>
    internal void CancelWait()
    {
        try
        {
            _waitCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The bridge was already disposed, so no wait can be outstanding and no enumerator depends on it.
        }
    }

    /// <summary>Stops accepting notifications and interrupts the outstanding wait.</summary>
    internal void RequestStop()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        CancelWait();
    }

    /// <summary>Completes the bounded channel, stops accepting and releases the wait token source.</summary>
    /// <remarks>
    /// Already accepted notifications stay readable in the channel: completion ends the sequence rather than clearing
    /// it. This is a bridge-level guarantee only. The source stops its enumeration on the stop flag before it drains
    /// the channel, so on disposal a delivery that was accepted but not yet read is deliberately not surfaced — the
    /// source is a best-effort wake-up signal and disposal is not a drain. Calls after the first one are no-ops.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Interlocked.Exchange(ref _completed, 1);
        _channel.Writer.TryComplete();
        CancelWait();
        _waitCancellation.Dispose();
    }
}
