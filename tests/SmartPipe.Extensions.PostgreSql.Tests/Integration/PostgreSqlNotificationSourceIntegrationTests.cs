using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Real-server scenarios for the <c>LISTEN</c>/<c>NOTIFY</c> notification source.
/// </summary>
/// <remarks>
/// Every scenario owns a unique channel, a dedicated listener data source with its own application name and its own
/// source instance, so the class is parallel-safe with the other integration classes and a listen backend can be
/// identified (or terminated) without touching any other connection.
/// </remarks>
public sealed class PostgreSqlNotificationSourceIntegrationTests(PostgreSqlIntegrationDatabase database)
    : IClassFixture<PostgreSqlIntegrationDatabase>
{
    [Fact]
    public async Task Notify_SingleChannel_DeliversChannelPayloadAndNotifyingBackendPid()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        var notifyingPid = await database.NotifyAsync(channel, "payload-1", ct);
        var notification = await NextAsync(enumerator, ct);

        Assert.Equal(channel, notification.Channel);
        Assert.Equal("payload-1", notification.Payload);
        Assert.Equal(notifyingPid, notification.BackendProcessId);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_MultipleChannels_DeliverEveryChannelIndependently()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var first = database.NewChannel();
        var second = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [first, second]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await database.NotifyAsync(first, "on-first", ct);
        await database.NotifyAsync(second, "on-second", ct);

        var firstNotification = await NextAsync(enumerator, ct);
        var secondNotification = await NextAsync(enumerator, ct);

        Assert.Equal(first, firstNotification.Channel);
        Assert.Equal("on-first", firstNotification.Payload);
        Assert.Equal(second, secondNotification.Channel);
        Assert.Equal("on-second", secondNotification.Payload);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_DuplicateChannel_IsRejectedByTheFactory()
    {
        database.RequireServer();
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        var failure = Assert.Throws<ArgumentException>(() => PostgreSqlPipelineComponents.NotificationSource(
            listen,
            [channel, channel],
            new PostgreSqlNotificationSourceOptions()));

        Assert.Contains(PostgreSqlErrorMessages.ChannelDuplicate, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notify_CommittedNotificationInsideATransaction_IsDelivered()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await database.NotifyInTransactionAsync([(channel, "committed")], commit: true, ct);
        var notification = await NextAsync(enumerator, ct);

        Assert.Equal("committed", notification.Payload);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_RolledBackNotification_IsNotDelivered()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await database.NotifyInTransactionAsync([(channel, "rolled-back")], commit: false, ct);

        // The sentinel is sent after the rollback, so it is the only notification the server can deliver: receiving it
        // first proves the rolled-back NOTIFY was never queued.
        await database.NotifyAsync(channel, "sentinel", ct);
        var notification = await NextAsync(enumerator, ct);

        Assert.Equal("sentinel", notification.Payload);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_IdenticalNotificationsInOneTransaction_AreCoalesced()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        // Documented behaviour of PostgreSQL itself, observed here: identical (channel, payload) NOTIFY statements
        // inside one transaction are folded into a single delivery at COMMIT. This package neither adds nor removes
        // that folding, and never promises one delivery per NOTIFY statement.
        await database.NotifyInTransactionAsync(
            [(channel, "folded"), (channel, "folded")],
            commit: true,
            ct);

        await database.NotifyAsync(channel, "sentinel", ct);

        var first = await NextAsync(enumerator, ct);
        Assert.Equal("folded", first.Payload);

        // The next delivery is the sentinel, which proves the duplicate was folded rather than queued behind it.
        var second = await NextAsync(enumerator, ct);
        Assert.Equal("sentinel", second.Payload);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_DistinctNotificationsInOneTransaction_AreDeliveredInOrder()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await database.NotifyInTransactionAsync(
            [(channel, "first"), (channel, "second"), (channel, "third")],
            commit: true,
            ct);

        var delivered = new List<string>();
        for (var index = 0; index < 3; index++)
            delivered.Add((await NextAsync(enumerator, ct)).Payload);

        Assert.Equal(new[] { "first", "second", "third" }, delivered);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_ControlledCommitOrder_IsObservedByTheListener()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await using var firstSender = await database.OpenConnectionAsync(ct);
        await using var secondSender = await database.OpenConnectionAsync(ct);
        await using var firstTransaction = await firstSender.BeginTransactionAsync(ct);
        await using var secondTransaction = await secondSender.BeginTransactionAsync(ct);

        await PostgreSqlIntegrationDatabase.ExecuteOnConnectionAsync(
            firstSender,
            "SELECT pg_notify(@channel, @payload)",
            ct,
            ("channel", channel),
            ("payload", "committed-first-transaction"));

        await PostgreSqlIntegrationDatabase.ExecuteOnConnectionAsync(
            secondSender,
            "SELECT pg_notify(@channel, @payload)",
            ct,
            ("channel", channel),
            ("payload", "committed-second-transaction"));

        // The second transaction commits first, so its notification must arrive first.
        await secondTransaction.CommitAsync(ct);
        await firstTransaction.CommitAsync(ct);
        await database.NotifyAsync(channel, "sentinel", ct);

        var delivered = new List<string>();
        for (var index = 0; index < 3; index++)
            delivered.Add((await NextAsync(enumerator, ct)).Payload);

        Assert.Equal(
            new[] { "committed-second-transaction", "committed-first-transaction", "sentinel" },
            delivered);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_PayloadIsPreservedByteForByte()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        const string payload = "  Елена 漢字 \t tab, \"double\", 'single', \\backslash, \r\n newline, trailing  ";
        await database.NotifyAsync(channel, payload, ct);
        var notification = await NextAsync(enumerator, ct);

        Assert.True(
            string.Equals(payload, notification.Payload, StringComparison.Ordinal),
            "The notification payload was not preserved byte-for-byte.");

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_QuotedAndSpecialChannelIdentifier_Works()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;

        // A space and a double quote in one identifier: only correct provider-side quoting can register this channel.
        var channel = $"say \"hi\" now {Guid.NewGuid():N}";
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await database.NotifyAsync(channel, "special", ct);
        var notification = await NextAsync(enumerator, ct);

        Assert.True(
            string.Equals(channel, notification.Channel, StringComparison.Ordinal),
            "The channel identifier was not reported exactly as registered.");
        Assert.Equal("special", notification.Payload);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_PayloadIsNeverLogged()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);
        var loggerFactory = new RecordingLoggerFactory();
        const string payload = "SECRET-PAYLOAD-9f3c1d";

        await using var source = await CreateSourceAsync(listen, [channel], loggerFactory: loggerFactory);
        await source.InitializeAsync(ct);

        // The capture is proved before its absence is trusted.
        loggerFactory.CreateLogger("scenario-probe").LogInformation("probe-marker-{Marker}", "captured");
        Assert.True(loggerFactory.Contains("probe-marker"), "The recording logger factory captured nothing.");

        // The enumeration carries the same token that is cancelled below, and every wait stays bounded.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var enumerator = Open(source, cancellation.Token);
        await database.NotifyAsync(channel, payload, ct);
        var notification = await NextAsync(enumerator, ct);
        Assert.Equal(payload, notification.Payload);

        // Force the failure path as well, so the component definitely logs something about this run.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await enumerator.MoveNextAsync().AsTask().WaitAsync(PostgreSqlTestGuard.Short, ct);
        });

        await enumerator.DisposeAsync();
        await source.DisposeAsync();

        Assert.False(
            loggerFactory.Contains(payload),
            $"The notification payload reached the logs: {string.Join(" | ", loggerFactory.Entries)}");
        Assert.False(
            loggerFactory.Contains(channel),
            $"The channel identifier reached the logs: {string.Join(" | ", loggerFactory.Entries)}");
    }

    [Fact]
    public async Task Notify_CallerCancellation_InterruptsTheWait()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var enumerator = Open(source, cancellation.Token);

        await database.NotifyAsync(channel, "wake", ct);
        Assert.Equal("wake", (await NextAsync(enumerator, ct)).Payload);

        // The next wait is interrupted rather than waiting for a notification that never comes.
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await enumerator.MoveNextAsync().AsTask().WaitAsync(PostgreSqlTestGuard.Short, ct);
        });

        Assert.NotNull(failure);
        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_DisposeWhileWaiting_EndsThePendingEnumeration()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out var applicationName);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        await database.NotifyAsync(channel, "wake", ct);
        Assert.Equal("wake", (await NextAsync(enumerator, ct)).Payload);

        var pending = enumerator.MoveNextAsync().AsTask();

        // Disposal runs while the enumeration is parked, so both sides are bounded: disposal must interrupt the wait.
        var disposal = source.DisposeAsync().AsTask();
        var moved = await pending.WaitAsync(PostgreSqlTestGuard.Short, ct);
        await disposal.WaitAsync(PostgreSqlTestGuard.Short, ct);

        Assert.False(moved, "Disposal must end the pending enumeration instead of leaving it parked.");

        await enumerator.DisposeAsync();

        var released = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountBackendsAsync(applicationName, token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(released, "Disposal must release the dedicated LISTEN connection.");
    }

    [Fact]
    public async Task Notify_BackendTermination_FaultsTheSourceWithoutReconnect()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out var applicationName);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);

        var listenPid = await database.ScalarAsync<int>(
            "SELECT pid FROM pg_stat_activity WHERE application_name = @application_name AND pid <> pg_backend_pid()",
            ct,
            ("application_name", applicationName));

        await using var enumerator = Open(source, ct);
        await database.NotifyAsync(channel, "wake", ct);
        Assert.Equal("wake", (await NextAsync(enumerator, ct)).Payload);

        var pending = enumerator.MoveNextAsync().AsTask();
        await database.TerminateBackendAsync(listenPid, ct);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await pending.WaitAsync(PostgreSqlTestGuard.Short, ct);
        });

        var primary = PostgreSqlFailureAssert.Primary(failure);

        // A lost connection is a fault, never caller cancellation, and it is reported as the connection-loss failure
        // when the provider failure is mapped.
        Assert.IsNotType<OperationCanceledException>(primary);
        Assert.True(
            primary is NpgsqlException or IOException or System.Net.Sockets.SocketException
                || primary.Message.Contains(PostgreSqlErrorMessages.NotificationConnectionLost, StringComparison.Ordinal),
            $"Expected a connection-loss fault but observed: {PostgreSqlFailureAssert.Describe(failure)}");

        await enumerator.DisposeAsync();

        // No automatic reconnect: no backend with this application name may appear again.
        var noReconnect = await PostgreSqlIntegrationDatabase.StaysFalseAsync(
            async token => await database.CountBackendsAsync(applicationName, token) > 0,
            PostgreSqlTestGuard.AbsenceWindow,
            ct);
        Assert.True(noReconnect, "The LISTEN source reconnected after the dedicated connection was lost.");

        await DisposeToleratingCleanupFailureAsync(source);
    }

    /// <summary>
    /// Exact-capacity, overflow and first-cause arbitration coverage at the only level where they are provable.
    /// </summary>
    /// <remarks>
    /// An end-to-end overflow cannot be driven against a real server: Npgsql completes one asynchronous message per
    /// <c>WaitAsync</c>, so a burst of NOTIFY statements - however it is shaped, including a notice plus several
    /// notifications in one statement - is handed to the callback one message at a time and the bounded bridge never
    /// fills. A future reader must not "fix" that missing end-to-end case by adding a server-driven test that cannot
    /// fail; the deterministic instrument is the bridge itself, exercised here.
    /// </remarks>
    [Fact]
    public void NotifyBridge_ExactCapacity_AcceptsNAndFaultsOnNPlusOne()
    {
        const int capacity = 3;
        using var bridge = new PostgreSqlNotificationBridge(capacity);

        for (var index = 1; index <= capacity; index++)
        {
            Assert.True(
                bridge.TryWrite(Notification($"accepted-{index}")),
                $"The bridge rejected notification {index} although its capacity is {capacity}.");
        }

        // capacity + 1 is an explicit fault: not accepted, not silently dropped, and it publishes the first cause.
        Assert.False(bridge.TryWrite(Notification("rejected")));
        var overflow = Assert.IsType<InvalidOperationException>(bridge.TerminalCause);
        Assert.Contains(PostgreSqlErrorMessages.NotificationBufferOverflow, overflow.Message, StringComparison.Ordinal);
        Assert.False(bridge.IsCompleted);

        // Nothing is accepted after the terminal cause, and the first cause is immutable.
        Assert.False(bridge.TryWrite(Notification("after-terminal")));
        Assert.False(bridge.TryRecordCause(new InvalidOperationException("a later failure")));
        Assert.Same(overflow, bridge.TerminalCause);

        // Every accepted notification stays readable in FIFO order, and the rejected one is absent.
        var delivered = new List<string>();
        while (bridge.TryRead(out var notification))
            delivered.Add(notification.Payload);
        Assert.Equal(new[] { "accepted-1", "accepted-2", "accepted-3" }, delivered);

        // The wait token is source-private control flow: the overflow already interrupted the outstanding wait, and
        // requesting stop is idempotent on top of it. Accepted notifications stay readable either way.
        Assert.True(bridge.WaitToken.IsCancellationRequested);
        bridge.RequestStop();
        Assert.True(bridge.StopRequested);

        var afterStop = new List<string>();
        while (bridge.TryRead(out var stillAccepted))
            afterStop.Add(stillAccepted.Payload);
        Assert.Empty(afterStop);

        bridge.Dispose();
        Assert.True(bridge.IsCompleted);
        Assert.False(bridge.TryWrite(Notification("after-dispose")));
    }

    [Fact]
    public void NotifyBridge_StopRequestInterruptsTheWaitBeforeAnyFault()
    {
        // A healthy bridge: requesting stop is what interrupts the outstanding wait, without publishing a cause.
        using var bridge = new PostgreSqlNotificationBridge(capacity: 2);
        Assert.True(bridge.TryWrite(Notification("accepted")));
        Assert.False(bridge.WaitToken.IsCancellationRequested);
        Assert.Null(bridge.TerminalCause);

        bridge.RequestStop();

        Assert.True(bridge.StopRequested);
        Assert.True(bridge.WaitToken.IsCancellationRequested);
        Assert.Null(bridge.TerminalCause);
        Assert.True(bridge.TryRead(out var notification));
        Assert.Equal("accepted", notification.Payload);
    }

    [Fact]
    public async Task Notify_BurstLargerThanCapacity_IsDeliveredWithoutSilentLoss()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);
        const int capacity = 3;
        const int burst = 4;

        await using var source = await CreateSourceAsync(
            listen,
            [channel],
            new PostgreSqlNotificationSourceOptions { BufferCapacity = capacity });
        await source.InitializeAsync(ct);

        // Four deliveries arrive before the consumer starts, so they are all waiting on the wire at once. Because
        // Npgsql hands one asynchronous message to the callback per completed wait (see the bridge-level remark on
        // NotifyBridge_ExactCapacity_AcceptsNAndFaultsOnNPlusOne), a real server cannot fill the bounded bridge: the
        // documented behaviour is that every notification is delivered, in order, with no silent drop and no overflow.
        for (var index = 1; index <= burst; index++)
            await database.NotifyAsync(channel, $"burst-{index}", ct);

        await using var enumerator = Open(source, ct);
        var delivered = new List<string>();
        for (var index = 1; index <= burst; index++)
            delivered.Add((await NextAsync(enumerator, ct)).Payload);

        Assert.Equal(new[] { "burst-1", "burst-2", "burst-3", "burst-4" }, delivered);

        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Notify_FailedListenRegistration_RollsBackInitialization()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var listen = database.CreateListenerDataSource(out var applicationName);

        // The embedded NUL is a genuine LISTEN failure: PostgreSQL rejects the message, so registration cannot commit.
        var invalidChannel = $"sp_ch_{Guid.NewGuid():N}\0invalid";
        await using var source = await CreateSourceAsync(listen, [invalidChannel]);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await source.InitializeAsync(ct);
        });

        var primary = PostgreSqlFailureAssert.Primary(failure);
        Assert.IsNotType<OperationCanceledException>(primary);
        Assert.IsAssignableFrom<NpgsqlException>(primary);

        // Initialization rolled back: the dedicated connection is gone and no registration transaction is left open.
        var released = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountBackendsAsync(applicationName, token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(released, "The failed LISTEN registration left its dedicated connection behind.");
        Assert.Equal(0L, await database.CountIdleInTransactionBackendsAsync(applicationName, ct));

        // The component stays disposable, and a valid registration on the very same data source still works.
        await source.DisposeAsync();

        var validChannel = database.NewChannel();
        await using var valid = await CreateSourceAsync(listen, [validChannel]);
        await valid.InitializeAsync(ct);
        await using var enumerator = Open(valid, ct);
        await database.NotifyAsync(validChannel, "after-failure", ct);
        Assert.Equal("after-failure", (await NextAsync(enumerator, ct)).Payload);
        await enumerator.DisposeAsync();
        await valid.DisposeAsync();
    }

    [Fact]
    public async Task Notify_PrimaryFailureIsReportedBeforeCleanupFailures()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out var applicationName);
        var loggerFactory = new RecordingLoggerFactory();

        await using var source = await CreateSourceAsync(listen, [channel], loggerFactory: loggerFactory);
        await source.InitializeAsync(ct);

        var listenPid = await database.ScalarAsync<int>(
            "SELECT pid FROM pg_stat_activity WHERE application_name = @application_name AND pid <> pg_backend_pid()",
            ct,
            ("application_name", applicationName));

        await using var enumerator = Open(source, ct);
        var pending = enumerator.MoveNextAsync().AsTask();
        await database.TerminateBackendAsync(listenPid, ct);

        var runFailure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await pending.WaitAsync(PostgreSqlTestGuard.Short, ct);
        });

        Assert.IsNotType<OperationCanceledException>(PostgreSqlFailureAssert.Primary(runFailure));

        await enumerator.DisposeAsync();

        // Cleanup failures, when they occur, are reported only by disposal: the run failure was already surfaced by
        // the enumeration above, so cleanup can never replace it.
        await DisposeToleratingCleanupFailureAsync(source);

        var entries = loggerFactory.Entries.ToList();
        var runFailureIndex = entries.FindIndex(entry => entry.Contains("faulted with failure category", StringComparison.Ordinal));
        var cleanupIndex = entries.FindIndex(entry => entry.Contains("cleanup produced", StringComparison.Ordinal));
        Assert.True(runFailureIndex >= 0, $"The run failure was never logged: {string.Join(" | ", entries)}");
        if (cleanupIndex >= 0)
        {
            Assert.True(
                runFailureIndex < cleanupIndex,
                "The cleanup failure was reported before the run failure that caused it.");
        }

        Assert.DoesNotContain(entries, entry => entry.Contains(channel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Notify_HandlerIsDetachedAndNothingIsAcceptedAfterDisposal()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out var applicationName);
        var loggerFactory = new RecordingLoggerFactory();

        await using var source = await CreateSourceAsync(listen, [channel], loggerFactory: loggerFactory);
        await source.InitializeAsync(ct);
        await using (var enumerator = Open(source, ct))
        {
            await database.NotifyAsync(channel, "before-disposal", ct);
            Assert.Equal("before-disposal", (await NextAsync(enumerator, ct)).Payload);
            await enumerator.DisposeAsync();
        }

        await source.DisposeAsync();
        await source.DisposeAsync();

        var entryCountAfterDisposal = loggerFactory.Entries.Count;

        // Npgsql exposes no public way to enumerate attached handlers, so the scenario asserts the observable
        // consequences: the dedicated connection is really closed, a late NOTIFY reaches nothing, nothing is logged
        // and the disposed source refuses a new enumeration instead of accepting a delivery.
        var released = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountBackendsAsync(applicationName, token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(released, "The disposed source kept its LISTEN connection open.");

        await database.NotifyAsync(channel, "after-disposal", ct);

        Assert.Equal(entryCountAfterDisposal, loggerFactory.Entries.Count);
        Assert.False(loggerFactory.Contains("after-disposal"), "A notification sent after disposal was logged.");

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in source.ReadEnvelopesAsync(ct))
            {
            }
        });
    }

    [Fact]
    public async Task Notify_LeavesNoLiveTaskAfterDisposal()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out var applicationName);

        await using var source = await CreateSourceAsync(listen, [channel]);
        await source.InitializeAsync(ct);
        await using (var enumerator = Open(source, ct))
        {
            await database.NotifyAsync(channel, "wake", ct);
            Assert.Equal("wake", (await NextAsync(enumerator, ct)).Payload);
            await enumerator.DisposeAsync();
        }

        await source.DisposeAsync();

        var (taskCount, incomplete) = ProbeTasks(source);
        Assert.True(taskCount > 0, "The probe found no Task on the source, so the assertion would be vacuous.");
        Assert.Empty(incomplete);

        var released = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountBackendsAsync(applicationName, token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(released, "A live task would still have to hold the dedicated connection open.");
    }

    [Fact]
    public async Task Notify_StartupRacePattern_ListenThenSnapshotThenLaterChange()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out _);
        var table = database.NewTable("snapshot");
        await database.ExecuteAsync($"CREATE TABLE {table} (id integer PRIMARY KEY, value text NOT NULL)", ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, value) VALUES (1, 'a'), (2, 'b'), (3, 'c')",
            ct);

        await using var source = await CreateSourceAsync(listen, [channel]);

        // 1. LISTEN is ready: the registration transaction has committed.
        await source.InitializeAsync(ct);
        await using var enumerator = Open(source, ct);

        // 2. The durable snapshot is read only after readiness, and it is the source of truth for the consumer state.
        Assert.Equal(3L, await database.CountRowsAsync(table, ct));

        // 3. A later change and its NOTIFY are committed together, which is the durable-outbox shape.
        await using (var connection = await database.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await PostgreSqlIntegrationDatabase.ExecuteOnConnectionAsync(
                connection,
                transaction,
                $"INSERT INTO {table} (id, value) VALUES (4, 'd')",
                ct);

            await PostgreSqlIntegrationDatabase.ExecuteOnConnectionAsync(
                connection,
                transaction,
                "SELECT pg_notify(@channel, @payload)",
                ct,
                ("channel", channel),
                ("payload", "row-4-committed"));

            await transaction.CommitAsync(ct);
        }

        // 4. The wake-up is observed, and the consumer re-reads the durable snapshot rather than trusting the payload.
        var notification = await NextAsync(enumerator, ct);
        Assert.Equal("row-4-committed", notification.Payload);
        Assert.Equal(4L, await database.CountRowsAsync(table, ct));

        // Documented limitation of this pattern: a NOTIFY committed around the initial snapshot may still be delivered
        // after LISTEN became ready, so it can describe state that was already observed. The notification is only a
        // wake-up hint; the durable snapshot decides what work remains, and duplicates must be harmless.
        await enumerator.DisposeAsync();
        await source.DisposeAsync();
    }

    private static PostgreSqlNotification Notification(string payload) =>
        new("bridge-channel", payload, BackendProcessId: 4242);

    /// <summary>
    /// Disposes a component whose connection was lost. Cleanup legitimately fails in that situation; what matters is
    /// that the failure is a cleanup failure and never a cancellation or the run failure that was already reported.
    /// </summary>
    private static async Task DisposeToleratingCleanupFailureAsync(IAsyncDisposable component)
    {
        try
        {
            await component.DisposeAsync();
        }
        catch (Exception failure)
        {
            Assert.IsNotType<OperationCanceledException>(PostgreSqlFailureAssert.Primary(failure));
        }
    }

    private static IAsyncEnumerator<ProcessingEnvelope<PostgreSqlNotification>> Open(
        IPipelineSource<PostgreSqlNotification> source,
        CancellationToken ct) =>
        source.ReadEnvelopesAsync(ct).GetAsyncEnumerator(ct);

    private static async Task<PostgreSqlNotification> NextAsync(
        IAsyncEnumerator<ProcessingEnvelope<PostgreSqlNotification>> enumerator,
        CancellationToken ct)
    {
        var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(PostgreSqlTestGuard.Short, ct);
        Assert.True(moved, "The notification source completed before the expected notification was delivered.");
        return enumerator.Current.Payload;
    }

    /// <summary>
    /// Walks the component's object graph and reports every <see cref="Task"/> it holds plus the ones that are still
    /// running. The graph is limited to the component and its own nested private types, and the non-vacuity of the
    /// probe is asserted by the caller.
    /// </summary>
    private static (int TaskCount, IReadOnlyList<string> Incomplete) ProbeTasks(object instance)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var found = new List<Task>();

        Visit(instance, 0);
        return (found.Count, found.Where(task => !task.IsCompleted).Select(task => task.ToString() ?? "task").ToArray());

        void Visit(object? candidate, int depth)
        {
            if (candidate is null || depth > 2 || !visited.Add(candidate))
                return;

            if (candidate is Task task)
            {
                found.Add(task);
                return;
            }

            var type = candidate.GetType();
            if (type.IsPrimitive || type.IsEnum || candidate is string)
                return;

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var value = field.GetValue(candidate);
                if (value is null)
                    continue;

                if (value is Task)
                {
                    Visit(value, depth + 1);
                    continue;
                }

                var namespaceName = value.GetType().Namespace;
                if (namespaceName is not null && namespaceName.StartsWith("SmartPipe", StringComparison.Ordinal))
                    Visit(value, depth + 1);
            }
        }
    }

    private async Task<IPipelineSource<PostgreSqlNotification>> CreateSourceAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<string> channels,
        PostgreSqlNotificationSourceOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        var descriptor = PostgreSqlPipelineComponents.NotificationSource(
            dataSource,
            channels,
            options ?? new PostgreSqlNotificationSourceOptions(),
            loggerFactory);

        return await PostgreSqlComponentActivation.ActivateAsync(descriptor);
    }
}
