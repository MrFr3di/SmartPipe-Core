using System.Transactions;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Real-server scenarios that need the components' internal test seams.
/// </summary>
/// <remarks>
/// The seams exist so that provider interactions can be observed or replaced without changing product behaviour:
/// the connection/exporter/importer factories of the COPY components and the LISTEN/UNLISTEN seams of the
/// notification source. The two invariants covered here are "an already-cancelled or ambient-transaction run never
/// reaches the pool / never starts COPY" and "a cleanup failure never replaces the primary failure".
/// </remarks>
public sealed class PostgreSqlComponentSeamIntegrationTests(PostgreSqlIntegrationDatabase database)
    : IClassFixture<PostgreSqlIntegrationDatabase>
{
    [Fact]
    public async Task CopyOut_CancelledRun_DoesNotOpenAConnection()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var connectionOpens = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await using var source = new PostgreSqlBinaryCopySource<CopyRow>(
            database.DataSource,
            PostgreSqlCopyShape.CopyToCommand(table),
            PostgreSqlCopyShape.ReadRowAsync,
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = PostgreSqlCopyShape.ColumnCount }),
            logger: null,
            activationCancellationToken: cancelled.Token,
            connectionFactory: async token =>
            {
                connectionOpens++;
                return await database.DataSource.OpenConnectionAsync(token);
            });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await source.InitializeAsync();
        });

        Assert.NotNull(failure);
        Assert.Equal(0, connectionOpens);
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_CleanupFailure_NeverReplacesThePrimaryFailure()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 100) g",
            ct);

        var expected = new SeamScenarioException("the row reader failed first");
        var listenPid = 0;
        await using var source = new PostgreSqlBinaryCopySource<CopyRow>(
            database.DataSource,
            PostgreSqlCopyShape.CopyToCommand(table),
            rowReader: async (exporter, columnCount, token) =>
            {
                var row = await PostgreSqlCopyShape.ReadRowAsync(exporter, columnCount, token);

                // Killing the activation backend makes every cleanup step on the exporter fail, while the failure the
                // row reader throws stays the primary failure of the run.
                await database.TerminateBackendAsync(listenPid, token);
                throw expected;
            },
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = PostgreSqlCopyShape.ColumnCount }),
            logger: null,
            activationCancellationToken: CancellationToken.None,
            connectionFactory: async token =>
            {
                var connection = await database.DataSource.OpenConnectionAsync(token);
                listenPid = await PostgreSqlIntegrationDatabase.ScalarOnConnectionAsync<int>(
                    connection,
                    "SELECT pg_backend_pid()",
                    token);
                return connection;
            });

        await source.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, ct);
        });

        // Npgsql tolerates disposing an exporter and a connection whose session was killed, so this seam can force the
        // primary failure deterministically but not a cleanup failure. The invariant that must hold either way is that
        // the primary failure stays first: when cleanup does fail, the policy combines it behind the primary failure.
        Assert.Same(expected, PostgreSqlFailureAssert.Primary(failure));
        if (failure is AggregateException combined)
        {
            Assert.Same(expected, combined.InnerExceptions[0]);
            PostgreSqlFailureAssert.ContainsMessage(failure, PostgreSqlErrorMessages.SourceCleanupFailed);
        }

        await DisposeToleratingCleanupFailureAsync(source, expected);
    }

    [Fact]
    public async Task CopyIn_CancelledRun_DoesNotOpenAConnection()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var connectionOpens = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await using var sink = new PostgreSqlBinaryCopyBatchSink<CopyRow>(
            database.DataSource,
            PostgreSqlCopyShape.CopyFromCommand(table),
            PostgreSqlCopyShape.WriteRowAsync,
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 8 }),
            logger: null,
            activationCancellationToken: cancelled.Token,
            connectionFactory: async (dataSource, token) =>
            {
                connectionOpens++;
                return await dataSource.OpenConnectionAsync(token);
            });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await sink.InitializeAsync();
        });

        Assert.NotNull(failure);
        Assert.Equal(0, connectionOpens);

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_AmbientTransactionAfterActivation_IsRejectedBeforeTheCopyProtocolStarts()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var importsStarted = 0;
        await using var sink = new PostgreSqlBinaryCopyBatchSink<CopyRow>(
            database.DataSource,
            PostgreSqlCopyShape.CopyFromCommand(table),
            PostgreSqlCopyShape.WriteRowAsync,
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 8 }),
            logger: null,
            activationCancellationToken: CancellationToken.None,
            importerFactory: (connection, command, token) =>
            {
                importsStarted++;
                return connection.BeginBinaryImportAsync(command, token);
            });

        await sink.InitializeAsync(ct);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await sink.WriteAsync(ProcessingEnvelope<IReadOnlyList<CopyRow>>.Create(
                    new[] { new CopyRow(1, "row-1", null, null, null, null, null, null) }), ct);
            });

            Assert.Contains(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message, StringComparison.Ordinal);
        }

        // Not a single COPY protocol start happened, and the table stayed empty.
        Assert.Equal(0, importsStarted);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_CleanupFailure_NeverReplacesThePrimaryFailure()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var expected = new SeamScenarioException("the row writer failed first");
        var sinkPid = 0;
        await using var sink = new PostgreSqlBinaryCopyBatchSink<CopyRow>(
            database.DataSource,
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);

                // Killing the batch backend makes the aborting importer disposal fail, while the row writer's own
                // failure stays the primary failure of the batch.
                await database.TerminateBackendAsync(sinkPid, token);
                throw expected;
            },
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 8 }),
            logger: null,
            activationCancellationToken: CancellationToken.None,
            connectionFactory: async (dataSource, token) =>
            {
                var connection = await dataSource.OpenConnectionAsync(token);
                sinkPid = await PostgreSqlIntegrationDatabase.ScalarOnConnectionAsync<int>(
                    connection,
                    "SELECT pg_backend_pid()",
                    token);
                return connection;
            });

        await sink.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await sink.WriteAsync(ProcessingEnvelope<IReadOnlyList<CopyRow>>.Create(
                new[] { new CopyRow(1, "row-1", null, null, null, null, null, null) }), ct);
        });

        // See the COPY OUT scenario: the seam forces the primary failure deterministically, and a cleanup failure is
        // provider-dependent. The primary failure must stay first whenever cleanup does add a failure.
        Assert.Same(expected, PostgreSqlFailureAssert.Primary(failure));
        if (failure is AggregateException combined)
        {
            Assert.Same(expected, combined.InnerExceptions[0]);
            PostgreSqlFailureAssert.ContainsMessage(failure, PostgreSqlErrorMessages.SinkCleanupFailed);
        }

        await DisposeToleratingCleanupFailureAsync(sink, expected);
    }

    [Fact]
    public async Task Notify_AmbientTransaction_IsRejectedBeforeTheConnectionIsOpened()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var channel = database.NewChannel();
        var listen = database.CreateListenerDataSource(out var applicationName);

        await using var source = new PostgreSqlNotificationSource(
            listen,
            PostgreSqlChannelSet.Create([channel]),
            PostgreSqlNotificationSourceOptionsSnapshot.Create(new PostgreSqlNotificationSourceOptions()),
            logger: null,
            activationCancellationToken: CancellationToken.None);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await source.InitializeAsync(ct);
            });

            Assert.Contains(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message, StringComparison.Ordinal);
        }

        // The rejection happened before the first provider call, so no dedicated LISTEN connection was created.
        Assert.Equal(0L, await database.CountBackendsAsync(applicationName, ct));

        await source.DisposeAsync();
    }

    private async Task<string> CreateTableAsync(CancellationToken ct)
    {
        var table = database.NewTable();
        await database.ExecuteAsync($"CREATE TABLE {table} ({PostgreSqlCopyShape.TableDefinition})", ct);
        return table;
    }

    /// <summary>
    /// Disposes a component whose connection was terminated. Cleanup may legitimately fail in that situation; what
    /// matters is that the cleanup can never resurface the primary failure inside this scenario, and that it is never
    /// a cancellation.
    /// </summary>
    private static async Task DisposeToleratingCleanupFailureAsync(IAsyncDisposable component, Exception primaryFailure)
    {
        try
        {
            await component.DisposeAsync();
        }
        catch (Exception failure)
        {
            Assert.NotSame(primaryFailure, PostgreSqlFailureAssert.Primary(failure));
            Assert.IsNotType<OperationCanceledException>(PostgreSqlFailureAssert.Primary(failure));
        }
    }

    private sealed class SeamScenarioException(string message) : InvalidOperationException(message);
}
