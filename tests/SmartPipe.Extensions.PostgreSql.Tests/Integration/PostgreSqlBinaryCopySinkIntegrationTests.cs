using System.Transactions;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Real-server scenarios for the binary <c>COPY … FROM STDIN (FORMAT BINARY)</c> batch sink.
/// </summary>
/// <remarks>
/// One batch envelope means one complete PostgreSQL COPY. Every scenario owns a fresh table inside the fixture's
/// private schema, so the class is parallel-safe with the other integration classes.
/// </remarks>
public sealed class PostgreSqlBinaryCopySinkIntegrationTests(PostgreSqlIntegrationDatabase database)
    : IClassFixture<PostgreSqlIntegrationDatabase>
{
    private static readonly CopyRow FullRow = new(
        Id: 1,
        Name: "alpha",
        Amount: 1234.5678m,
        Day: new DateOnly(2024, 2, 29),
        AtTime: new TimeOnly(13, 45, 7),
        Stamp: new DateTime(2024, 2, 29, 13, 45, 7, DateTimeKind.Unspecified),
        Instant: new DateTime(2024, 2, 29, 13, 45, 7, DateTimeKind.Utc),
        Document: "{\"a\":1}");

    [Fact]
    public async Task CopyIn_EmptyBatch_IsASuccessfulNoOp()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await sink.InitializeAsync(ct);

        await sink.WriteAsync(Batch(), ct);
        await sink.WriteAsync(ProcessingEnvelope<IReadOnlyList<CopyRow>>.Create(null!), ct);

        Assert.Equal(0L, await database.CountRowsAsync(table, ct));
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_OneRow_CompletesTheCopyAndIsVisibleToASeparateConnection()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await sink.InitializeAsync(ct);

        await sink.WriteAsync(Batch(FullRow), ct);

        // Immediately, on a separate connection, and before disposal: a successful write already completed the COPY.
        var rows = await ReadRowsAsync(table, ct);
        var actual = Assert.Single(rows);
        Assert.Equal(FullRow.Name, actual.Name);
        Assert.Equal(FullRow.Amount, actual.Amount);
        Assert.Equal(FullRow.Day, actual.Day);
        Assert.Equal(FullRow.AtTime, actual.AtTime);
        Assert.Equal<DateTime?>(FullRow.Stamp, actual.Stamp);
        Assert.Equal<DateTime?>(FullRow.Instant, actual.Instant);
        Assert.Equal("{\"a\": 1}", actual.Document);
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await sink.DisposeAsync();
        Assert.Equal(1L, await database.CountRowsAsync(table, ct));
    }

    [Fact]
    public async Task CopyIn_ExactMaxRowsPerBatch_IsAccepted()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 4 });
        await sink.InitializeAsync(ct);

        await sink.WriteAsync(Batch(Row(1), Row(2), Row(3), Row(4)), ct);

        Assert.Equal(4L, await database.CountRowsAsync(table, ct));
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_MaxRowsPerBatchPlusOne_IsRejectedBeforeAnyCopyStarts()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 4 });
        await sink.InitializeAsync(ct);

        // A pre-existing row keeps the row-count proof honest: the rejected batch must not touch the table at all.
        await database.ExecuteAsync($"INSERT INTO {table} (id, name) VALUES (99, 'pre-existing')", ct);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1), Row(2), Row(3), Row(4), Row(5)), ct);
        });

        Assert.Contains(PostgreSqlErrorMessages.BatchTooLarge, failure.Message, StringComparison.Ordinal);
        Assert.Equal(1L, await database.CountRowsAsync(table, ct));
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_JsonbColumn_RequiresTheExplicitProviderType()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        // The naive writer states no provider type for the jsonb column, so the server rejects the binary value.
        await using (var naive = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await importer.WriteNullAsync(token);
                await importer.WriteNullAsync(token);
                await importer.WriteNullAsync(token);
                await importer.WriteNullAsync(token);
                await importer.WriteNullAsync(token);
                await importer.WriteNullAsync(token);
                await importer.WriteNullAsync(token);
                await importer.WriteAsync(row.Document ?? string.Empty, token);
            }))
        {
            await naive.InitializeAsync(ct);
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await naive.WriteAsync(Batch(FullRow), ct);
            });

            Assert.IsNotType<OperationCanceledException>(PostgreSqlFailureAssert.Primary(failure));
            Assert.Equal(0L, await database.CountRowsAsync(table, ct));
            await naive.DisposeAsync();
        }

        // The same value succeeds as soon as the provider type is stated explicitly.
        await using var explicitSink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await explicitSink.InitializeAsync(ct);
        await explicitSink.WriteAsync(Batch(FullRow), ct);

        var rows = await ReadRowsAsync(table, ct);
        Assert.Equal("{\"a\": 1}", Assert.Single(rows).Document);
        await explicitSink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_NullFields_RoundTripAsNull()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await sink.InitializeAsync(ct);
        await sink.WriteAsync(Batch(new CopyRow(7, null, null, null, null, null, null, null)), ct);
        await sink.DisposeAsync();

        var actual = Assert.Single(await ReadRowsAsync(table, ct));
        Assert.Equal<int?>(7, actual.Id);
        Assert.Null(actual.Name);
        Assert.Null(actual.Amount);
        Assert.Null(actual.Day);
        Assert.Null(actual.AtTime);
        Assert.Null(actual.Stamp);
        Assert.Null(actual.Instant);
        Assert.Null(actual.Document);
    }

    [Fact]
    public async Task CopyIn_RowWriterFailure_IsPropagatedAndLeavesZeroRows()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var expected = new CopyInScenarioException("the row writer rejected row 3");
        var attempts = 0;
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                attempts++;
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
                if (row.Id == 3)
                    throw expected;
            });
        await sink.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1), Row(2), Row(3), Row(4)), ct);
        });

        Assert.Same(expected, PostgreSqlFailureAssert.Primary(failure));

        // Each row is attempted exactly once, so the failed COPY is neither retried nor partially committed.
        Assert.Equal(3, attempts);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_CancellationBeforeCopyStart_WritesNothing()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await sink.InitializeAsync(ct);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1)), cancelled.Token);
        });

        Assert.NotNull(failure);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_CancellationBeforeInitialize_IsRejectedAndWritesNothing()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            activationToken: cancelled.Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await sink.InitializeAsync();
        });

        Assert.NotNull(failure);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_CancellationWhileWriting_AbortsTheCopy()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var written = 0;
        var rows = Enumerable.Range(1, 512).Select(Row).ToArray();
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
                if (++written == 10)
                    cancellation.Cancel();
            });
        await sink.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await sink.WriteAsync(Batch(rows), cancellation.Token);
        });

        Assert.NotNull(failure);

        // The unfinished COPY was aborted by importer disposal, so no row of the cancelled batch is visible.
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_CancellationImmediatelyBeforeCompletion_AbortsTheCopy()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var written = 0;
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);

                // Every row has been written; the cancellation therefore lands between the last row and CompleteAsync.
                if (++written == 3)
                    cancellation.Cancel();
            });
        await sink.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1), Row(2), Row(3)), cancellation.Token);
        });

        Assert.NotNull(failure);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_ExternalCancellationWhileGated_AbortsTheCopyAndLeavesZeroRows()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var entered = PostgreSqlGate.Create();
        var releaseGate = PostgreSqlGate.Create();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rows = Enumerable.Range(1, 1000).Select(Row).ToArray();
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
                if (row.Id == 1)
                {
                    entered.TrySetResult();
                    await releaseGate.Task.WaitAsync(token);
                }
            });
        await sink.InitializeAsync(ct);

        var write = Task.Run(async () => await sink.WriteAsync(Batch(rows), cancellation.Token), ct);
        await entered.Task.WaitAsync(PostgreSqlTestGuard.Short, ct);

        // The COPY really is in flight on the server while the batch is still incomplete.
        Assert.Equal(1L, await database.CountActiveCopyBackendsAsync(ct));

        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await write.WaitAsync(PostgreSqlTestGuard.Short, ct);
        });

        Assert.NotNull(failure);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        var stopped = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountActiveCopyBackendsAsync(token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(stopped, "The aborted COPY was still running on the server after the batch failed.");

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_DisposeAsync_PerformsCleanupOnlyWithoutBusinessWork()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        // This COPY command can never run: the table does not exist. Disposal must therefore succeed, because it
        // performs no COPY work of its own.
        var missingTable = database.NewTable("missing");
        await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(missingTable));
        await sink.InitializeAsync(ct);

        await sink.DisposeAsync();
        await sink.DisposeAsync();

        var failure = await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1)), ct);
        });

        Assert.NotNull(failure);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));
    }

    [Fact]
    public async Task CopyIn_AfterASuccessfulBatch_DisposeAddsNoWork()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await sink.InitializeAsync(ct);
        await sink.WriteAsync(Batch(Row(1), Row(2)), ct);
        Assert.Equal(2L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();

        // Disposal is cleanup only: no completion, no extra COPY and no extra row.
        Assert.Equal(2L, await database.CountRowsAsync(table, ct));
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));
    }

    [Fact]
    public async Task CopyIn_EachBatchIsAFreshCopyOperation()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var entered = PostgreSqlGate.Create();
        var releaseGate = PostgreSqlGate.Create();
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
                if (row.Id == 1)
                {
                    entered.TrySetResult();
                    await releaseGate.Task;
                }
            });
        await sink.InitializeAsync(ct);

        var first = Task.Run(async () => await sink.WriteAsync(Batch(Row(1)), ct), ct);
        await entered.Task.WaitAsync(PostgreSqlTestGuard.Short, ct);
        Assert.Equal(1L, await database.CountActiveCopyBackendsAsync(ct));

        releaseGate.TrySetResult();
        await first.WaitAsync(PostgreSqlTestGuard.Short, ct);

        var closed = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountActiveCopyBackendsAsync(token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(closed, "No COPY may stay open between two batches.");
        Assert.Equal(1L, await database.CountRowsAsync(table, ct));

        // The second batch is its own COPY: it is neither a continuation nor a replay of the first one.
        await sink.WriteAsync(Batch(Row(2)), ct);
        Assert.Equal(2L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_EarlierSuccessfulBatchStaysCommittedWhenALaterBatchFails()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var expected = new CopyInScenarioException("the later batch failed");
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                if (row.Id == 4)
                    throw expected;
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
            });
        await sink.InitializeAsync(ct);

        await sink.WriteAsync(Batch(Row(1), Row(2)), ct);
        Assert.Equal(2L, await database.CountRowsAsync(table, ct));

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await sink.WriteAsync(Batch(Row(3), Row(4), Row(5)), ct);
        });

        Assert.Same(expected, PostgreSqlFailureAssert.Primary(failure));

        // Batch one stays committed and batch two is entirely absent.
        var ids = (await ReadRowsAsync(table, ct)).Select(row => row.Id ?? -1).ToArray();
        Assert.Equal(new[] { 1, 2 }, ids);

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_FailedBatchIsNotRetriedAndTheNextBatchSucceeds()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var attempts = 0;
        var failNext = true;
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                attempts++;
                if (failNext)
                    throw new CopyInScenarioException("first attempt fails");
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
            });
        await sink.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1), Row(2)), ct);
        });

        Assert.IsType<CopyInScenarioException>(PostgreSqlFailureAssert.Primary(failure));
        Assert.Equal(1, attempts);
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        failNext = false;
        await sink.WriteAsync(Batch(Row(3)), ct);

        // No internal retry happened, and the connection is still usable for the next, independent batch.
        Assert.Equal(2, attempts);
        var ids = (await ReadRowsAsync(table, ct)).Select(row => row.Id ?? -1).ToArray();
        Assert.Equal(new[] { 3 }, ids);

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_PrimaryFailureIsPreservedAndNotReplacedByCleanup()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        var expected = new CopyInScenarioException("the batch failed before completion");
        await using var sink = await CreateSinkAsync(
            PostgreSqlCopyShape.CopyFromCommand(table),
            rowWriter: async (importer, row, token) =>
            {
                await PostgreSqlCopyShape.WriteRowAsync(importer, row, token);
                throw expected;
            });
        await sink.InitializeAsync(ct);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await sink.WriteAsync(Batch(Row(1)), ct);
        });

        // The exact instance is surfaced first, so the failure keeps its identity and stack trace.
        Assert.Same(expected, PostgreSqlFailureAssert.Primary(failure));
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_AmbientTransaction_IsRejectedBeforeTheCopyStarts()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));
        await sink.InitializeAsync(ct);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await sink.WriteAsync(Batch(Row(1)), ct);
            });

            Assert.Contains(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message, StringComparison.Ordinal);
        }

        // A scope entered after activation is rejected before the COPY protocol starts, so the table stays empty.
        Assert.Equal(0L, await database.CountRowsAsync(table, ct));
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));

        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CopyIn_AmbientTransaction_IsRejectedBeforeConnectionAcquisition()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var sink = await CreateSinkAsync(PostgreSqlCopyShape.CopyFromCommand(table));

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await sink.InitializeAsync(ct);
            });

            Assert.Contains(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, await database.CountRowsAsync(table, ct));
    }

    private static CopyRow Row(int id) => new(id, $"row-{id}", null, null, null, null, null, null);

    private static ProcessingEnvelope<IReadOnlyList<CopyRow>> Batch(params CopyRow[] rows) =>
        ProcessingEnvelope<IReadOnlyList<CopyRow>>.Create(rows);

    private async Task<string> CreateTableAsync(CancellationToken ct)
    {
        var table = database.NewTable();
        await database.ExecuteAsync($"CREATE TABLE {table} ({PostgreSqlCopyShape.TableDefinition})", ct);
        return table;
    }

    private async Task<List<CopyRow>> ReadRowsAsync(string table, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"SELECT {PostgreSqlCopyShape.ColumnList} FROM {table} ORDER BY id",
            connection);

        var rows = new List<CopyRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CopyRow(
                reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<TimeOnly>(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    private async Task<IPipelineSink<IReadOnlyList<CopyRow>>> CreateSinkAsync(
        string copyFromCommand,
        PostgreSqlBinaryCopySinkOptions? options = null,
        Func<NpgsqlBinaryImporter, CopyRow, CancellationToken, ValueTask>? rowWriter = null,
        NpgsqlDataSource? dataSource = null,
        CancellationToken activationToken = default)
    {
        var descriptor = PostgreSqlPipelineComponents.BinaryCopyBatchSink(
            dataSource ?? database.DataSource,
            copyFromCommand,
            rowWriter ?? PostgreSqlCopyShape.WriteRowAsync,
            options ?? new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 1024 });

        return await PostgreSqlComponentActivation.ActivateAsync(descriptor, activationToken);
    }

    private sealed class CopyInScenarioException(string message) : InvalidOperationException(message);
}
