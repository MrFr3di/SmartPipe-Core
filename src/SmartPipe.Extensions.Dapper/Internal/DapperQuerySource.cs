#nullable enable

using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Dapper;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Streams the first result set of one explicit-SQL Dapper query per run.</summary>
internal sealed class DapperQuerySource<T> : IPipelineSource<T>
{
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> _acquireConnection;
    private readonly PipelineActivationContext _context;
    private readonly string _sql;
    private readonly DapperQueryOptionsSnapshot _options;
    private readonly Func<PipelineActivationContext, object?>? _parametersFactory;
    private readonly Func<DbDataReader, T>? _rowMapper;
    private readonly ILogger<DapperQuerySource<T>>? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly DapperSingleFlightDisposal _disposal = new();

    private DapperConnectionLease? _lease;
    private DbDataReader? _reader;
    private object? _parameters;
    private bool _initialized;
    private bool _disposed;

    internal DapperQuerySource(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> acquireConnection,
        PipelineActivationContext context,
        string sql,
        DapperQueryOptionsSnapshot options,
        Func<PipelineActivationContext, object?>? parametersFactory,
        Func<DbDataReader, T>? rowMapper,
        ILogger<DapperQuerySource<T>>? logger,
        CancellationToken activationCancellationToken)
    {
        _acquireConnection = acquireConnection;
        _context = context;
        _sql = sql;
        _options = options;
        _parametersFactory = parametersFactory;
        _rowMapper = rowMapper;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
    }

    /// <summary>Acquires the single per-run connection and materializes the run parameters.</summary>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            using var linkedCancellation = DapperCancellation.CreateLinked(
                _activationCancellationToken,
                ct,
                out var cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var lease = await DapperConnectionLease
                .OpenAsync(_acquireConnection, _context, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                _parameters = _parametersFactory?.Invoke(_context);
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception>();
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }

                DapperCleanup.ThrowPrimaryFirst(
                    primaryFailure,
                    cleanupFailures,
                    "The Dapper query parameter factory failed and cleanup also failed.");
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            _lease = lease;
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>Streams the first result set and releases the run resources exactly once.</summary>
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linkedCancellation = DapperCancellation.CreateLinked(
            _activationCancellationToken,
            ct,
            out var cancellationToken);
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var lease = _lease
            ?? throw new InvalidOperationException("The Dapper query source has no connection left for this run.");
        var startedTimestamp = _context.TimeProvider.GetTimestamp();
        var rowCount = 0L;
        Exception? primaryFailure = null;

        try
        {
            DbDataReader? reader = null;
            try
            {
                reader = await lease.Connection
                    .ExecuteReaderAsync(CreateCommandDefinition(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }

            if (reader is not null)
            {
                _reader = reader;
                var map = _rowMapper ?? SqlMapper.GetRowParser<T>(reader);
                while (true)
                {
                    bool hasRow;
                    try
                    {
                        hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        primaryFailure = exception;
                        break;
                    }

                    if (!hasRow)
                        break;

                    T value;
                    try
                    {
                        value = map(reader);
                    }
                    catch (Exception exception)
                    {
                        primaryFailure = exception;
                        break;
                    }

                    rowCount++;
                    yield return ProcessingEnvelope<T>.Create(value);
                }
            }
        }
        finally
        {
            var cleanupFailures = await ReleaseAsync().ConfigureAwait(false);
            LogOutcome(startedTimestamp, rowCount, primaryFailure);
            DapperCleanup.ThrowPrimaryFirst(
                primaryFailure,
                cleanupFailures,
                "The Dapper query failed and cleanup also failed.");
            if (primaryFailure is not null)
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>Releases the run resources without executing any SQL.</summary>
    public ValueTask DisposeAsync() => _disposal.DisposeAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        await _initializeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            var cleanupFailures = await ReleaseAsync().ConfigureAwait(false);
            DapperCleanup.ThrowPrimaryFirst(null, cleanupFailures, "Disposing the Dapper query source failed.");
        }
        finally
        {
            _initializeGate.Release();
            _initializeGate.Dispose();
        }
    }

    private async ValueTask<IReadOnlyList<Exception>> ReleaseAsync()
    {
        var failures = new List<Exception>();

        var reader = Interlocked.Exchange(ref _reader, null);
        if (reader is not null)
        {
            try
            {
                // Dapper's wrapped reader owns the command it executed: its synchronous disposal releases
                // the reader first and then that command. The asynchronous path releases only the reader,
                // so the synchronous release is the exactly-once path for both.
                reader.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private CommandDefinition CreateCommandDefinition(CancellationToken cancellationToken) =>
        new(
            _sql,
            _parameters,
            transaction: null,
            _options.CommandTimeoutSeconds,
            _options.CommandType,
            _options.Flags,
            cancellationToken);

    private void LogOutcome(long startedTimestamp, long rowCount, Exception? failure)
    {
        if (_logger is null)
            return;

        _logger.LogInformation(
            "Dapper query {OperationName} for pipeline {PipelineKey} run {RunId} {Outcome} after {DurationMilliseconds} ms with {RowCount} rows.",
            _options.OperationName,
            _context.PipelineKey.Value,
            _context.RunId,
            DescribeOutcome(failure),
            _context.TimeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            rowCount);
    }

    private static string DescribeOutcome(Exception? failure)
    {
        if (failure is null)
            return "succeeded";

        if (failure is OperationCanceledException)
            return "cancelled";

        return "failed";
    }
}
