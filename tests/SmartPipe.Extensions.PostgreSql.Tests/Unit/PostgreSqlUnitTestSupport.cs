using Npgsql;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Server-free endpoints, COPY statements and row callbacks shared by the PostgreSQL unit tests.</summary>
/// <remarks>
/// <para>
/// Nothing here reaches a server: the endpoint is loopback port 1, which is never a PostgreSQL server, and
/// <see cref="NpgsqlDataSource.Create(string)"/> does not open a connection until it is asked to.
/// </para>
/// <para>
/// An <see cref="NpgsqlDataSource"/> cannot be subclassed to record open attempts: every abstract member it declares
/// (<c>OwnsConnectors</c>, <c>TryGetIdleConnector</c>, <c>Get</c>, <c>OpenNewConnector</c>, <c>Return</c>,
/// <c>Statistics</c>) is <c>internal</c>, so no assembly outside Npgsql can derive from it, and
/// <see cref="NpgsqlConnection"/> is sealed. "Zero provider calls" is therefore established by the reachability
/// control in <see cref="AssertProviderFailureAsync"/>: a genuine open attempt against this endpoint always fails
/// loudly with a provider exception, so observing the provider's own ambient-transaction rejection instead proves that
/// no open was attempted.
/// </para>
/// </remarks>
internal static class PostgreSqlUnitTestSupport
{
    /// <summary>Loopback port 1: never a PostgreSQL server, refused in well under the one second connection timeout.</summary>
    internal const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Username=smartpipe;Database=smartpipe;Timeout=1";

    internal const string CopyToCommand = "COPY (SELECT 1) TO STDOUT (FORMAT BINARY)";

    internal const string CopyFromCommand = "COPY public.smartpipe_probe (id) FROM STDIN (FORMAT BINARY)";

    /// <summary>Reads exactly one row; the returned column count keeps the callback argument meaningful.</summary>
    internal static Func<NpgsqlBinaryExporter, int, CancellationToken, ValueTask<int>> RowReader { get; } =
        static (_, columnCount, _) => ValueTask.FromResult(columnCount);

    /// <summary>Writes exactly one row.</summary>
    internal static Func<NpgsqlBinaryImporter, int, CancellationToken, ValueTask> RowWriter { get; } =
        static (_, _, _) => ValueTask.CompletedTask;

    /// <summary>The cancellation token of the running test.</summary>
    internal static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    /// <summary>Creates a data source that never connects until an operation asks it to.</summary>
    internal static NpgsqlDataSource CreateUnreachableDataSource() =>
        NpgsqlDataSource.Create(UnreachableConnectionString);

    /// <summary>Creates a real connection object that was never opened and therefore performed no I/O.</summary>
    internal static NpgsqlConnection CreateUnopenedConnection() => new(UnreachableConnectionString);

    /// <summary>
    /// Proves the reachability control for <paramref name="dataSource"/>: an open attempt fails with a provider
    /// failure, and the borrowed data source is still alive, because a disposed data source fails before any provider
    /// work.
    /// </summary>
    /// <param name="dataSource">The borrowed data source to probe.</param>
    /// <returns>The observed provider failure.</returns>
    internal static async Task<Exception> AssertProviderFailureAsync(NpgsqlDataSource dataSource)
    {
        var failure = await Record.ExceptionAsync(
            async () => await dataSource.OpenConnectionAsync(TestCancellation));

        Assert.NotNull(failure);
        // A disposed data source fails before any provider work.
        Assert.IsNotType<ObjectDisposedException>(failure);
        // A real open attempt always surfaces as the provider's own failure, never as an InvalidOperationException.
        Assert.IsAssignableFrom<NpgsqlException>(failure);
        return failure;
    }
}
