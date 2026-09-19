#nullable enable

using System.Data.Common;
using System.Runtime.ExceptionServices;
using Dapper;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Executes exactly one explicit-SQL Dapper command per envelope.</summary>
internal sealed class DapperCommandSink<T> : IPipelineSink<T>
{
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> _acquireConnection;
    private readonly PipelineActivationContext _context;
    private readonly string _sql;
    private readonly DapperSinkOptionsSnapshot _options;
    private readonly Func<ProcessingEnvelope<T>, object?>? _parameterFactory;
    private readonly ILogger<DapperCommandSink<T>>? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _disposeSync = new();

    private DapperConnectionLease? _lease;
    private bool _initialized;
    private bool _disposed;
    private Task? _disposeTask;

    internal DapperCommandSink(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> acquireConnection,
        PipelineActivationContext context,
        string sql,
        DapperSinkOptionsSnapshot options,
        Func<ProcessingEnvelope<T>, object?>? parameterFactory,
        ILogger<DapperCommandSink<T>>? logger,
        CancellationToken activationCancellationToken)
    {
        _acquireConnection = acquireConnection;
        _context = context;
        _sql = sql;
        _options = options;
        _parameterFactory = parameterFactory;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
    }

    /// <summary>Acquires the single per-run connection.</summary>
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

    /// <summary>Executes the configured command once for the envelope.</summary>
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
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
                    "The Dapper command sink is not initialized. Call InitializeAsync before writing.");

            var parameters = _parameterFactory is null ? envelope.Payload : _parameterFactory(envelope);
            var startedTimestamp = _context.TimeProvider.GetTimestamp();
            try
            {
                var affectedRows = await lease.Connection
                    .ExecuteAsync(CreateCommandDefinition(parameters, cancellationToken))
                    .ConfigureAwait(false);
                LogOutcome(startedTimestamp, affectedRows, failure: null);
            }
            catch (Exception failure)
            {
                LogOutcome(startedTimestamp, 0, failure);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Releases the run connection without executing any SQL.</summary>
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

            DapperCleanup.ThrowPrimaryFirst(null, cleanupFailures, "Disposing the Dapper command sink failed.");
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private CommandDefinition CreateCommandDefinition(object? parameters, CancellationToken cancellationToken) =>
        new(
            _sql,
            parameters,
            transaction: null,
            _options.CommandTimeoutSeconds,
            _options.CommandType,
            _options.Flags,
            cancellationToken);

    private void LogOutcome(long startedTimestamp, int affectedRows, Exception? failure)
    {
        if (_logger is null)
            return;

        _logger.LogInformation(
            "Dapper command {OperationName} for pipeline {PipelineKey} run {RunId} {Outcome} after {DurationMilliseconds} ms with {AffectedRows} affected rows.",
            _options.OperationName,
            _context.PipelineKey.Value,
            _context.RunId,
            DescribeOutcome(failure),
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
