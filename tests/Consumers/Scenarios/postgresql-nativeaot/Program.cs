using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql;

// The slim data source is the documented trim/AOT and static-primitive path: built-in primitive mappings only, no
// dynamic JSON, no unmapped or composite types, no reflection-based registration and static COPY callbacks.
// Transport security keeps its slim-builder default (off); the local trust server needs no TLS.
var connectionString = ConsumerEnvironment.RequireConnectionString();
const string CopyTargetCommand = $"COPY {AotScenario.RowsTable} (id, name) FROM STDIN (FORMAT BINARY)";
const string CopySourceCommand = $"COPY (SELECT id, name FROM {AotScenario.RowsTable} ORDER BY id) TO STDOUT (FORMAT BINARY)";
const string StreamingCopyCommand =
    "COPY (SELECT value, 'row-' || value FROM generate_series(1, 50000) AS value) TO STDOUT (FORMAT BINARY)";

await using var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).Build();
try
{
    var application = await dataSource.OpenConnectionAsync();
    try
    {
        await ConsumerSql.ExecuteAsync(
            application,
            $"""
            DROP SCHEMA IF EXISTS {AotScenario.Schema} CASCADE;
            CREATE SCHEMA {AotScenario.Schema};
            CREATE TABLE {AotScenario.RowsTable} (id integer PRIMARY KEY, name text NOT NULL);
            INSERT INTO {AotScenario.RowsTable} (id, name) VALUES (1, 'Ada'), (2, 'Grace');
            """);

        var applicationBackendId = await ConsumerSql.ReadBackendProcessIdAsync(application);

        // Leg 1: binary COPY OUT against the real server.
        var copyOutDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<AotRow>(
                new PipelineKey(AotScenario.CopyOutPipelineId),
                dataSource,
                CopySourceCommand,
                AotCallbacks.ReadRowAsync,
                new PostgreSqlBinaryCopySourceOptions
                {
                    OperationName = AotScenario.CopyOutPipelineId,
                    ExpectedColumnCount = 2,
                })
            .Build();

        var copiedRows = await PipelineDrain.ReadAllAsync(copyOutDefinition);
        ConsumerCheck.Require(
            copiedRows.Count == 2 && copiedRows[0] == new AotRow(1, "Ada") && copiedRows[1] == new AotRow(2, "Grace"),
            "The COPY OUT leg did not stream the seeded rows in order.");

        // Leg 2: binary COPY IN, one complete COPY per batch envelope.
        var copyInDefinition = BatchDefinitions
            .FromBatches(new PipelineKey(AotScenario.CopyInPipelineId), [new List<AotRow> { new(3, "Linus") }])
            .ToPostgreSqlBinaryCopy(
                dataSource,
                CopyTargetCommand,
                AotCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = AotScenario.CopyInPipelineId,
                    MaxRowsPerBatch = 8,
                });

        await PipelineCompletion.RunAsync(copyInDefinition);
        ConsumerCheck.Require(
            await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {AotScenario.RowsTable}") == 3,
            "The committed COPY IN row is not visible to the application connection.");

        // Leg 3: a LISTEN delivery on its own session, then cooperative cancellation of that live run.
        var listenerDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromNotifications(
                new PipelineKey(AotScenario.ListenPipelineId),
                dataSource,
                [AotScenario.Channel],
                new PostgreSqlNotificationSourceOptions
                {
                    OperationName = AotScenario.ListenPipelineId,
                    BufferCapacity = 8,
                })
            .Build();

        // StartAsync returns after the LISTEN registration committed, so a NOTIFY issued from the application's own
        // connection afterwards is delivered to the pipeline's listening session.
        var listenerRun = await listenerDefinition.StartAsync();
        try
        {
            await ConsumerSql.ExecuteAsync(application, $"NOTIFY {AotScenario.Channel}, 'trim-wake-up'");
            var delivery = await listenerRun.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            ConsumerCheck.Require(delivery.Result.IsSuccess, "The LISTEN source published a failing notification result.");
            var notification = delivery.Result.Value!;
            ConsumerCheck.Require(
                notification.Channel == AotScenario.Channel
                    && notification.Payload == "trim-wake-up"
                    && notification.BackendProcessId == applicationBackendId,
                "The LISTEN delivery did not match the notification the application raised.");
        }
        finally
        {
            await RunCancellation.RequireCancelledAsync(listenerRun, "The notification run");
            await listenerRun.DisposeAsync();
        }

        // Leg 4: cancellation of a COPY OUT that is still in flight. The export cannot finish while the consumer
        // stops reading, so cancellation is observed by the exporter and the run stops cooperatively.
        var streamingDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<AotRow>(
                new PipelineKey(AotScenario.StreamingPipelineId),
                dataSource,
                StreamingCopyCommand,
                AotCallbacks.ReadRowAsync,
                new PostgreSqlBinaryCopySourceOptions
                {
                    OperationName = AotScenario.StreamingPipelineId,
                    ExpectedColumnCount = 2,
                })
            .Build();

        var streamingRun = await streamingDefinition.StartAsync();
        try
        {
            var firstRow = await streamingRun.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            ConsumerCheck.Require(
                firstRow.Result.IsSuccess && firstRow.Result.Value == new AotRow(1, "row-1"),
                "The in-flight COPY OUT did not stream its first row.");
            await RunCancellation.RequireCancelledAsync(streamingRun, "The in-flight COPY OUT run");
        }
        finally
        {
            await streamingRun.DisposeAsync();
        }

        ConsumerCheck.Require(
            await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {AotScenario.RowsTable}") == 3,
            "The application connection observed a disturbed table after cancellation.");
    }
    finally
    {
        await application.DisposeAsync();
    }
}
finally
{
    await ConsumerSql.DropSchemaAsync(dataSource, AotScenario.Schema);
}

Console.WriteLine("CONSUMER_OK postgresql-nativeaot");
return 0;
