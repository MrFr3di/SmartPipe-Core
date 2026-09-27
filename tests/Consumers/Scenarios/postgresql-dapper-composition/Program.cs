using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper;
using SmartPipe.Extensions.PostgreSql;

// The application owns the data source, the schema and every connection string. Both integrations borrow the
// same long-lived instance: SmartPipe.Extensions.Dapper through the generic DbDataSource boundary and the
// PostgreSQL-native COPY / LISTEN components through their provider-specific factories.
var connectionString = ConsumerEnvironment.RequireConnectionString();
const string Schema = "sp_consumer_postgresql_dapper";
const string Channel = "sp_dapper_composition_channel";
const string CopyTargetCommand = $"COPY {Schema}.copy_target (id, name) FROM STDIN (FORMAT BINARY)";

await using var dataSource = NpgsqlDataSource.Create(connectionString);
try
{
    var application = await dataSource.OpenConnectionAsync();
    try
    {
        await ConsumerSql.ExecuteAsync(
            application,
            $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.source_rows (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE {Schema}.sink_rows (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE {Schema}.copy_source (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE {Schema}.copy_target (id integer PRIMARY KEY, name text NOT NULL);
            INSERT INTO {Schema}.source_rows (id, name) VALUES (1, 'Ada'), (2, 'Grace');
            INSERT INTO {Schema}.copy_source (id, name) VALUES (10, 'Alpha'), (11, 'Beta');
            """);

        // This application connection stays checked out for the whole scenario, so no pipeline run can borrow it.
        var applicationBackendId = await ConsumerSql.ReadBackendProcessIdAsync(application);

        // Leg 1: the generic DbDataSource boundary of SmartPipe.Extensions.Dapper. Every result is requested
        // explicitly because the default runtime output policy suppresses sink successes.
        var dapperBackendIds = new List<int>();
        var dapperDefinition = DapperPipelineDefinitionBuilder
            .FromQuery<DapperRow>(
                new PipelineKey("postgresql-dapper-composition-query"),
                dataSource,
                $"SELECT id, name, pg_backend_pid() AS backend_pid FROM {Schema}.source_rows ORDER BY id",
                new DapperQueryOptions { OperationName = "postgresql-dapper-composition-query" },
                rowMapper: reader => new DapperRow(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)))
            .WithRuntimeOptions(new PipelineRuntimeOptions { OutputPolicy = PipelineOutputPolicy.EmitAll })
            .ToCommand(
                dataSource,
                $"INSERT INTO {Schema}.sink_rows (id, name) VALUES (@Id, @Name) ON CONFLICT (id) DO NOTHING",
                new DapperSinkOptions { OperationName = "postgresql-dapper-composition-command" },
                parameterFactory: envelope => new { envelope.Payload.Id, envelope.Payload.Name });

        var dapperRows = await PipelineDrain.ReadAllAsync(dapperDefinition, row =>
        {
            dapperBackendIds.Add(row.BackendProcessId);
            return Task.CompletedTask;
        });

        ConsumerCheck.Require(
            dapperRows.Select(row => new Row(row.Id, row.Name)).SequenceEqual([new Row(1, "Ada"), new Row(2, "Grace")]),
            "The Dapper leg did not stream the seeded rows in order through the shared data source.");
        ConsumerCheck.Require(
            dapperBackendIds.Count == 2 && dapperBackendIds.Distinct().Count() == 1,
            "One Dapper source run must use exactly one connection for every row it streams.");
        ConsumerCheck.Require(
            dapperBackendIds.TrueForAll(backendId => backendId != applicationBackendId),
            "The Dapper leg reused the application's checked-out connection instead of obtaining its own.");
        ConsumerCheck.Require(
            await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {Schema}.sink_rows") == 2,
            "The Dapper sink did not commit the rows for the application connection to observe.");

        // Leg 2: the PostgreSQL-native binary COPY OUT source on the very same data source.
        var copyBackendIds = new List<int>();
        var copyOutDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<Row>(
                new PipelineKey("postgresql-dapper-composition-copy-out"),
                dataSource,
                $"COPY (SELECT id, name, pg_backend_pid() FROM {Schema}.copy_source ORDER BY id) TO STDOUT (FORMAT BINARY)",
                async (exporter, columnCount, cancellationToken) =>
                {
                    ConsumerCheck.Require(columnCount == 3, "The COPY OUT export reported an unexpected column count.");
                    var id = await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                    var name = await exporter.ReadAsync<string>(NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                    var backendId = await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                    copyBackendIds.Add(backendId);
                    return new Row(id, name);
                },
                new PostgreSqlBinaryCopySourceOptions
                {
                    OperationName = "postgresql-dapper-composition-copy-out",
                    ExpectedColumnCount = 3,
                })
            .Build();

        var copiedRows = await PipelineDrain.ReadAllAsync(copyOutDefinition);
        ConsumerCheck.Require(
            copiedRows.SequenceEqual([new Row(10, "Alpha"), new Row(11, "Beta")]),
            "The PostgreSQL-native COPY OUT leg did not stream the seeded rows in order.");
        ConsumerCheck.Require(
            copyBackendIds.Count == 2
                && copyBackendIds.Distinct().Count() == 1
                && copyBackendIds[0] != applicationBackendId,
            "The COPY OUT leg must obtain its own single connection instead of the application's checked-out connection.");

        // Leg 3: the PostgreSQL-native binary COPY IN batch sink on the same data source. The default runtime output
        // policy suppresses successful results once a sink is attached, so every result is requested explicitly: the
        // published output is the observation that the sink write returned.
        var writtenRows = 0;
        var copyInDefinition = BatchDefinitions
            .FromBatches<Row>(new PipelineKey("postgresql-dapper-composition-copy-in"), [copiedRows])
            .WithRuntimeOptions(new PipelineRuntimeOptions { OutputPolicy = PipelineOutputPolicy.EmitAll })
            .ToPostgreSqlBinaryCopy(
                dataSource,
                CopyTargetCommand,
                CopyCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = "postgresql-dapper-composition-copy-in",
                    MaxRowsPerBatch = 16,
                });

        var committedBatches = await PipelineDrain.ReadAllAsync(copyInDefinition, async batch =>
        {
            writtenRows += batch.Count;
            var visible = await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {Schema}.copy_target");
            ConsumerCheck.Require(
                visible == writtenRows,
                $"A successful COPY IN write was not durable: {visible} rows were visible after {writtenRows} rows were reported as written.");
        });

        ConsumerCheck.Require(
            committedBatches.Count == 1 && committedBatches[0].Count == copiedRows.Count,
            "The COPY IN leg did not publish exactly one complete batch.");

        // Leg 4: LISTEN / NOTIFY through the same data source, while the application keeps its own session.
        var listenerDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromNotifications(
                new PipelineKey("postgresql-dapper-composition-listen"),
                dataSource,
                [Channel],
                new PostgreSqlNotificationSourceOptions
                {
                    OperationName = "postgresql-dapper-composition-listen",
                    BufferCapacity = 8,
                })
            .Build();

        // StartAsync returns after the LISTEN registration committed, so a NOTIFY issued afterwards is delivered to
        // the pipeline's listening session. The notification is raised on a second connection leased from the same
        // data source, so the scenario observes three distinct sessions of one shared data source.
        var listenerRun = await listenerDefinition.StartAsync();
        PipelineOutput<PostgreSqlNotification>? delivered = null;
        var notifierBackendId = 0;
        try
        {
            await using (var notifier = await dataSource.OpenConnectionAsync())
            {
                notifierBackendId = await ConsumerSql.ReadBackendProcessIdAsync(notifier);
                ConsumerCheck.Require(
                    notifierBackendId != applicationBackendId,
                    "A fresh connection from the shared data source must never be the application's checked-out connection.");
                await ConsumerSql.ExecuteAsync(notifier, $"NOTIFY {Channel}, 'wake-up'");
            }

            delivered = await listenerRun.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            ConsumerCheck.Require(delivered.Result.IsSuccess, "The LISTEN source published a failing notification result.");

            // The application's long-lived session never registered the channel, so the delivery above proves that
            // the source run holds its own listening session for the lifetime of the run.
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(application, "SELECT count(*) FROM pg_listening_channels()") == 0,
                "The application's own connection must not be the session that listens.");
        }
        finally
        {
            await listenerRun.CancelAsync();
            try
            {
                await listenerRun.Completion;
            }
            catch (OperationCanceledException)
            {
                // Cancelling an intentionally infinite notification source is the expected stop.
            }

            ConsumerCheck.Require(
                listenerRun.State == PipelineRunState.Cancelled,
                "The notification run did not end in a cooperative cancellation.");
            await listenerRun.DisposeAsync();
        }

        ConsumerCheck.Require(delivered is not null, "The LISTEN source did not deliver the notification raised by the application.");
        var notification = delivered!.Result.Value!;
        ConsumerCheck.Require(
            notification.Channel == Channel && notification.Payload == "wake-up",
            "The LISTEN source delivered a different channel or payload than the application raised.");
        ConsumerCheck.Require(
            notification.BackendProcessId == notifierBackendId,
            "The delivered notification must originate from the notifying application connection.");
        ConsumerCheck.Require(
            notification.BackendProcessId != applicationBackendId,
            "The notification must not originate from the application's long-lived session.");

        // Both integrations must still work after both pipelines have run, and neither may have disposed the shared
        // data source: a second run of each leg starts from the very same instance.
        var secondDapperBackendIds = new List<int>();
        var secondDapperRows = await PipelineDrain.ReadAllAsync(dapperDefinition, row =>
        {
            secondDapperBackendIds.Add(row.BackendProcessId);
            return Task.CompletedTask;
        });
        ConsumerCheck.Require(secondDapperRows.Count == 2, "The Dapper leg stopped working after both pipelines had run.");
        ConsumerCheck.Require(
            secondDapperBackendIds.Count == 2 && secondDapperBackendIds.TrueForAll(backendId => backendId != applicationBackendId),
            "The second Dapper run reused the application's checked-out connection instead of obtaining its own.");

        var secondCopiedRows = await PipelineDrain.ReadAllAsync(copyOutDefinition);
        ConsumerCheck.Require(
            secondCopiedRows.SequenceEqual([new Row(10, "Alpha"), new Row(11, "Beta")]),
            "The PostgreSQL-native COPY OUT leg stopped working after both pipelines had run.");

        await ConsumerSql.ExecuteAsync(
            application,
            $"CREATE TABLE {Schema}.copy_target_reuse (id integer PRIMARY KEY, name text NOT NULL);");
        var reuseCopyInDefinition = BatchDefinitions
            .FromBatches<Row>(new PipelineKey("postgresql-dapper-composition-copy-in-reuse"), [secondCopiedRows])
            .WithRuntimeOptions(new PipelineRuntimeOptions { OutputPolicy = PipelineOutputPolicy.EmitAll })
            .ToPostgreSqlBinaryCopy(
                dataSource,
                $"COPY {Schema}.copy_target_reuse (id, name) FROM STDIN (FORMAT BINARY)",
                CopyCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = "postgresql-dapper-composition-copy-in-reuse",
                    MaxRowsPerBatch = 16,
                });

        var reuseBatches = await PipelineDrain.ReadAllAsync(reuseCopyInDefinition);
        ConsumerCheck.Require(
            reuseBatches.Count == 1 && reuseBatches[0].Count == secondCopiedRows.Count,
            "The PostgreSQL-native COPY IN leg stopped working after both pipelines had run.");
        ConsumerCheck.Require(
            (await ConsumerSql.ReadRowsAsync(application, $"SELECT id, name FROM {Schema}.copy_target_reuse ORDER BY id"))
                .SequenceEqual(secondCopiedRows),
            "The reused COPY IN write is not queryable through the application connection.");

        // A fresh acquisition through the same instance proves the data source is still open and usable.
        await using (var reuse = await dataSource.OpenConnectionAsync())
        {
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(reuse, $"SELECT count(*) FROM {Schema}.copy_source") == 2,
                "The application-owned data source was no longer usable after both pipelines had run.");
        }

        ConsumerCheck.Require(
            await ConsumerSql.ReadBackendProcessIdAsync(application) == applicationBackendId,
            "The application's own connection was replaced or disturbed during the scenario.");
    }
    finally
    {
        await application.DisposeAsync();
    }
}
finally
{
    await ConsumerSql.DropSchemaAsync(dataSource, Schema);
}

Console.WriteLine("CONSUMER_OK postgresql-dapper-composition");
return 0;
