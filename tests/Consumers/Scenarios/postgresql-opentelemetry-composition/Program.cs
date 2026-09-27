using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using SmartPipe.Core;
using SmartPipe.Extensions.OpenTelemetry;
using SmartPipe.Extensions.PostgreSql;
using SmartPipe.Consumer.PostgreSql;

// Telemetry stays layered: SmartPipe.Extensions.OpenTelemetry registers the pipeline runtime's own diagnostics
// sources, Npgsql.OpenTelemetry registers the database client instrumentation, and the PostgreSQL production package
// creates no database spans of its own. Both provider-side packages belong to the application/test surface only.
var connectionString = ConsumerEnvironment.RequireConnectionString();
const string CopyTargetCommand = $"COPY {TelemetryScenario.RowsTable} (id, name) FROM STDIN (FORMAT BINARY)";
const string CopySourceCommand = $"COPY (SELECT id, name FROM {TelemetryScenario.RowsTable} ORDER BY id) TO STDOUT (FORMAT BINARY)";

var exported = new List<Activity>();
var services = new ServiceCollection();
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddNpgsql().AddInMemoryExporter(exported))
    .AddSmartPipeInstrumentation();

using var provider = services.BuildServiceProvider();
using var tracerProvider = provider.GetRequiredService<TracerProvider>();

await using var dataSource = NpgsqlDataSource.Create(connectionString);
try
{
    var application = await dataSource.OpenConnectionAsync();
    try
    {
        await ConsumerSql.ExecuteAsync(
            application,
            $"""
            DROP SCHEMA IF EXISTS {TelemetryScenario.Schema} CASCADE;
            CREATE SCHEMA {TelemetryScenario.Schema};
            CREATE TABLE {TelemetryScenario.RowsTable} (id integer PRIMARY KEY, name text NOT NULL);
            INSERT INTO {TelemetryScenario.RowsTable} (id, name) VALUES (1, 'Ada'), (2, 'Grace');
            """);

        // A control command executed outside every pipeline run: its database span must not claim a SmartPipe parent.
        await ConsumerSql.ExecuteAsync(application, "SELECT 42");

        var copyInDefinition = BatchDefinitions
            .FromBatches(
                new PipelineKey(TelemetryScenario.CopyInPipelineId),
                [new List<TelemetryRow> { new(3, "Linus"), new(4, "Margaret") }])
            .ToPostgreSqlBinaryCopy(
                dataSource,
                CopyTargetCommand,
                TelemetryCallbacks.WriteRowAsync,
                new PostgreSqlBinaryCopySinkOptions
                {
                    OperationName = TelemetryScenario.CopyInPipelineId,
                    MaxRowsPerBatch = 8,
                });

        var copyOutDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<TelemetryRow>(
                new PipelineKey(TelemetryScenario.CopyOutPipelineId),
                dataSource,
                CopySourceCommand,
                TelemetryCallbacks.ReadRowAsync,
                new PostgreSqlBinaryCopySourceOptions
                {
                    OperationName = TelemetryScenario.CopyOutPipelineId,
                    ExpectedColumnCount = 2,
                })
            .Build();

        await PipelineCompletion.RunAsync(copyInDefinition);
        ConsumerCheck.Require(
            await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {TelemetryScenario.RowsTable}") == 4,
            "The committed COPY IN batch is not visible to the application connection.");
        var copiedRows = await PipelineDrain.ReadAllAsync(copyOutDefinition);
        ConsumerCheck.Require(
            copiedRows.Count == 4 && copiedRows[0].Id == 1 && copiedRows[3].Id == 4,
            "The COPY OUT pipeline did not stream every committed row in order.");

        tracerProvider.ForceFlush();

        var smartPipeActivities = exported
            .Where(activity => string.Equals(activity.Source.Name, SmartPipeDiagnostics.ActivitySourceName, StringComparison.Ordinal))
            .ToArray();
        var databaseActivities = exported
            .Where(activity => ActivityGraph.Text(activity, "db.system.name") is not null)
            .ToArray();
        var activitiesBySpanId = exported.ToDictionary(activity => activity.SpanId);

        ConsumerCheck.Require(
            smartPipeActivities.Any(activity => activity.OperationName == "Pipeline.Run"),
            "The SmartPipe pipeline runtime emitted no Pipeline.Run activity through the registered source.");
        ConsumerCheck.Require(databaseActivities.Length > 0, "Npgsql emitted no database client spans.");
        ConsumerCheck.Require(
            databaseActivities.All(activity => !string.Equals(activity.Source.Name, SmartPipeDiagnostics.ActivitySourceName, StringComparison.Ordinal)),
            "A database client span originated from SmartPipe instead of Npgsql.");
        ConsumerCheck.Require(
            !smartPipeActivities.Any(ActivityGraph.CarriesDatabaseAttribute),
            "SmartPipe created a duplicate database span by attaching database client attributes to its own activity.");

        ConsumerCheck.Require(
            FindCopyActivityWithSmartPipeParent("COPY FROM", TelemetryScenario.CopyInPipelineId, databaseActivities, activitiesBySpanId) is not null,
            "The COPY IN activity emitted by Npgsql does not carry the active SmartPipe pipeline parent.");
        ConsumerCheck.Require(
            FindCopyActivityWithSmartPipeParent("COPY TO", TelemetryScenario.CopyOutPipelineId, databaseActivities, activitiesBySpanId) is not null,
            "The COPY OUT activity emitted by Npgsql does not carry the active SmartPipe pipeline parent.");

        var controlActivity = databaseActivities.SingleOrDefault(
            activity => string.Equals(ActivityGraph.Text(activity, "db.query.text"), "SELECT 42", StringComparison.Ordinal));
        ConsumerCheck.Require(
            controlActivity is not null,
            "The control command executed outside every pipeline produced no database client span.");
        ConsumerCheck.Require(
            ActivityGraph.FindSmartPipeAncestor(controlActivity!, activitiesBySpanId) is null,
            "A command executed outside a SmartPipe run was parented by SmartPipe.");

        // The PostgreSQL production package must not depend on any telemetry package.
        ConsumerAssemblyGuard.RequireNoReferenceContaining(
            typeof(PostgreSqlPipelineComponents).Assembly,
            "OpenTelemetry");
        ConsumerAssemblyGuard.RequireNoReferenceContaining(
            typeof(PostgreSqlPipelineComponents).Assembly,
            "SmartPipe.Extensions.OpenTelemetry");
    }
    finally
    {
        await application.DisposeAsync();
    }
}
finally
{
    await ConsumerSql.DropSchemaAsync(dataSource, TelemetryScenario.Schema);
}

Console.WriteLine("CONSUMER_OK postgresql-opentelemetry-composition");
return 0;

// Finds the Npgsql COPY activity that is a descendant of the named SmartPipe pipeline run.
Activity? FindCopyActivityWithSmartPipeParent(
    string operationName,
    string pipelineId,
    IReadOnlyList<Activity> databaseSpans,
    IReadOnlyDictionary<ActivitySpanId, Activity> spanIndex)
{
    foreach (var activity in databaseSpans)
    {
        if (!string.Equals(ActivityGraph.Text(activity, "db.operation.name"), operationName, StringComparison.Ordinal))
            continue;

        var pipelineActivity = ActivityGraph.FindSmartPipeAncestor(activity, spanIndex);
        if (pipelineActivity is not null
            && string.Equals(ActivityGraph.Text(pipelineActivity, "smartpipe.pipeline_id"), pipelineId, StringComparison.Ordinal))
        {
            return activity;
        }
    }

    return null;
}
