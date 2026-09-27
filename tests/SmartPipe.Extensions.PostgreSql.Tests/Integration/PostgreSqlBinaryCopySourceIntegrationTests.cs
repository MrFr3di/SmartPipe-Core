using System.Net.Sockets;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Real-server scenarios for the binary <c>COPY … TO STDOUT (FORMAT BINARY)</c> source.
/// </summary>
/// <remarks>
/// Each scenario creates its own table inside the fixture's private schema and its own source instance, so this class
/// is parallel-safe with the other integration classes. The row callback is
/// <see cref="PostgreSqlCopyShape.ReadRowAsync"/>, which asserts the column count contract for every row.
/// </remarks>
public sealed class PostgreSqlBinaryCopySourceIntegrationTests(PostgreSqlIntegrationDatabase database)
    : IClassFixture<PostgreSqlIntegrationDatabase>
{
    /// <summary>Deliberately nothing listens on this loopback port, so activation must fail with a provider error.</summary>
    private const int UnreachablePort = 1;

    private static readonly CopyRow OneFullRow = new(
        Id: 1,
        Name: "alpha",
        Amount: 1234.5678m,
        Day: new DateOnly(2024, 2, 29),
        AtTime: new TimeOnly(13, 45, 7),
        Stamp: new DateTime(2024, 2, 29, 13, 45, 7, DateTimeKind.Unspecified),
        Instant: new DateTime(2024, 2, 29, 13, 45, 7, DateTimeKind.Utc),
        Document: "{\"a\":1}");

    [Fact]
    public async Task CopyOut_ZeroRows_CompletesWithoutEnvelopes()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        await using var source = await CreateSourceAsync(PostgreSqlCopyShape.CopyToCommand(table));
        await source.InitializeAsync(ct);

        Assert.Equal(0, await PostgreSqlEnumeration.CountAsync(source, ct));

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_OneRow_ReadsEveryMappedType()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await SeedRowAsync(table, OneFullRow, ct);

        var row = await ReadSingleRowAsync(table, ct);

        Assert.Equal(OneFullRow.Id, row.Id);
        Assert.Equal(OneFullRow.Name, row.Name);
        Assert.Equal(OneFullRow.Amount, row.Amount);
        Assert.Equal(OneFullRow.Day, row.Day);
        Assert.Equal(OneFullRow.AtTime, row.AtTime);
        Assert.Equal(OneFullRow.Stamp, row.Stamp);
        Assert.Equal(OneFullRow.Instant, row.Instant);

        // Npgsql 10.0.3 chooses the DateTimeKind it reports for timestamptz (observed: Unspecified for the exact UTC
        // instant). The component contract is that the instant is passed through untouched, so the scenario asserts
        // the instant plus the fact that it is never shifted into local time.
        Assert.NotEqual(DateTimeKind.Local, row.Instant!.Value.Kind);
        Assert.Equal("{\"a\": 1}", row.Document);
    }

    [Fact]
    public async Task CopyOut_OneHundredRows_ReadsEveryRowInOrder()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 100) g",
            ct);

        var rows = await ReadAllRowsAsync(PostgreSqlCopyShape.CopyToCommand(table), ct);

        Assert.Equal(100, rows.Count);
        Assert.Equal(Enumerable.Range(1, 100), rows.Select(row => row.Id ?? -1));
        Assert.Equal("row-100", rows[^1].Name);
    }

    [Fact]
    public async Task CopyOut_NullColumns_AreReportedAsNull()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync($"INSERT INTO {table} (id) VALUES (1)", ct);

        var row = await ReadSingleRowAsync(table, ct);

        Assert.Equal<int?>(1, row.Id);
        Assert.Null(row.Name);
        Assert.Null(row.Amount);
        Assert.Null(row.Day);
        Assert.Null(row.AtTime);
        Assert.Null(row.Stamp);
        Assert.Null(row.Instant);
        Assert.Null(row.Document);
    }

    [Fact]
    public async Task CopyOut_TextColumn_PreservesUnicodeQuotesAndEmptyString()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        const string unicode = "Елена 漢字 \"double\" 'single' \ttab\nline\\backslash";
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) VALUES (@id, @name)",
            ct,
            ("id", 1),
            ("name", unicode));
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) VALUES (@id, @name)",
            ct,
            ("id", 2),
            ("name", string.Empty));

        var rows = await ReadAllRowsAsync(PostgreSqlCopyShape.CopyToCommand(table), ct);

        Assert.Equal(2, rows.Count);
        Assert.True(string.Equals(unicode, rows[0].Name, StringComparison.Ordinal), "The text column was not preserved byte-for-byte.");
        Assert.Equal(string.Empty, rows[1].Name);
    }

    [Fact]
    public async Task CopyOut_NumericColumn_PreservesScaleAndSign()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        foreach (var (id, amount) in new[] { (1, 1234.5678m), (2, -0.0001m), (3, 99999999999999.9999m) })
        {
            await database.ExecuteAsync(
                $"INSERT INTO {table} (id, amount) VALUES (@id, @amount)",
                ct,
                ("id", id),
                ("amount", amount));
        }

        var rows = await ReadAllRowsAsync(PostgreSqlCopyShape.CopyToCommand(table), ct);

        Assert.Equal(3, rows.Count);
        Assert.Equal<decimal?>(1234.5678m, rows[0].Amount);
        Assert.Equal<decimal?>(-0.0001m, rows[1].Amount);
        Assert.Equal<decimal?>(99999999999999.9999m, rows[2].Amount);
    }

    [Fact]
    public async Task CopyOut_DateOnlyColumn_CoversBoundaries()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        var values = new DateOnly?[] { DateOnly.MinValue, new DateOnly(2024, 2, 29), DateOnly.MaxValue };
        for (var index = 0; index < values.Length; index++)
        {
            await database.ExecuteAsync(
                $"INSERT INTO {table} (id, day) VALUES (@id, @day)",
                ct,
                ("id", index + 1),
                ("day", values[index]));
        }

        var rows = await ReadAllRowsAsync(PostgreSqlCopyShape.CopyToCommand(table), ct);

        Assert.Equal(values, rows.Select(row => row.Day));
    }

    [Fact]
    public async Task CopyOut_TimeOnlyColumn_PreservesMicrosecondPrecision()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        var values = new TimeOnly?[]
        {
            TimeOnly.MinValue,
            TimeOnly.FromTimeSpan(new TimeSpan(0, 13, 45, 7, 123, 456)),
            new TimeOnly(23, 59, 59),
        };
        for (var index = 0; index < values.Length; index++)
        {
            await database.ExecuteAsync(
                $"INSERT INTO {table} (id, at_time) VALUES (@id, @at_time)",
                ct,
                ("id", index + 1),
                ("at_time", values[index]));
        }

        var rows = await ReadAllRowsAsync(PostgreSqlCopyShape.CopyToCommand(table), ct);

        Assert.Equal(values, rows.Select(row => row.AtTime));
    }

    [Fact]
    public async Task CopyOut_TimestampAndTimestamptz_PreserveTheirInstants()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        var stamp = new DateTime(2024, 2, 29, 13, 45, 7, 123, DateTimeKind.Unspecified).AddTicks(4560);
        var instant = new DateTime(2024, 2, 29, 13, 45, 7, 123, DateTimeKind.Utc).AddTicks(4560);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, stamp, instant) VALUES (@id, @stamp, @instant)",
            ct,
            ("id", 1),
            ("stamp", stamp),
            ("instant", instant));

        var row = await ReadSingleRowAsync(table, ct);

        Assert.Equal<DateTime?>(stamp, row.Stamp);
        Assert.Equal(DateTimeKind.Unspecified, row.Stamp!.Value.Kind);
        Assert.Equal<DateTime?>(instant, row.Instant);

        // See CopyOut_OneRow_ReadsEveryMappedType: the provider picks the reported Kind, so the scenario asserts the
        // instant is preserved and never shifted into local time.
        Assert.NotEqual(DateTimeKind.Local, row.Instant!.Value.Kind);
    }

    [Fact]
    public async Task CopyOut_JsonbColumn_IsReadAsStringWithAnExplicitProviderType()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, document) VALUES (@id, CAST(@document AS jsonb))",
            ct,
            ("id", 1),
            ("document", "{\"nested\":{\"b\":[1,2,3],\"a\":\"text\"},\"empty\":null}"));

        var row = await ReadSingleRowAsync(table, ct);

        // JSONB is read as raw text with NpgsqlDbType.Jsonb: the text is PostgreSQL's canonical jsonb output.
        Assert.Equal("{\"empty\": null, \"nested\": {\"a\": \"text\", \"b\": [1, 2, 3]}}", row.Document);
    }

    [Fact]
    public async Task CopyOut_ExpectedColumnCountMatch_Succeeds()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await SeedRowAsync(table, OneFullRow, ct);

        var rows = await ReadAllRowsAsync(
            PostgreSqlCopyShape.CopyToCommand(table),
            ct,
            new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = PostgreSqlCopyShape.ColumnCount });

        Assert.Single(rows);
        Assert.Equal(OneFullRow.Id, rows[0].Id);
    }

    [Fact]
    public async Task CopyOut_ExpectedColumnCountMismatch_FailsBeforeTheRowCallbackRuns()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await SeedRowAsync(table, OneFullRow, ct);

        var callbackInvocations = 0;
        await using var source = await CreateSourceAsync(
            PostgreSqlCopyShape.CopyToCommand(table),
            new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = PostgreSqlCopyShape.ColumnCount - 1 },
            (exporter, columnCount, token) =>
            {
                callbackInvocations++;
                return PostgreSqlCopyShape.ReadRowAsync(exporter, columnCount, token);
            });

        await source.InitializeAsync(ct);
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, ct);
        });

        Assert.Equal(0, callbackInvocations);
        Assert.IsNotType<OperationCanceledException>(PostgreSqlFailureAssert.Primary(failure));
        Assert.True(
            PostgreSqlFailureAssert.Tree(failure).Any(exception =>
                exception.Message.Contains("column", StringComparison.OrdinalIgnoreCase)),
            $"A column-count mismatch must be reported as such, but the failure was: {PostgreSqlFailureAssert.Describe(failure)}");

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_CancellationBeforeBegin_ThrowsOperationCanceled()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await SeedRowAsync(table, OneFullRow, ct);

        await using var source = await CreateSourceAsync(PostgreSqlCopyShape.CopyToCommand(table));
        await source.InitializeAsync(ct);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, cancelled.Token);
        });

        Assert.NotNull(failure);
        Assert.Equal(0L, await database.CountActiveCopyBackendsAsync(ct));
        Assert.Equal(1L, await database.CountRowsAsync(table, ct));

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_CancellationDuringRowStart_ThrowsOperationCanceled()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);

        // A 50k-row COPY cannot stay inside Npgsql's read buffer: the exporter must refill it, and that is where a
        // cancelled token is observed. Npgsql does not inspect the token while data is still buffered.
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 50000) g",
            ct);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rowsRead = 0;
        await using var source = await CreateSourceAsync(
            PostgreSqlCopyShape.CopyToCommand(table),
            rowReader: async (exporter, columnCount, token) =>
            {
                var row = await PostgreSqlCopyShape.ReadRowAsync(exporter, columnCount, token);
                rowsRead++;
                if (rowsRead == 1)
                    cancellation.Cancel();
                return row;
            });

        await source.InitializeAsync(ct);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, cancellation.Token);
        });

        Assert.NotNull(failure);
        Assert.True(rowsRead >= 1, "The export produced no row before the cancellation was observed.");
        Assert.IsAssignableFrom<OperationCanceledException>(PostgreSqlFailureAssert.Primary(failure));

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_CancellationDuringRowRead_ThrowsOperationCanceled()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 50000) g",
            ct);

        var firstRowsDelivered = PostgreSqlGate.Create();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var canceller = Task.Run(
            async () =>
            {
                await firstRowsDelivered.Task;
                cancellation.Cancel();
            },
            ct);

        var rowsRead = 0;
        await using var source = await CreateSourceAsync(
            PostgreSqlCopyShape.CopyToCommand(table),
            rowReader: async (exporter, columnCount, token) =>
            {
                var row = await PostgreSqlCopyShape.ReadRowAsync(exporter, columnCount, token);
                if (++rowsRead == 25)
                    firstRowsDelivered.TrySetResult();
                return row;
            });

        await source.InitializeAsync(ct);

        // The cancellation arrives from outside while the COPY is streaming, so the source observes it in the read
        // path of an already started export.
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, cancellation.Token);
        });

        await canceller;
        Assert.NotNull(failure);
        Assert.True(rowsRead >= 25, $"Only {rowsRead} rows were delivered before the external cancellation.");

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_RowReaderFailure_IsPropagatedAsThePrimaryFailure()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 100) g",
            ct);

        var expected = new CopyOutScenarioException("the consumer rejected row 3");
        var rowsRead = 0;
        await using var source = await CreateSourceAsync(
            PostgreSqlCopyShape.CopyToCommand(table),
            rowReader: async (exporter, columnCount, token) =>
            {
                var row = await PostgreSqlCopyShape.ReadRowAsync(exporter, columnCount, token);
                if (++rowsRead == 3)
                    throw expected;
                return row;
            });

        await source.InitializeAsync(ct);
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, ct);
        });

        Assert.Same(expected, PostgreSqlFailureAssert.Primary(failure));
        Assert.Equal(3, rowsRead);

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_EarlyConsumerBreak_StopsTheExportAndDisposesCleanly()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 100000) g",
            ct);

        await using var source = await CreateSourceAsync(PostgreSqlCopyShape.CopyToCommand(table));
        await source.InitializeAsync(ct);

        var consumed = 0;
        await foreach (var envelope in source.ReadEnvelopesAsync(ct).WithCancellation(ct))
        {
            Assert.Equal(consumed + 1, envelope.Payload.Id);
            consumed++;
            if (consumed == 5)
                break;
        }

        Assert.Equal(5, consumed);

        // Breaking out of the enumeration disposes the enumerator, which runs the source's early-break path: the
        // export must already be released, without the source throwing and without leaking protocol state.
        var stopped = await PostgreSqlIntegrationDatabase.WaitUntilAsync(
            async token => await database.CountActiveCopyBackendsAsync(token) == 0,
            PostgreSqlTestGuard.Short,
            ct);
        Assert.True(stopped, "The binary COPY export was still running after the consumer broke out of the enumeration.");

        await source.DisposeAsync();
        Assert.Equal(100000L, await database.CountRowsAsync(table, ct));
    }

    [Fact]
    public async Task CopyOut_ProviderConnectionFailure_FailsActivationWithoutStartingCopy()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;

        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Port = UnreachablePort,
            Pooling = false,
            Timeout = 3,
        };
        await using var unreachable = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        await using var source = await CreateSourceAsync(
            "COPY (SELECT 1) TO STDOUT (FORMAT BINARY)",
            dataSource: unreachable);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => source.InitializeAsync(ct).AsTask());

        Assert.IsNotType<OperationCanceledException>(PostgreSqlFailureAssert.Primary(failure));
        Assert.True(
            PostgreSqlFailureAssert.Tree(failure).Any(exception => exception is NpgsqlException or SocketException),
            $"Expected a provider connection failure but observed: {PostgreSqlFailureAssert.Describe(failure)}");

        // Activation failed, so disposal must be a no-op that neither throws nor hangs.
        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_RepeatedEnumeration_IsRejected()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await SeedRowAsync(table, OneFullRow, ct);

        await using var source = await CreateSourceAsync(PostgreSqlCopyShape.CopyToCommand(table));
        await source.InitializeAsync(ct);
        Assert.Equal(1, await PostgreSqlEnumeration.CountAsync(source, ct));

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await PostgreSqlEnumeration.CountAsync(source, ct);
        });

        Assert.True(
            failure is InvalidOperationException or ObjectDisposedException,
            $"The second enumeration must be rejected, but the failure was: {PostgreSqlFailureAssert.Describe(failure)}");
        PostgreSqlFailureAssert.ContainsMessage(failure, PostgreSqlErrorMessages.SourceEnumeratedTwice);

        await source.DisposeAsync();
    }

    [Fact]
    public async Task CopyOut_ConcurrentEnumeration_IsRejected()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateTableAsync(ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, 'row-' || g FROM generate_series(1, 10) g",
            ct);

        await using var source = await CreateSourceAsync(PostgreSqlCopyShape.CopyToCommand(table));
        await source.InitializeAsync(ct);

        await using var first = source.ReadEnvelopesAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await first.MoveNextAsync());

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var second = source.ReadEnvelopesAsync(ct).GetAsyncEnumerator(ct);
            await second.MoveNextAsync();
        });

        Assert.True(
            failure is InvalidOperationException or ObjectDisposedException,
            $"The concurrent enumeration must be rejected, but the failure was: {PostgreSqlFailureAssert.Describe(failure)}");
        PostgreSqlFailureAssert.ContainsMessage(failure, PostgreSqlErrorMessages.SourceEnumeratedTwice);

        await first.DisposeAsync();
        await source.DisposeAsync();
    }

    private async Task<string> CreateTableAsync(CancellationToken ct)
    {
        var table = database.NewTable();
        await database.ExecuteAsync($"CREATE TABLE {table} ({PostgreSqlCopyShape.TableDefinition})", ct);
        return table;
    }

    private async Task SeedRowAsync(string table, CopyRow row, CancellationToken ct)
    {
        await database.ExecuteAsync(
            $"INSERT INTO {table} ({PostgreSqlCopyShape.ColumnList}) VALUES (@id, @name, @amount, @day, @at_time, @stamp, @instant, CAST(@document AS jsonb))",
            ct,
            ("id", row.Id),
            ("name", row.Name),
            ("amount", row.Amount),
            ("day", row.Day),
            ("at_time", row.AtTime),
            ("stamp", row.Stamp),
            ("instant", row.Instant),
            ("document", row.Document));
    }

    private async Task<CopyRow> ReadSingleRowAsync(string table, CancellationToken ct)
    {
        var rows = await ReadAllRowsAsync(PostgreSqlCopyShape.CopyToCommand(table), ct);
        return Assert.Single(rows);
    }

    private async Task<IReadOnlyList<CopyRow>> ReadAllRowsAsync(
        string copyToCommand,
        CancellationToken ct,
        PostgreSqlBinaryCopySourceOptions? options = null)
    {
        await using var source = await CreateSourceAsync(copyToCommand, options);
        await source.InitializeAsync(ct);

        var rows = new List<CopyRow>();
        await foreach (var envelope in source.ReadEnvelopesAsync(ct).WithCancellation(ct))
            rows.Add(envelope.Payload);

        await source.DisposeAsync();
        return rows;
    }

    private async Task<IPipelineSource<CopyRow>> CreateSourceAsync(
        string copyToCommand,
        PostgreSqlBinaryCopySourceOptions? options = null,
        Func<NpgsqlBinaryExporter, int, CancellationToken, ValueTask<CopyRow>>? rowReader = null,
        NpgsqlDataSource? dataSource = null)
    {
        var descriptor = PostgreSqlPipelineComponents.BinaryCopySource(
            dataSource ?? database.DataSource,
            copyToCommand,
            rowReader ?? PostgreSqlCopyShape.ReadRowAsync,
            options ?? new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = PostgreSqlCopyShape.ColumnCount });

        return await PostgreSqlComponentActivation.ActivateAsync(descriptor);
    }

    private sealed class CopyOutScenarioException(string message) : InvalidOperationException(message);
}
