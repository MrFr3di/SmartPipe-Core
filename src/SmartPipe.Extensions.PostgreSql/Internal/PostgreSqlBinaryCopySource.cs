using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Streams a binary <c>COPY … TO STDOUT (FORMAT BINARY)</c> export as <see cref="ProcessingEnvelope{T}"/> values.
/// </summary>
/// <typeparam name="T">The row value produced by the caller-supplied row reader.</typeparam>
/// <remarks>
/// <para>
/// One instance serves exactly one pipeline activation. <see cref="InitializeAsync"/> opens one pooled connection and
/// deliberately performs no COPY work, so activation never blocks on a server-side export; the protocol starts with
/// the first <see cref="ReadEnvelopesAsync"/> call.
/// </para>
/// <para>
/// Rows are streamed one at a time from the server-side cursor. Nothing is materialised, prefetched or moved to a
/// detached task, so memory use does not depend on the result size. A normal end of stream completes the export,
/// whereas an early consumer break, cancellation or row-reader failure cancels the export before the exporter is
/// disposed, which is the only way PostgreSQL stops producing rows nobody will read.
/// </para>
/// <para>
/// Exactly one enumeration is permitted per instance. <see cref="DisposeAsync"/> is single-flight and idempotent,
/// starts no protocol work of its own, and releases the exporter (when an abandoned enumeration left one behind)
/// followed by the connection.
/// </para>
/// </remarks>
internal sealed class PostgreSqlBinaryCopySource<T> : IPipelineSource<T>
{
    private const string InitializeCategory = "initialize";
    private const string ExportStartCategory = "export-start";
    private const string ReadCategory = "read";
    private const string CallbackCategory = "callback";
    private const string ColumnCountCategory = "column-count";
    private const string CancelledCategory = "cancelled";
    private const string CleanupCategory = "cleanup";

    private readonly string _copyToCommand;
    private readonly Func<NpgsqlBinaryExporter, int, CancellationToken, ValueTask<T>> _rowReader;
    private readonly PostgreSqlBinaryCopySourceOptionsSnapshot _options;
    private readonly ILogger? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly Func<CancellationToken, ValueTask<NpgsqlConnection>> _connectionFactory;
    private readonly Func<NpgsqlConnection, string, CancellationToken, Task<NpgsqlBinaryExporter>> _exporterFactory;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly object _disposeSync = new();

    private NpgsqlConnection? _connection;
    private NpgsqlBinaryExporter? _exporter;
    private bool _initialized;
    private bool _disposed;
    private int _enumerationStarted;
    private Task? _disposeTask;

    /// <summary>Creates a copy-out source for one pipeline activation.</summary>
    /// <param name="dataSource">The application-owned data source. Never disposed by this source.</param>
    /// <param name="copyToCommand">The complete configuration COPY statement sent to PostgreSQL.</param>
    /// <param name="rowReader">Reads exactly one row from the borrowed exporter.</param>
    /// <param name="options">The validated, immutable option snapshot.</param>
    /// <param name="logger">An optional borrowed logger. Never disposed by this source.</param>
    /// <param name="activationCancellationToken">The run-scoped activation token.</param>
    /// <param name="connectionFactory">Test seam for opening the activation connection.</param>
    /// <param name="exporterFactory">Test seam for starting the binary export.</param>
    internal PostgreSqlBinaryCopySource(
        NpgsqlDataSource dataSource,
        string copyToCommand,
        Func<NpgsqlBinaryExporter, int, CancellationToken, ValueTask<T>> rowReader,
        PostgreSqlBinaryCopySourceOptionsSnapshot options,
        ILogger? logger,
        CancellationToken activationCancellationToken,
        Func<CancellationToken, ValueTask<NpgsqlConnection>>? connectionFactory = null,
        Func<NpgsqlConnection, string, CancellationToken, Task<NpgsqlBinaryExporter>>? exporterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(copyToCommand);
        ArgumentNullException.ThrowIfNull(rowReader);
        ArgumentNullException.ThrowIfNull(options);

        _copyToCommand = copyToCommand;
        _rowReader = rowReader;
        _options = options;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
        _connectionFactory = connectionFactory ?? dataSource.OpenConnectionAsync;
        _exporterFactory = exporterFactory ?? BeginBinaryExportAsync;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        var effectiveToken = ct;
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            PostgreSqlAmbientTransaction.Reject();

            using var linked = PostgreSqlLinkedCancellation.Create(
                _activationCancellationToken,
                ct,
                out var cancellationToken);
            effectiveToken = cancellationToken;

            // A run that is already cancelled must not reach the pool at all: checking here keeps the borrowed
            // application data source untouched and makes the rejection observable as cancellation rather than as a
            // provider open attempt.
            cancellationToken.ThrowIfCancellationRequested();

            _connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch (Exception primaryFailure)
        {
            // The linked token governs the connection open, so it is the token that must decide whether this failure
            // is cancellation or an initialization fault.
            var failure = Classify(primaryFailure, effectiveToken, InitializeCategory);
            var cleanupFailures = await ReleaseResourcesAsync(exportCompleted: false).ConfigureAwait(false);
            LogOutcome(failure, rowCount: 0L, cleanupFailures);
            PostgreSqlFailures.Throw(
                failure.Exception,
                cleanupFailures,
                PostgreSqlErrorMessages.SourceCleanupFailed,
                PostgreSqlErrorMessages.SourceCleanupOnlyFailed);
            throw;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = PostgreSqlLinkedCancellation.Create(
            _activationCancellationToken,
            ct,
            out var cancellationToken);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (Interlocked.CompareExchange(ref _enumerationStarted, 1, 0) != 0)
            throw new InvalidOperationException(PostgreSqlErrorMessages.SourceEnumeratedTwice);

        var connection = Volatile.Read(ref _connection)
            ?? throw new InvalidOperationException(PostgreSqlErrorMessages.SourceNotInitialized);

        PostgreSqlAmbientTransaction.Reject();

        var (exporter, startFailure) = await TryStartExportAsync(connection, cancellationToken).ConfigureAwait(false);
        var failure = startFailure;
        var exportCompleted = false;
        var rowCount = 0L;

        try
        {
            if (failure is null && exporter is null)
            {
                // A substituted exporter factory returned neither a cursor nor a failure. Report it through the same
                // primary-failure path instead of entering the protocol loop without an exporter.
                failure = new SourceFailure(
                    new InvalidOperationException(PostgreSqlErrorMessages.SourceNotInitialized),
                    ExportStartCategory);
            }

            while (failure is null)
            {
                // No recorded failure always means the exporter factory returned a usable cursor.
                var activeExporter = exporter!;
                int columnCount;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    columnCount = await activeExporter.StartRowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = Classify(exception, cancellationToken, ReadCategory);
                    break;
                }

                if (columnCount == -1)
                {
                    exportCompleted = true;
                    break;
                }

                if (_options.ExpectedColumnCount is int expectedColumnCount && columnCount != expectedColumnCount)
                {
                    failure = new SourceFailure(
                        new InvalidDataException(
                            string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                PostgreSqlErrorMessages.ColumnCountMismatchFormat,
                                columnCount,
                                expectedColumnCount)),
                        ColumnCountCategory);
                    break;
                }

                T value;
                try
                {
                    value = await _rowReader(activeExporter, columnCount, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = Classify(exception, cancellationToken, CallbackCategory);
                    break;
                }

                rowCount++;
                yield return ProcessingEnvelope<T>.Create(value);
            }
        }
        finally
        {
            var cleanupFailures = await ReleaseResourcesAsync(exportCompleted).ConfigureAwait(false);
            LogOutcome(failure, rowCount, cleanupFailures);
            PostgreSqlFailures.Throw(
                failure?.Exception,
                cleanupFailures,
                PostgreSqlErrorMessages.SourceCleanupFailed,
                PostgreSqlErrorMessages.SourceCleanupOnlyFailed);
        }

        _logger?.LogDebug(
            "PostgreSQL binary COPY OUT source {OperationName} completed after {RowCount} rows.",
            _options.OperationName,
            rowCount);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? starter = null;
        Task task;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = starter.Task;
            }

            task = _disposeTask;
        }

        if (starter is not null)
            _ = RunDisposeAsync(starter);

        return new ValueTask(task);
    }

    private static Task<NpgsqlBinaryExporter> BeginBinaryExportAsync(
        NpgsqlConnection connection,
        string copyToCommand,
        CancellationToken cancellationToken) =>
        connection.BeginBinaryExportAsync(copyToCommand, cancellationToken);

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(NpgsqlBinaryExporter? Exporter, SourceFailure? Failure)> TryStartExportAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var exporter = await _exporterFactory(connection, _copyToCommand, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _exporter, exporter);
            return (exporter, null);
        }
        catch (Exception exception)
        {
            return (null, Classify(exception, cancellationToken, ExportStartCategory));
        }
    }

    private async Task RunDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _initializeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            var cleanupFailures = await ReleaseResourcesAsync(exportCompleted: false).ConfigureAwait(false);
            if (cleanupFailures.Count != 0)
            {
                _logger?.LogWarning(
                    "PostgreSQL binary COPY OUT source {OperationName} cleanup failed; failure category {FailureCategory}.",
                    _options.OperationName,
                    CleanupCategory);
            }

            PostgreSqlFailures.Throw(
                null,
                cleanupFailures,
                PostgreSqlErrorMessages.SourceCleanupFailed,
                PostgreSqlErrorMessages.SourceCleanupOnlyFailed);
        }
        finally
        {
            _initializeGate.Release();
            _initializeGate.Dispose();
        }
    }

    /// <summary>Releases the exporter (when held) and then the connection; every attempt is made.</summary>
    /// <param name="exportCompleted">
    /// <see langword="true"/> when the protocol reached end of stream, so the exporter is disposed gracefully.
    /// Otherwise, including an abandoned enumeration, the export is cancelled before it is disposed. The order is
    /// deliberate: disposing a non-consumed exporter is a graceful drain that lets the server run the export to
    /// completion, which would read the whole remaining result set and can hang disposal for an arbitrarily long
    /// time. An early stop aborts, so this must not be "simplified" into drain-only disposal.
    /// </param>
    /// <returns>The cleanup failures in the order they occurred.</returns>
    private async ValueTask<IReadOnlyList<Exception>> ReleaseResourcesAsync(bool exportCompleted)
    {
        var failures = new List<Exception>();

        var exporter = Interlocked.Exchange(ref _exporter, null);
        if (exporter is not null)
        {
            if (!exportCompleted)
            {
                // Cancel before disposing, otherwise the dispose drains the rest of the export instead of aborting it.
                try
                {
                    await exporter.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                await exporter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static SourceFailure Classify(Exception exception, CancellationToken cancellationToken, string category) =>
        new(
            exception,
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? CancelledCategory
                : category);

    private void LogOutcome(SourceFailure? failure, long rowCount, IReadOnlyList<Exception> cleanupFailures)
    {
        if (failure is { } recorded)
        {
            if (recorded.Category == CancelledCategory)
            {
                _logger?.LogDebug(
                    "PostgreSQL binary COPY OUT source {OperationName} was cancelled after {RowCount} rows.",
                    _options.OperationName,
                    rowCount);
            }
            else
            {
                _logger?.LogError(
                    "PostgreSQL binary COPY OUT source {OperationName} failed after {RowCount} rows; failure category {FailureCategory}.",
                    _options.OperationName,
                    rowCount,
                    recorded.Category);
            }
        }

        if (cleanupFailures.Count != 0)
        {
            _logger?.LogWarning(
                "PostgreSQL binary COPY OUT source {OperationName} cleanup failed after {RowCount} rows; failure category {FailureCategory}.",
                _options.OperationName,
                rowCount,
                CleanupCategory);
        }
    }

    private readonly record struct SourceFailure(Exception Exception, string Category);
}
