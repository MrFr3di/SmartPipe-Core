using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>
/// Server-free first-cause arbitration tests for the LISTEN notification bridge.
/// </summary>
/// <remarks>
/// <para>
/// The bridge is the only mutable rendezvous state of a notification run, so the arbitration contract can be forced
/// deterministically here without a server: the bindings under test are the same ones the source uses, namely a
/// cancellation registration that publishes an <see cref="OperationCanceledException"/>, the overflow publication of
/// <see cref="PostgreSqlNotificationBridge.TryWrite"/> and the connection-fault publication of the wait path.
/// </para>
/// <para>
/// Ordering is forced by completion-signalled gates and synchronous call ordering, never by elapsed time. The one
/// genuinely concurrent case publishes through a <see cref="TaskCompletionSource"/> gate and asserts the invariant that
/// exactly one publisher wins.
/// </para>
/// </remarks>
public sealed class PostgreSqlNotificationBridgeTests
{
    private static readonly PostgreSqlNotification First = new("orders", "first", 101);
    private static readonly PostgreSqlNotification Second = new("orders", "second", 102);
    private static readonly PostgreSqlNotification Third = new("orders", "third", 103);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveCapacity_IsRejected(int capacity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlNotificationBridge(capacity));

        Assert.Equal("capacity", exception.ParamName);
    }

    [Fact]
    public void TryWrite_AcceptsExactlyCapacityNotificationsInFifoOrder()
    {
        using var bridge = new PostgreSqlNotificationBridge(3);

        Assert.True(bridge.TryWrite(First));
        Assert.True(bridge.TryWrite(Second));
        Assert.True(bridge.TryWrite(Third));

        Assert.True(bridge.TryRead(out var first));
        Assert.True(bridge.TryRead(out var second));
        Assert.True(bridge.TryRead(out var third));

        Assert.Equal(First, first);
        Assert.Equal(Second, second);
        Assert.Equal(Third, third);
        Assert.False(bridge.TryRead(out _));
        Assert.Null(bridge.TerminalCause);
    }

    [Fact]
    public void TryWrite_OverflowPublishesTheOverflowCauseAndInterruptsTheWait()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);
        Assert.True(bridge.TryWrite(First));

        Assert.False(bridge.TryWrite(Second));

        var cause = Assert.IsType<InvalidOperationException>(bridge.TerminalCause);
        Assert.Equal(PostgreSqlErrorMessages.NotificationBufferOverflow, cause.Message);
        Assert.True(bridge.WaitToken.IsCancellationRequested);
    }

    [Fact]
    public void OverflowThenCallerCancellation_KeepsOverflowAsThePrimaryCause()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException? publishedCancellation = null;
        using var registration = cancellation.Token.Register(() =>
        {
            publishedCancellation = new OperationCanceledException(cancellation.Token);
            bridge.TryRecordCause(publishedCancellation);
        });

        // Overflow wins the arbitration first.
        Assert.True(bridge.TryWrite(First));
        Assert.False(bridge.TryWrite(Second));
        var overflow = bridge.TerminalCause;
        Assert.NotNull(overflow);

        // The caller cancellation that follows is secondary evidence and never replaces the stored cause.
        cancellation.Cancel();

        Assert.NotNull(publishedCancellation);
        Assert.Same(overflow, bridge.TerminalCause);
        Assert.NotSame(publishedCancellation, bridge.TerminalCause);

        // Accepted notifications stay readable; nothing published after the terminal cause is accepted.
        Assert.False(bridge.TryWrite(Third));
        Assert.True(bridge.TryRead(out var accepted));
        Assert.Equal(First, accepted);
        Assert.False(bridge.TryRead(out _));
    }

    [Fact]
    public void CallerCancellationThenOverflow_KeepsCancellationAsThePrimaryCause()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException? publishedCancellation = null;
        using var registration = cancellation.Token.Register(() =>
        {
            publishedCancellation = new OperationCanceledException(cancellation.Token);
            bridge.TryRecordCause(publishedCancellation);
        });

        Assert.True(bridge.TryWrite(First));

        // Caller cancellation wins the arbitration first.
        cancellation.Cancel();

        Assert.NotNull(publishedCancellation);
        Assert.Same(publishedCancellation, bridge.TerminalCause);

        // The write that would have overflowed is rejected without being accepted and without becoming the cause.
        Assert.False(bridge.TryWrite(Second));
        Assert.Same(publishedCancellation, bridge.TerminalCause);
        Assert.NotEqual(PostgreSqlErrorMessages.NotificationBufferOverflow, bridge.TerminalCause!.Message);

        Assert.True(bridge.TryRead(out var accepted));
        Assert.Equal(First, accepted);
        Assert.False(bridge.TryRead(out _));
    }

    [Fact]
    public void OverflowThenConnectionFault_KeepsOverflowAsThePrimaryCause()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);
        Assert.True(bridge.TryWrite(First));
        Assert.False(bridge.TryWrite(Second));
        var overflow = bridge.TerminalCause;
        Assert.NotNull(overflow);

        var connectionFault = ConnectionLostFailure();

        Assert.False(bridge.TryRecordCause(connectionFault));
        Assert.Same(overflow, bridge.TerminalCause);
        Assert.False(bridge.TryWrite(Third));
        Assert.Equal(PostgreSqlErrorMessages.NotificationBufferOverflow, bridge.TerminalCause!.Message);
    }

    [Fact]
    public void ConnectionFaultThenOverflow_KeepsTheConnectionFaultAsThePrimaryCause()
    {
        using var bridge = new PostgreSqlNotificationBridge(2);
        var connectionFault = ConnectionLostFailure();

        // The wait path records the provider fault first.
        Assert.True(bridge.TryRecordCause(connectionFault));

        Assert.False(bridge.TryWrite(First));
        Assert.Same(connectionFault, bridge.TerminalCause);
        Assert.NotEqual(PostgreSqlErrorMessages.NotificationBufferOverflow, bridge.TerminalCause!.Message);

        // CancelWait was already requested by the wait path; the rejected notifications were never accepted.
        bridge.CancelWait();
        Assert.True(bridge.WaitToken.IsCancellationRequested);
        Assert.False(bridge.TryRead(out _));
    }

    [Fact]
    public async Task ConcurrentCausePublication_ExactlyOneCauseWinsTheSlot()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);
        var overflow = new InvalidOperationException(PostgreSqlErrorMessages.NotificationBufferOverflow);
        var fault = ConnectionLostFailure();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var overflowPublisher = Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false);
            return bridge.TryRecordCause(overflow);
        });
        var faultPublisher = Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false);
            return bridge.TryRecordCause(fault);
        });

        gate.SetResult();
        var published = await Task.WhenAll(overflowPublisher, faultPublisher);

        Assert.Single(published, static winner => winner);
        var winner = published[0] ? overflow : fault;
        var loser = published[0] ? fault : overflow;

        Assert.Same(winner, bridge.TerminalCause);
        Assert.NotSame(loser, bridge.TerminalCause);
    }

    [Fact]
    public void RequestStop_StopsAcceptingWithoutRecordingAFailure()
    {
        using var bridge = new PostgreSqlNotificationBridge(2);

        bridge.RequestStop();

        Assert.True(bridge.StopRequested);
        Assert.Null(bridge.TerminalCause);
        Assert.False(bridge.TryWrite(First));
        Assert.False(bridge.TryRead(out _));
        Assert.True(bridge.WaitToken.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_CompletesTheBridgeAndKeepsAcceptedNotificationsReadable()
    {
        var bridge = new PostgreSqlNotificationBridge(2);
        Assert.True(bridge.TryWrite(First));

        bridge.Dispose();

        Assert.True(bridge.IsCompleted);
        Assert.False(bridge.TryWrite(Second));
        Assert.True(bridge.TryRead(out var accepted));
        Assert.Equal(First, accepted);
        Assert.False(bridge.TryRead(out _));

        // Disposal is idempotent.
        bridge.Dispose();
        Assert.True(bridge.IsCompleted);
    }

    [Fact]
    public void TryWrite_NullNotification_IsRejected()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);

        var exception = Assert.Throws<ArgumentNullException>(() => bridge.TryWrite(null!));

        Assert.Equal("notification", exception.ParamName);
    }

    [Fact]
    public void TryRecordCause_NullCause_IsRejected()
    {
        using var bridge = new PostgreSqlNotificationBridge(1);

        var exception = Assert.Throws<ArgumentNullException>(() => bridge.TryRecordCause(null!));

        Assert.Equal("cause", exception.ParamName);
    }

    private static InvalidOperationException ConnectionLostFailure() =>
        new(
            PostgreSqlErrorMessages.NotificationConnectionLost,
            new InvalidOperationException("the dedicated LISTEN connection was lost"));
}
