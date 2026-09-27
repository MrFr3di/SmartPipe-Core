using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Writes every envelope as exactly one complete binary <c>COPY … FROM STDIN (FORMAT BINARY)</c> statement over a
/// single per-run connection owned by this component.
/// </summary>
/// <typeparam name="T">Row value accepted by the configured row writer.</typeparam>
/// <remarks>
/// A successful <see cref="WriteAsync"/> means the COPY already completed on the server: the importer's completion
/// call returned before success was reported. A failure or cancellation before completion aborts the unfinished COPY
/// by disposing the importer, which makes PostgreSQL revert its rows; the connection is deliberately kept for the
/// next batch, because a connection broken by the failure is expected to fail the next batch. There is no retry and
/// no inference of rollback or success from an ambiguous completion.
/// </remarks>
internal sealed partial class PostgreSqlBinaryCopyBatchSink<T> : IPipelineSink<IReadOnlyList<T>>
{
    private const int Active = 0;
    private const int Disposing = 1;
    private const int Disposed = 2;
    private const int Faulted = 3;

    private const string OperationKind = "binary-copy-batch-sink";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _copyFromCommand;
    private readonly Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> _rowWriter;
    private readonly PostgreSqlBinaryCopySinkOptionsSnapshot _options;
    private readonly ILogger? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly Func<NpgsqlDataSource, CancellationToken, ValueTask<NpgsqlConnection>>? _connectionFactory;
    private readonly Func<NpgsqlConnection, string, CancellationToken, Task<NpgsqlBinaryImporter>>? _importerFactory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _disposeSync = new();

    private NpgsqlConnection? _connection;
    private NpgsqlBinaryImporter? _importer;
    private bool _initialized;
    private int _state;
    private Task? _disposeTask;

    public PostgreSqlBinaryCopyBatchSink(
        NpgsqlDataSource dataSource,
        string copyFromCommand,
        Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> rowWriter,
        PostgreSqlBinaryCopySinkOptionsSnapshot options,
        ILogger? logger,
        CancellationToken activationCancellationToken,
        Func<NpgsqlDataSource, CancellationToken, ValueTask<NpgsqlConnection>>? connectionFactory = null,
        Func<NpgsqlConnection, string, CancellationToken, Task<NpgsqlBinaryImporter>>? importerFactory = null)
    {
        _dataSource = dataSource;
        _copyFromCommand = copyFromCommand;
        _rowWriter = rowWriter;
        _options = options;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
        _connectionFactory = connectionFactory;
        _importerFactory = importerFactory;
    }

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfNotActive();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            if (_initialized)
                return;

            using var linked = PostgreSqlLinkedCancellation.Create(
                _activationCancellationToken,
                ct,
                out var cancellationToken);
            try
            {
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception>();
                await DisposeResourcesAsync(cleanupFailures).ConfigureAwait(false);
                PostgreSqlFailures.Throw(
                    primaryFailure,
                    cleanupFailures,
                    PostgreSqlErrorMessages.SinkCleanupFailed,
                    PostgreSqlErrorMessages.SinkCleanupOnlyFailed);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask WriteAsync(ProcessingEnvelope<IReadOnlyList<T>> envelope, CancellationToken ct = default)
    {
        ThrowIfNotActive();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            var connection = _connection;
            if (!_initialized || connection is null)
                throw new InvalidOperationException(PostgreSqlErrorMessages.SinkNotInitialized);

            using var linked = PostgreSqlLinkedCancellation.Create(
                _activationCancellationToken,
                ct,
                out var cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var batch = envelope.Payload;
            if (batch is null || batch.Count == 0)
                return;

            // Rejected before any server work, so an oversized batch can never partially reach PostgreSQL.
            if (batch.Count > _options.MaxRowsPerBatch)
                throw new InvalidOperationException(PostgreSqlErrorMessages.BatchTooLarge);

            // A scope entered after activation would let Npgsql enlist the connection and break the
            // "one envelope equals one completed COPY" guarantee, so it must fail before the COPY starts.
            PostgreSqlAmbientTransaction.Reject();

            await CopyBatchAsync(connection, batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                Interlocked.CompareExchange(ref _state, Disposing, Active);
                _disposeTask = DisposeCoreAsync();
            }

            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private async ValueTask InitializeCoreAsync(CancellationToken cancellationToken)
    {
        PostgreSqlAmbientTransaction.Reject();

        // A run that is already cancelled must not reach the pool at all: checking here keeps the borrowed
        // application data source untouched and makes the rejection observable as cancellation rather than as a
        // provider open attempt.
        cancellationToken.ThrowIfCancellationRequested();

        // Exactly one connection per activation, borrowed from the application-owned data source. COPY starts only
        // inside WriteAsync, never here.
        _connection = _connectionFactory is not null
            ? await _connectionFactory(_dataSource, cancellationToken).ConfigureAwait(false)
            : await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // A connection obtained for an already cancelled run is released by the failure path instead of being handed
        // to a batch. The field is assigned first so the failure path can always dispose it.
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask CopyBatchAsync(
        NpgsqlConnection connection,
        IReadOnlyList<T> batch,
        CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        ulong rowsCopied = 0;
        try
        {
            var importer = _importerFactory is not null
                ? await _importerFactory(connection, _copyFromCommand, cancellationToken).ConfigureAwait(false)
                : await connection.BeginBinaryImportAsync(_copyFromCommand, cancellationToken).ConfigureAwait(false);
            _importer = importer;

            foreach (var row in batch)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await _rowWriter(importer, row, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // The caller's token is used deliberately: completion is server work that must stay cancellable, and a
            // successful return here is the only evidence that makes this write a success.
            rowsCopied = await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        // Disposing an incomplete import cancels it (CopyFail) and PostgreSQL reverts the rows. Successful or not,
        // the importer is released here and a disposal failure never replaces the primary failure.
        await DisposeImporterAsync(cleanupFailures).ConfigureAwait(false);

        if (primaryFailure is not null || cleanupFailures.Count != 0)
        {
            LogFailed(primaryFailure ?? cleanupFailures[0]);
            PostgreSqlFailures.Throw(
                primaryFailure,
                cleanupFailures,
                PostgreSqlErrorMessages.SinkCleanupFailed,
                PostgreSqlErrorMessages.SinkCleanupOnlyFailed);
        }

        LogCompleted(rowsCopied);
    }

    private async Task DisposeCoreAsync()
    {
        var acquired = false;
        var cleanupFailures = new List<Exception>();
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            acquired = true;

            // Cleanup only: no SQL, no business write and no completion call. An importer still held here belongs
            // to an unfinished COPY, so disposing it aborts that COPY.
            await DisposeResourcesAsync(cleanupFailures).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                _writeGate.Release();

            // The gate is part of this component's disposable state, exactly as in the CSV sink. Nothing can enter
            // WriteAsync after this point: DisposeAsync sets the state to Disposing before this runs, so a later
            // WriteAsync fails ThrowIfNotActive instead of waiting on a disposed semaphore.
            _writeGate.Dispose();
        }

        if (cleanupFailures.Count != 0)
        {
            LogFailed(cleanupFailures[0]);
            Volatile.Write(ref _state, Faulted);
            PostgreSqlFailures.Throw(
                null,
                cleanupFailures,
                PostgreSqlErrorMessages.SinkCleanupFailed,
                PostgreSqlErrorMessages.SinkCleanupOnlyFailed);
        }

        Volatile.Write(ref _state, Disposed);
    }

    private async ValueTask DisposeResourcesAsync(List<Exception> failures)
    {
        await DisposeImporterAsync(failures).ConfigureAwait(false);
        await DisposeConnectionAsync(failures).ConfigureAwait(false);
        _initialized = false;
    }

    private async ValueTask DisposeImporterAsync(List<Exception> failures)
    {
        var importer = _importer;
        _importer = null;
        if (importer is null)
            return;

        try
        {
            await importer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async ValueTask DisposeConnectionAsync(List<Exception> failures)
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
            return;

        try
        {
            // Borrowed from the application-owned data source; the data source itself is never disposed here.
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private void LogCompleted(ulong rowsCopied)
    {
        if (_logger is not null)
            LogCopyCompleted(_logger, _options.OperationName, OperationKind, rowsCopied);
    }

    private void LogFailed(Exception failure)
    {
        // Only a category is logged: provider messages can quote rejected row values, which never reach logs.
        if (_logger is not null)
            LogCopyFailure(_logger, _options.OperationName, OperationKind, DescribeFailure(failure));
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "canceled",
        NpgsqlException => "npgsql",
        InvalidOperationException => "invalid-operation",
        _ => "failure",
    };

    [LoggerMessage(1, LogLevel.Debug, "PostgreSQL binary COPY batch sink completed a COPY. Operation={OperationName} Kind={OperationKind} RowsCopied={RowsCopied}.")]
    private static partial void LogCopyCompleted(
        ILogger logger,
        string operationName,
        string operationKind,
        ulong rowsCopied);

    [LoggerMessage(2, LogLevel.Warning, "PostgreSQL binary COPY batch sink detected a failure. Operation={OperationName} Kind={OperationKind} FailureCategory={FailureCategory}.")]
    private static partial void LogCopyFailure(
        ILogger logger,
        string operationName,
        string operationKind,
        string failureCategory);

    private void ThrowIfNotActive()
    {
        if (Volatile.Read(ref _state) != Active)
            throw new ObjectDisposedException(nameof(PostgreSqlBinaryCopyBatchSink<T>));
    }
}
