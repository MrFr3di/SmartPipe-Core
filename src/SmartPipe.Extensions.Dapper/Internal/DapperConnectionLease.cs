#nullable enable

using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Owns exactly one per-run connection and disposes it exactly once.</summary>
/// <remarks>
/// The lease opens the borrowed connection when the provider returns it closed and keeps the connection
/// alive until a single-flight <see cref="DisposeAsync"/> releases it. Repeated disposal returns the same
/// completion and never retries a failed release.
/// </remarks>
internal sealed class DapperConnectionLease : IAsyncDisposable
{
    private readonly DapperSingleFlightDisposal _disposal = new();

    private DbConnection? _connection;

    private DapperConnectionLease(DbConnection connection) => _connection = connection;

    /// <summary>Gets the live connection of this lease.</summary>
    internal DbConnection Connection =>
        Volatile.Read(ref _connection) ?? throw new ObjectDisposedException(nameof(DapperConnectionLease));

    /// <summary>Acquires one fresh open connection for a single run.</summary>
    internal static async ValueTask<DapperConnectionLease> OpenAsync(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> acquireConnection,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        var connection = await acquireConnection(context, cancellationToken).ConfigureAwait(false);
        if (connection is null)
            throw new InvalidOperationException("The Dapper connection factory returned no connection.");

        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            DapperCleanup.ThrowPrimaryFirst(
                primaryFailure,
                cleanupFailures,
                "Opening the Dapper connection failed and cleanup also failed.");
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        return new(connection);
    }

    /// <summary>Releases the connection exactly once.</summary>
    public ValueTask DisposeAsync() => _disposal.DisposeAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
            return;

        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
