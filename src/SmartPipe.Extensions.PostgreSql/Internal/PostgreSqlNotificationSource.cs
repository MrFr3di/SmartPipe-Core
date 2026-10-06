#nullable enable

using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>Streams the <c>NOTIFY</c> deliveries of one LISTEN run over a dedicated connection.</summary>
/// <remarks>
/// <para>
/// The source is a best-effort wake-up signal, never a durable queue: it does not reconnect, does not replay and
/// does not invent an event timestamp. Readiness is reported only after the registration transaction committed,
/// because <c>LISTEN</c> takes effect at <c>COMMIT</c>; a committed <c>LISTEN</c> is therefore visible to a
/// notification sender before the first enumeration starts.
/// </para>
/// <para>
/// The enumerator owns the whole wait lifecycle of the connection: it waits for an asynchronous server message,
/// drains the bounded bridge and never polls PostgreSQL with a periodic query. No background task is started, no
/// automatic reconnect is attempted and a lost connection faults the run.
/// </para>
/// <para>
/// Disposing the component while an enumeration is pending ends that enumeration without a fault, because completion
/// is the bridge signal that the run is over.
/// </para>
/// </remarks>
internal sealed class PostgreSqlNotificationSource : IPipelineSource<PostgreSqlNotification>
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlChannelSet _channelSet;
    private readonly PostgreSqlNotificationSourceOptionsSnapshot _options;
    private readonly ILogger? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly Func<NpgsqlTransaction, string, CancellationToken, ValueTask>? _listenRegistration;
    private readonly Func<NpgsqlConnection, CancellationToken, ValueTask>? _unlisten;
    private readonly PostgreSqlNotificationBridge _bridge;
    private readonly NpgsqlCommandBuilder _identifierQuoter = new();

    /// <summary>Becomes 1 on the first enumeration; the bridge is a SingleReader channel.</summary>
    private int _enumerationStarted;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _disposalSync = new();

    private NpgsqlConnection? _connection;
    private Task? _disposal;
    private bool _handlerAttached;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates one notification source for a single runtime-owned activation.</summary>
    /// <param name="dataSource">The application-owned data source. Never disposed by this component.</param>
    /// <param name="channelSet">The validated, defensively copied channel identifiers.</param>
    /// <param name="options">The validated options snapshot.</param>
    /// <param name="logger">An optional borrowed logger.</param>
    /// <param name="activationCancellationToken">The run-scoped activation token.</param>
    /// <param name="listenRegistration">
    /// Test seam that replaces the per-channel <c>LISTEN</c> execution. The product path passes <see langword="null"/>
    /// and executes the quoted statement inside the registration transaction.
    /// </param>
    /// <param name="unlisten">
    /// Test seam that replaces the best-effort <c>UNLISTEN *</c> executed during disposal. The product path passes
    /// <see langword="null"/>.
    /// </param>
    internal PostgreSqlNotificationSource(
        NpgsqlDataSource dataSource,
        PostgreSqlChannelSet channelSet,
        PostgreSqlNotificationSourceOptionsSnapshot options,
        ILogger? logger,
        CancellationToken activationCancellationToken,
        Func<NpgsqlTransaction, string, CancellationToken, ValueTask>? listenRegistration = null,
        Func<NpgsqlConnection, CancellationToken, ValueTask>? unlisten = null)
    {
        _dataSource = dataSource;
        _channelSet = channelSet;
        _options = options;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
        _listenRegistration = listenRegistration;
        _unlisten = unlisten;
        _bridge = new PostgreSqlNotificationBridge(options.BufferCapacity);
    }

    /// <summary>Opens the dedicated connection and registers every channel in one committed transaction.</summary>
    /// <param name="ct">Cancellation token for activation.</param>
    /// <returns>A value task that completes after the registration transaction committed.</returns>
    /// <remarks>
    /// Activation is idempotent and gated. Any failure or cancellation rolls the registration back with a
    /// non-cancelled token, detaches the notification handler, disposes the transaction and the connection, and
    /// surfaces the primary failure ahead of the cleanup failures.
    /// </remarks>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            using var linkedCancellation = PostgreSqlLinkedCancellation.Create(
                _activationCancellationToken,
                ct,
                out var token);
            token.ThrowIfCancellationRequested();

            // An ambient transaction would silently enlist the dedicated connection and make the single registration
            // transaction meaningless, so it is rejected before the first provider call.
            PostgreSqlAmbientTransaction.Reject();

            _connection = await ActivateAsync(token).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>Streams accepted notifications until a terminal cause or the end of the run.</summary>
    /// <param name="ct">Cancellation token for this enumeration.</param>
    /// <returns>An asynchronous sequence of notification envelopes.</returns>
    /// <remarks>
    /// The first recorded cause arbitrates the outcome. Caller or activation cancellation recorded first stops the
    /// enumeration immediately. Overflow or a provider/connection fault recorded first delivers every already
    /// accepted notification in FIFO order and then surfaces the stored fault; cancellation observed during that
    /// drain stops it promptly without replacing the stored fault.
    /// </remarks>
    public async IAsyncEnumerable<ProcessingEnvelope<PostgreSqlNotification>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The bridge is a SingleReader channel, so a second or concurrent enumeration would violate the BCL
        // single-reader promise instead of failing fast. The COPY source rejects the same misuse.
        if (Interlocked.CompareExchange(ref _enumerationStarted, 1, 0) != 0)
            throw new InvalidOperationException(PostgreSqlErrorMessages.NotificationSourceEnumeratedTwice);

        var connection = _connection
            ?? throw new InvalidOperationException(PostgreSqlErrorMessages.SourceNotInitialized);

        using var linkedCancellation = PostgreSqlLinkedCancellation.Create(
            _activationCancellationToken,
            ct,
            out var effective);
        using var cancellationRegistration = RegisterCancellation(effective, ct);
        using var waitLinkedCancellation = PostgreSqlLinkedCancellation.Create(
            effective,
            _bridge.WaitToken,
            out var waitToken);

        var notificationCount = 0L;
        Exception? failure = null;
        try
        {
            while (true)
            {
                var terminal = _bridge.TerminalCause;
                if (terminal is OperationCanceledException)
                {
                    // Caller or activation cancellation was recorded first: stop immediately without draining.
                    failure = terminal;
                    throw terminal;
                }

                if (terminal is not null)
                {
                    // Overflow or a provider/connection fault was recorded first: deliver the notifications that
                    // were already accepted in FIFO order, then surface the fault that won the arbitration. A
                    // cancellation observed during this drain stops it promptly and never replaces the fault.
                    while (!effective.IsCancellationRequested)
                    {
                        if (!_bridge.TryRead(out var accepted))
                            break;

                        notificationCount++;
                        yield return ProcessingEnvelope<PostgreSqlNotification>.Create(accepted);
                    }

                    var fault = _bridge.TerminalCause ?? terminal;
                    failure = fault;
                    throw fault;
                }

                if (effective.IsCancellationRequested)
                {
                    // Cancellation observed before a cause was published becomes the terminal cause, unless another
                    // failure won the arbitration in the meantime.
                    var cancellation = CreateCancellation(effective, ct);
                    _bridge.TryRecordCause(cancellation);
                    var cancelled = _bridge.TerminalCause ?? cancellation;
                    failure = cancelled;
                    throw cancelled;
                }

                if (_bridge.StopRequested)
                    break;

                if (_bridge.TryRead(out var notification))
                {
                    notificationCount++;
                    yield return ProcessingEnvelope<PostgreSqlNotification>.Create(notification);
                    continue;
                }

                // WaitAsync completes for any asynchronous message, not only for a notification, so the loop always
                // comes back here and drains the bridge again.
                await WaitForServerMessageAsync(connection, waitToken).ConfigureAwait(false);
            }
        }
        finally
        {
            LogOutcome(notificationCount, failure ?? _bridge.TerminalCause);
        }
    }

    /// <summary>Releases the dedicated connection and its registration exactly once.</summary>
    /// <returns>A value task that completes after cleanup.</returns>
    /// <remarks>
    /// Cleanup order: cancel the wait, detach the notification handler, best-effort <c>UNLISTEN *</c>, complete the
    /// bridge and dispose the connection. Cleanup failures never replace a run failure.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposalSync)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    /// <summary>Opens the connection, attaches the handler exactly once and commits the channel registration.</summary>
    private async ValueTask<NpgsqlConnection> ActivateAsync(CancellationToken token)
    {
        var cleanupFailures = new List<Exception>();
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);

            // The handler is attached before registration so a notification that races the LISTEN commit is not lost.
            connection.Notification += OnNotification;
            _handlerAttached = true;

            PostgreSqlAmbientTransaction.Reject();

            transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            foreach (var channel in _channelSet.Channels)
            {
                await RegisterChannelAsync(connection, transaction, channel, token).ConfigureAwait(false);
            }

            // LISTEN takes effect at COMMIT, so readiness is reported only after this call returns.
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            await RollbackAsync(transaction, cleanupFailures).ConfigureAwait(false);
            DetachHandler(connection, cleanupFailures);
            await DisposeTransactionAsync(transaction, cleanupFailures).ConfigureAwait(false);
            await DisposeConnectionAsync(connection, cleanupFailures).ConfigureAwait(false);
            LogActivationFailure(primaryFailure);
            FailActivation(primaryFailure, cleanupFailures);
        }

        // A committed transaction owns nothing anymore, but releasing it must still be attempted and reported.
        await DisposeTransactionAsync(transaction, cleanupFailures).ConfigureAwait(false);
        if (cleanupFailures.Count != 0)
        {
            PostgreSqlFailures.Throw(
                null,
                cleanupFailures,
                PostgreSqlErrorMessages.NotificationCleanupAfterFailureFailed,
                PostgreSqlErrorMessages.NotificationCleanupOnlyFailed);
        }

        return connection!;
    }

    /// <summary>Waits for the next asynchronous server message on the dedicated connection.</summary>
    /// <remarks>
    /// The connection gate serializes this wait against the protocol work of disposal, so no statement is ever issued
    /// while a wait is still unwinding and no wait is ever started on a connection that disposal already released.
    /// </remarks>
    private async ValueTask WaitForServerMessageAsync(NpgsqlConnection connection, CancellationToken waitToken)
    {
        try
        {
            await _connectionGate.WaitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            // Cancellation, overflow, a recorded fault or disposal interrupted the wait: source-private and run
            // cancellation are pure control flow here, so nothing is recorded and nothing is mislabelled. An
            // OperationCanceledException raised while the token is still live is not control flow and is handled as a
            // provider fault by the caller instead of being swallowed into a silent retry loop.
            return;
        }

        try
        {
            if (_bridge.StopRequested || waitToken.IsCancellationRequested)
                return;

            await connection.WaitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            // A cancelled wait is control flow: the first-cause slot and the stop flag decide what happens next. An
            // OperationCanceledException with a live token means the provider failed rather than that we asked it to
            // stop, so it falls through to the provider-failure handler below instead of being swallowed.
        }
        catch (Exception exception)
        {
            _bridge.TryRecordCause(CreateProviderFailure(exception));
            _bridge.CancelWait();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>Bridges one provider notification into the bounded channel without blocking.</summary>
    /// <remarks>
    /// Npgsql invokes this handler on the connection read path and swallows anything thrown here, so the handler must
    /// never block, never await and never run user code of its own. Its only effect is one non-blocking bounded write.
    /// <para>
    /// One deliberate consequence: when that write finds the bridge full, the bridge records the overflow and calls
    /// <see cref="CancellationTokenSource.Cancel()"/> for the source-private wait token. A provider wait can only be
    /// interrupted through its own token, so that cancellation is intrinsic to stopping the wait, and it runs the
    /// registered wait callbacks synchronously on the provider read path. The registrations involved are this
    /// component's own wait machinery (the connection gate waiter, the linked wait source and Npgsql's wait
    /// registration); the handler itself still neither blocks nor awaits nor runs user pipeline code.
    /// </para>
    /// </remarks>
    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        _bridge.TryWrite(new PostgreSqlNotification(args.Channel, args.Payload, args.PID));
    }

    /// <summary>Records caller or activation cancellation as soon as the token is signalled.</summary>
    /// <returns>The registration that must be disposed with the enumeration.</returns>
    private CancellationTokenRegistration RegisterCancellation(CancellationToken effective, CancellationToken requested)
    {
        if (!effective.CanBeCanceled)
            return default;

        return effective.Register(
            () => _bridge.TryRecordCause(CreateCancellation(effective, requested)));
    }

    /// <summary>Creates the cancellation failure that identifies the token which actually signalled.</summary>
    private OperationCanceledException CreateCancellation(CancellationToken effective, CancellationToken requested)
    {
        var signalled = requested.IsCancellationRequested
            ? requested
            : _activationCancellationToken.IsCancellationRequested
                ? _activationCancellationToken
                : effective;
        return new OperationCanceledException(signalled);
    }

    /// <summary>Executes one quoted <c>LISTEN</c> inside the registration transaction.</summary>
    private async ValueTask RegisterChannelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string channel,
        CancellationToken token)
    {
        if (_listenRegistration is not null)
        {
            await _listenRegistration(transaction, channel, token).ConfigureAwait(false);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // PostgreSQL does not allow a parameter for the LISTEN identifier. Npgsql's QuoteIdentifier encloses the
        // identifier and doubles embedded quotes; existing integration coverage includes a quoted, spaced channel.
#pragma warning disable S2077 // The LISTEN identifier is Npgsql-quoted because PostgreSQL identifiers are not parameterizable.
        command.CommandText = "LISTEN " + _identifierQuoter.QuoteIdentifier(channel);
#pragma warning restore S2077
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Executes the best-effort <c>UNLISTEN *</c> of a healthy connection.</summary>
    private async ValueTask UnlistenAsync(NpgsqlConnection connection, CancellationToken token)
    {
        if (_unlisten is not null)
        {
            await _unlisten(connection, token).ConfigureAwait(false);
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "UNLISTEN *";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Unregisters a still-open session and skips a session that can no longer hold a registration.</summary>
    private async ValueTask TryUnlistenAsync(NpgsqlConnection connection, List<Exception> cleanupFailures)
    {
        // Only an open session still holds registrations: a lost session dropped them when it closed, so UNLISTEN
        // has no effect to lose and its failure would only repeat the connection loss the caller already observed.
        if (connection.FullState != ConnectionState.Open)
            return;

        try
        {
            // Cleanup protocol work never runs with the run token, so a cancelled run is still unregistered.
            await UnlistenAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
    }

    /// <summary>Rolls the registration back with a non-cancelled cleanup token.</summary>
    private static async ValueTask RollbackAsync(NpgsqlTransaction? transaction, List<Exception> cleanupFailures)
    {
        if (transaction is null)
            return;

        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
    }

    /// <summary>Disposes the registration transaction, recording a cleanup failure.</summary>
    private static async ValueTask DisposeTransactionAsync(NpgsqlTransaction? transaction, List<Exception> cleanupFailures)
    {
        if (transaction is null)
            return;

        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
    }

    /// <summary>Disposes the dedicated connection, recording a cleanup failure.</summary>
    private static async ValueTask DisposeConnectionAsync(NpgsqlConnection? connection, List<Exception> cleanupFailures)
    {
        if (connection is null)
            return;

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
    }

    /// <summary>Detaches the notification handler exactly once, recording a cleanup failure.</summary>
    private void DetachHandler(NpgsqlConnection? connection, List<Exception> cleanupFailures)
    {
        if (!_handlerAttached)
            return;

        _handlerAttached = false;
        if (connection is null)
            return;

        try
        {
            connection.Notification -= OnNotification;
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
    }

    /// <summary>Fails activation with the primary failure ahead of the cleanup failures.</summary>
    [DoesNotReturn]
    private static void FailActivation(Exception primaryFailure, IReadOnlyList<Exception> cleanupFailures)
    {
        PostgreSqlFailures.Throw(
            primaryFailure,
            cleanupFailures,
            PostgreSqlErrorMessages.NotificationCleanupAfterFailureFailed,
            PostgreSqlErrorMessages.NotificationCleanupOnlyFailed);

        throw new UnreachableException();
    }

    /// <summary>Releases the run resources exactly once.</summary>
    private async Task DisposeCoreAsync()
    {
        await _initializeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        var cleanupFailures = new List<Exception>();
        try
        {
            if (_disposed)
                return;

            _disposed = true;

            // 1. Stop accepting and interrupt the outstanding provider wait so the enumerator leaves WaitAsync.
            _bridge.RequestStop();

            var connection = _connection;
            _connection = null;

            // 2. Detach the provider handler so no further notification can reach the bridge.
            DetachHandler(connection, cleanupFailures);

            if (connection is not null)
            {
                await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    // 3. Best-effort UNLISTEN *: an open session is unregistered even when the run failed, should
                    //    that unregistration fail it is reported as a cleanup failure instead of replacing the run
                    //    failure, and a session that is no longer open is skipped entirely.
                    await TryUnlistenAsync(connection, cleanupFailures).ConfigureAwait(false);

                    // 4. Complete the bridge: accepted notifications stay readable, nothing new is accepted.
                    _bridge.Dispose();

                    // 5. Returning the connection to the application pool is the last provider interaction.
                    await DisposeConnectionAsync(connection, cleanupFailures).ConfigureAwait(false);
                }
                finally
                {
                    _connectionGate.Release();
                }
            }
            else
            {
                _bridge.Dispose();
            }
        }
        finally
        {
            // Idempotent backstop: the ordered completion may have been skipped by an exceptional cleanup path.
            _bridge.Dispose();
            _initializeGate.Release();
            _initializeGate.Dispose();
            // SemaphoreSlim has no native wait handle here; keep it alive while an iterator may still unwind its gate.
            _identifierQuoter.Dispose();
        }

        LogCleanupFailures(cleanupFailures);
        PostgreSqlFailures.Throw(
            null,
            cleanupFailures,
            PostgreSqlErrorMessages.NotificationCleanupAfterFailureFailed,
            PostgreSqlErrorMessages.NotificationCleanupOnlyFailed);
    }

    /// <summary>Classifies a failure observed while waiting for the next server message.</summary>
    /// <remarks>
    /// Every provider-level failure on the wait path means the dedicated LISTEN connection is no longer usable:
    /// nothing is replayed and no reconnect is attempted.
    /// </remarks>
    private static Exception CreateProviderFailure(Exception exception) =>
        exception switch
        {
            NpgsqlException => CreateConnectionLostFailure(exception),
            IOException => CreateConnectionLostFailure(exception),
            SocketException => CreateConnectionLostFailure(exception),
            TimeoutException => CreateConnectionLostFailure(exception),
            ObjectDisposedException => CreateConnectionLostFailure(exception),
            _ => exception,
        };

    private static InvalidOperationException CreateConnectionLostFailure(Exception exception) =>
        new(PostgreSqlErrorMessages.NotificationConnectionLost, exception);

    private void LogOutcome(long notificationCount, Exception? failure)
    {
        if (_logger is null)
            return;

        if (failure is null)
        {
            _logger.LogInformation(
                "PostgreSQL LISTEN source {OperationName} stopped after delivering {NotificationCount} notifications with bridge capacity {BridgeCapacity}.",
                _options.OperationName,
                notificationCount,
                _options.BufferCapacity);
            return;
        }

        if (failure is OperationCanceledException)
        {
            _logger.LogInformation(
                "PostgreSQL LISTEN source {OperationName} was cancelled after delivering {NotificationCount} notifications with bridge capacity {BridgeCapacity}.",
                _options.OperationName,
                notificationCount,
                _options.BufferCapacity);
            return;
        }

        _logger.LogWarning(
            "PostgreSQL LISTEN source {OperationName} faulted with failure category {FailureCategory} after delivering {NotificationCount} notifications with bridge capacity {BridgeCapacity}.",
            _options.OperationName,
            DescribeFailureCategory(failure),
            notificationCount,
            _options.BufferCapacity);
    }

    private void LogActivationFailure(Exception primaryFailure) =>
        _logger?.LogWarning(
            "PostgreSQL LISTEN source {OperationName} failed to activate with failure category {FailureCategory}.",
            _options.OperationName,
            DescribeFailureCategory(primaryFailure));

    private void LogCleanupFailures(IReadOnlyList<Exception> cleanupFailures)
    {
        if (_logger is null || cleanupFailures.Count == 0)
            return;

        _logger.LogWarning(
            "PostgreSQL LISTEN source {OperationName} cleanup produced {CleanupFailureCount} failures with failure category {FailureCategory}.",
            _options.OperationName,
            cleanupFailures.Count,
            DescribeFailureCategory(cleanupFailures[0]));
    }

    /// <summary>Maps a failure to a log-safe category; messages, payloads and channel text are never logged.</summary>
    private static string DescribeFailureCategory(Exception failure) =>
        failure is OperationCanceledException
            ? "cancelled"
            : failure.Message switch
            {
                PostgreSqlErrorMessages.NotificationBufferOverflow => "buffer-overflow",
                PostgreSqlErrorMessages.NotificationConnectionLost => "connection-lost",
                _ => "provider",
            };
}
