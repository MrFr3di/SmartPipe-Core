#nullable enable

using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Dapper;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Executes one preformed bounded batch envelope with exactly one Dapper command.</summary>
internal sealed class DapperBatchCommandSink<T> : IPipelineSink<IReadOnlyList<T>>
{
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> _acquireConnection;
    private readonly PipelineActivationContext _context;
    private readonly string _sql;
    private readonly DapperBatchSinkOptionsSnapshot _options;
    private readonly Func<T, object?>? _itemParameterFactory;
    private readonly ILogger<DapperBatchCommandSink<T>>? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _disposeSync = new();

    private DapperConnectionLease? _lease;
    private bool _initialized;
    private bool _disposed;
    private Task? _disposeTask;

    internal DapperBatchCommandSink(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> acquireConnection,
        PipelineActivationContext context,
        string sql,
        DapperBatchSinkOptionsSnapshot options,
        Func<T, object?>? itemParameterFactory,
        ILogger<DapperBatchCommandSink<T>>? logger,
        CancellationToken activationCancellationToken)
    {
        _acquireConnection = acquireConnection;
        _context = context;
        _sql = sql;
        _options = options;
        _itemParameterFactory = itemParameterFactory;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
    }

    /// <summary>Opens the first fresh connection of this run.</summary>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            using var linkedCancellation = CreateLinkedCancellation(ct, out var cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _lease = await DapperConnectionLease
                .OpenAsync(_acquireConnection, _context, cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Executes the whole batch with one Dapper command and releases its connection.</summary>
    public async ValueTask WriteAsync(
        ProcessingEnvelope<IReadOnlyList<T>> envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linkedCancellation = CreateLinkedCancellation(ct, out var cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var lease = _lease
                ?? throw new InvalidOperationException(
                    "The Dapper batch command sink is not initialized. Call InitializeAsync before writing.");

            var payload = envelope.Payload
                ?? throw new InvalidOperationException(
                    "The Dapper batch command sink requires a non-null IReadOnlyList payload.");
            if (payload.Count > _options.MaxBatchItems)
                throw new InvalidOperationException(
                    $"The batch payload contains {payload.Count} items, which exceeds MaxBatchItems {_options.MaxBatchItems}.");
            if (payload.Count == 0)
            {
                LogOutcome(_context.TimeProvider.GetTimestamp(), 0, "empty");
                return;
            }

            var items = new List<T>(payload.Count);
            for (var index = 0; index < payload.Count; index++)
                items.Add(payload[index]);

            var parameters = new List<object?>(items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                parameters.Add(_itemParameterFactory is null
                    ? items[index]
                    : _itemParameterFactory(items[index]));
            }

            DbTransaction? transaction = null;
            Exception? primaryFailure = null;
            var affectedRows = 0;
            var startedTimestamp = _context.TimeProvider.GetTimestamp();
            try
            {
                if (_options.TransactionMode == DapperBatchTransactionMode.PerBatch)
                {
                    transaction = _options.IsolationLevel is { } isolationLevel
                        ? await lease.Connection
                            .BeginTransactionAsync(isolationLevel, cancellationToken)
                            .ConfigureAwait(false)
                        : await lease.Connection
                            .BeginTransactionAsync(cancellationToken)
                            .ConfigureAwait(false);
                }

                affectedRows = await lease.Connection
                    .ExecuteAsync(CreateCommandDefinition(parameters, transaction, cancellationToken))
                    .ConfigureAwait(false);

                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }

            var cleanupFailures = new List<Exception>();
            if (primaryFailure is not null && transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            if (transaction is not null)
            {
                try
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            LogOutcome(startedTimestamp, affectedRows, DescribeOutcome(primaryFailure));
            DapperCleanup.ThrowPrimaryFirst(
                primaryFailure,
                cleanupFailures,
                "The Dapper batch command failed and cleanup also failed.");
            if (primaryFailure is not null)
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Releases the current run connection without executing any SQL.</summary>
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeSync)
        {
            task = _disposeTask ??= DisposeCoreAsync();
        }

        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            var cleanupFailures = new List<Exception>();
            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease is not null)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            DapperCleanup.ThrowPrimaryFirst(null, cleanupFailures, "Disposing the Dapper batch command sink failed.");
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private CommandDefinition CreateCommandDefinition(
        object parameters,
        DbTransaction? transaction,
        CancellationToken cancellationToken) =>
        new(
            _sql,
            parameters,
            transaction,
            _options.CommandTimeoutSeconds,
            _options.CommandType,
            _options.Flags,
            cancellationToken);

    private void LogOutcome(long startedTimestamp, int affectedRows, string outcome)
    {
        if (_logger is null)
            return;

        _logger.LogInformation(
            "Dapper batch {OperationName} for pipeline {PipelineKey} run {RunId} {Outcome} after {DurationMilliseconds} ms with {AffectedRows} affected rows.",
            _options.OperationName,
            _context.PipelineKey.Value,
            _context.RunId,
            outcome,
            _context.TimeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            affectedRows);
    }

    private static string DescribeOutcome(Exception? failure) =>
        failure is null ? "succeeded" : failure is OperationCanceledException ? "cancelled" : "failed";

    private CancellationTokenSource? CreateLinkedCancellation(
        CancellationToken requested,
        out CancellationToken effective)
    {
        if (!_activationCancellationToken.CanBeCanceled)
        {
            effective = requested;
            return null;
        }

        if (!requested.CanBeCanceled || requested == _activationCancellationToken)
        {
            effective = _activationCancellationToken;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _activationCancellationToken,
            requested);
        effective = linked.Token;
        return linked;
    }
}
