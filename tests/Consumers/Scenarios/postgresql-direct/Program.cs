using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql;
using SmartPipe.Consumer.PostgreSql;

// The application owns the data source and the connection string. The scenario reads the connection string from
// the environment, never from a hardcoded fallback, and fails loudly when it is missing.
var connectionString = ConsumerEnvironment.RequireConnectionString();
const string Schema = "sp_consumer_postgresql_direct";
const string CopyInCommand = $"COPY {Schema}.target_rows (id, name) FROM STDIN (FORMAT BINARY)";

await using var dataSource = NpgsqlDataSource.Create(connectionString);
try
{
    // One application connection stays open for the whole scenario. Every pipeline run must obtain its own
    // connection from the shared data source instead of borrowing this one.
    var verification = await dataSource.OpenConnectionAsync();
    try
    {
        await ConsumerSql.ExecuteAsync(
            verification,
            $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.source_rows (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE {Schema}.target_rows (id integer PRIMARY KEY, name text NOT NULL);
            INSERT INTO {Schema}.source_rows (id, name) VALUES (1, 'Ada'), (2, 'Grace'), (3, 'Alan');
            """);

        // COPY OUT: the server streams the seeded rows through the binary export protocol.
        var observedColumnCounts = new List<int>();
        var copyOut = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<Row>(
                new PipelineKey("postgresql-direct-copy-out"),
                dataSource,
                $"COPY (SELECT id, name FROM {Schema}.source_rows ORDER BY id) TO STDOUT (FORMAT BINARY)",
                (exporter, columnCount, cancellationToken) =>
                {
                    observedColumnCounts.Add(columnCount);
                    return CopyCallbacks.ReadRowAsync(exporter, cancellationToken);
                },
                new PostgreSqlBinaryCopySourceOptions
                {
                    OperationName = "postgresql-direct-copy-out",
                    ExpectedColumnCount = 2,
                })
            .Build();

        var streamedRows = await PipelineDrain.ReadAllAsync(copyOut);
        ConsumerCheck.Require(
            streamedRows.SequenceEqual([new Row(1, "Ada"), new Row(2, "Grace"), new Row(3, "Alan")]),
            "The binary COPY OUT stream did not produce the seeded rows in order.");
        ConsumerCheck.Require(
            observedColumnCounts.Count == 3 && observedColumnCounts.TrueForAll(count => count == 2),
            "The COPY OUT row reader did not receive two columns for every streamed row.");

        // COPY IN: one batch envelope is one complete COPY, so a successful write must already be durable. The
        // default runtime output policy suppresses successful results once a sink is attached, so the scenario asks
        // for every result explicitly: the published output is the observation that the sink write returned.
        var incoming = new Row[] { new(10, "Linus"), new(11, "Barbara") };
        var copyIn = BatchDefinitions
            .FromBatches<Row>(new PipelineKey("postgresql-direct-copy-in"), [incoming])
            .WithRuntimeOptions(new PipelineRuntimeOptions { OutputPolicy = PipelineOutputPolicy.EmitAll })
            .ToPostgreSqlBinaryCopy(
                dataSource,
                CopyInCommand,
                CopyCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = "postgresql-direct-copy-in",
                    MaxRowsPerBatch = 8,
                });

        var reportedBatches = 0;
        var reportedRows = 0;
        var writtenBatches = await PipelineDrain.ReadAllAsync(copyIn, async batch =>
        {
            reportedBatches++;
            reportedRows += batch.Count;

            // The batch output is published only after the sink's write returned, so this SELECT on an independent
            // application connection proves the committed rows are already visible.
            var visible = await ConsumerSql.CountAsync(verification, $"SELECT count(*) FROM {Schema}.target_rows");
            ConsumerCheck.Require(
                visible == reportedRows,
                $"A successful COPY IN write was not durable: {visible} rows were visible after {reportedRows} rows were reported as written.");
        });

        ConsumerCheck.Require(
            reportedBatches == 1 && writtenBatches.Count == 1,
            "The COPY IN pipeline did not publish exactly one batch output.");
        ConsumerCheck.Require(reportedRows == incoming.Length, "The COPY IN pipeline reported an unexpected number of rows.");
        ConsumerCheck.Require(
            (await ConsumerSql.ReadRowsAsync(verification, $"SELECT id, name FROM {Schema}.target_rows ORDER BY id")).SequenceEqual(incoming),
            "A normal SELECT did not return the COPY IN rows with their original values.");

        // An empty batch is a successful no-op: it publishes an output and writes nothing.
        var copyInEmpty = BatchDefinitions
            .FromBatches<Row>(new PipelineKey("postgresql-direct-copy-in-empty"), [Array.Empty<Row>()])
            .WithRuntimeOptions(new PipelineRuntimeOptions { OutputPolicy = PipelineOutputPolicy.EmitAll })
            .ToPostgreSqlBinaryCopy(
                dataSource,
                CopyInCommand,
                CopyCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = "postgresql-direct-copy-in-empty",
                    MaxRowsPerBatch = 8,
                });

        var emptyBatches = await PipelineDrain.ReadAllAsync(copyInEmpty);
        ConsumerCheck.Require(
            emptyBatches.Count == 1 && emptyBatches[0].Count == 0,
            "An empty batch must be a successful no-op that still publishes exactly one output.");
        ConsumerCheck.Require(
            await ConsumerSql.CountAsync(verification, $"SELECT count(*) FROM {Schema}.target_rows") == incoming.Length,
            "An empty batch changed rows.");

        // MaxRowsPerBatch + 1 is rejected before any server work, so the rejected batch can never change rows.
        // Emitting all results keeps the "no success output" observation meaningful for the rejected batch.
        var oversize = new Row[] { new(90, "Ada"), new(91, "Grace"), new(92, "Alan"), new(93, "Linus") };
        var copyInOversize = BatchDefinitions
            .FromBatches<Row>(new PipelineKey("postgresql-direct-copy-in-oversize"), [oversize])
            .WithRuntimeOptions(new PipelineRuntimeOptions { OutputPolicy = PipelineOutputPolicy.EmitAll })
            .ToPostgreSqlBinaryCopy(
                dataSource,
                CopyInCommand,
                CopyCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = "postgresql-direct-copy-in-oversize",
                    MaxRowsPerBatch = 3,
                });

        Exception? rejection = null;
        var rejectedOutputs = 0;
        var oversizeRun = await copyInOversize.StartAsync();
        try
        {
            await foreach (var _ in oversizeRun.Outputs.ReadAllAsync())
                rejectedOutputs++;

            await oversizeRun.Completion;
        }
        catch (Exception exception)
        {
            rejection = exception;
        }
        finally
        {
            await oversizeRun.DisposeAsync();
        }

        ConsumerCheck.Require(rejectedOutputs == 0, "A rejected batch must not publish a success output.");
        ConsumerCheck.Require(
            rejection is InvalidOperationException,
            $"A batch above MaxRowsPerBatch must fail the sink before server work; observed {rejection!.GetType().Name}.");
        ConsumerCheck.Require(
            rejection!.Message.Contains("MaxRowsPerBatch", StringComparison.Ordinal),
            "The oversized-batch rejection did not report the MaxRowsPerBatch contract.");
        ConsumerCheck.Require(
            await ConsumerSql.CountAsync(verification, $"SELECT count(*) FROM {Schema}.target_rows") == incoming.Length,
            "A rejected batch changed rows; an oversized batch must never partially reach PostgreSQL.");

        // The application-owned data source is still usable after every run, which is the observable proof that no
        // component disposed it: a second COPY OUT run starts from the same instance and streams the same rows.
        var reusedRows = await PipelineDrain.ReadAllAsync(copyOut);
        ConsumerCheck.Require(
            reusedRows.SequenceEqual([new Row(1, "Ada"), new Row(2, "Grace"), new Row(3, "Alan")]),
            "The application-owned data source was no longer usable after the pipeline runs.");
    }
    finally
    {
        await verification.DisposeAsync();
    }
}
finally
{
    await ConsumerSql.DropSchemaAsync(dataSource, Schema);
}

Console.WriteLine("CONSUMER_OK postgresql-direct");
return 0;
