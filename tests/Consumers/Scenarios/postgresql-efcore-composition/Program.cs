using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore;
using SmartPipe.Extensions.PostgreSql;
using SmartPipe.Consumer.PostgreSql;

// The application owns the data source, the Entity Framework Core context factory and every connection string.
// Npgsql.EntityFrameworkCore.PostgreSQL belongs to the application/test surface only: neither SmartPipe production
// package references the other, and the Entity Framework Core integration stays provider-neutral.
var connectionString = ConsumerEnvironment.RequireConnectionString();
const string CopyTargetCommand = $"COPY {EfCoreScenario.ItemsTable} (id, name, total) FROM STDIN (FORMAT BINARY)";
const string CopySourceCommand =
    $"COPY (SELECT id, name, total, pg_backend_pid() FROM {EfCoreScenario.ItemsTable} ORDER BY id) TO STDOUT (FORMAT BINARY)";

await using var dataSource = NpgsqlDataSource.Create(connectionString);
try
{
    var application = await dataSource.OpenConnectionAsync();
    try
    {
        await ConsumerSql.ExecuteAsync(
            application,
            $"""
            DROP SCHEMA IF EXISTS {EfCoreScenario.Schema} CASCADE;
            CREATE SCHEMA {EfCoreScenario.Schema};
            CREATE TABLE {EfCoreScenario.ItemsTable} (id integer PRIMARY KEY, name text NOT NULL, total numeric(12,2) NOT NULL);
            INSERT INTO {EfCoreScenario.ItemsTable} (id, name, total) VALUES (1, 'Ada', 12.50), (2, 'Grace', 25.00);
            """);

        // This application connection stays checked out for the whole scenario, so no pipeline run and no context
        // can borrow it.
        var applicationBackendId = await ConsumerSql.ReadBackendProcessIdAsync(application);

        var contextOptions = new DbContextOptionsBuilder<ItemsContext>().UseNpgsql(dataSource).Options;
        var contextFactory = new TrackingItemsContextFactory(contextOptions);

        var queryDefinition = EfCorePipelineDefinitionBuilder
            .FromQuery<ItemsContext, ItemRow>(
                new PipelineKey("postgresql-efcore-composition-query"),
                contextFactory,
                static (context, _) => context.Items.OrderBy(item => item.Id),
                new EfCoreQueryOptions { OperationName = "postgresql-efcore-composition-query" })
            .Build();

        // The compiled form proves the caller-provided async sequence surface and lets the run report the session
        // it actually used.
        var probeDefinition = EfCorePipelineDefinitionBuilder
            .FromCompiledQuery<ItemsContext, EfCoreProbe>(
                new PipelineKey("postgresql-efcore-composition-probe"),
                contextFactory,
                EfCoreProbeQuery.ProbeAsync,
                new EfCoreCompiledQueryOptions { OperationName = "postgresql-efcore-composition-probe" })
            .Build();

        // Leg 1: one runtime-owned LISTEN session, started before every other leg and expected to outlive them all.
        var listenerDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromNotifications(
                new PipelineKey("postgresql-efcore-composition-listen"),
                dataSource,
                [EfCoreScenario.Channel],
                new PostgreSqlNotificationSourceOptions
                {
                    OperationName = "postgresql-efcore-composition-listen",
                    BufferCapacity = 8,
                })
            .Build();

        // StartAsync returns after the LISTEN registration committed, so a NOTIFY issued from the application's own
        // connection afterwards is delivered to the pipeline's independent listening session.
        var listenerRun = await listenerDefinition.StartAsync();
        try
        {
            await ConsumerSql.ExecuteAsync(application, $"NOTIFY {EfCoreScenario.Channel}, 'before-efcore'");
            var beforeEfCore = await listenerRun.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            ConsumerCheck.Require(beforeEfCore.Result.IsSuccess, "The LISTEN source published a failing notification result.");

            // Leg 2: Entity Framework Core queries against the real server, through the runtime-owned context.
            var queriedRows = await PipelineDrain.ReadAllAsync(queryDefinition);
            ConsumerCheck.Require(
                queriedRows.Count == 2
                    && queriedRows[0].Id == 1
                    && queriedRows[0].Name == "Ada"
                    && queriedRows[0].Total == 12.50m
                    && queriedRows[1].Id == 2
                    && queriedRows[1].Name == "Grace",
                "The Entity Framework Core query leg did not read the seeded rows.");
            ConsumerCheck.Require(
                contextFactory.CreatedCount == 1 && contextFactory.DisposedCount == 1,
                $"The first Entity Framework Core run must create and dispose exactly one context: created={contextFactory.CreatedCount}, disposed={contextFactory.DisposedCount}.");

            // Leg 3: PostgreSQL-native binary COPY OUT on the same data source, reporting the session it used.
            var copyBackendIds = new List<int>();
            var copyOutDefinition = PostgreSqlPipelineDefinitionBuilder
                .FromBinaryCopy<ItemRow>(
                    new PipelineKey("postgresql-efcore-composition-copy-out"),
                    dataSource,
                    CopySourceCommand,
                    async (exporter, columnCount, cancellationToken) =>
                    {
                        // The exporter has no ColumnCount property: the column count is the reader's second argument.
                        ConsumerCheck.Require(columnCount == 4, "The COPY OUT export reported an unexpected column count.");
                        var id = await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                        var name = await exporter.ReadAsync<string>(NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                        var total = await exporter.ReadAsync<decimal>(NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                        copyBackendIds.Add(await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false));
                        return new ItemRow { Id = id, Name = name, Total = total };
                    },
                    new PostgreSqlBinaryCopySourceOptions
                    {
                        OperationName = "postgresql-efcore-composition-copy-out",
                        ExpectedColumnCount = 4,
                    })
                .Build();

            var copiedRows = await PipelineDrain.ReadAllAsync(copyOutDefinition);
            ConsumerCheck.Require(
                copiedRows.Count == 2 && copiedRows[0].Id == 1 && copiedRows[1].Id == 2,
                "The COPY OUT leg did not stream the seeded rows in order.");
            ConsumerCheck.Require(
                copyBackendIds.Count == 2
                    && copyBackendIds.Distinct().Count() == 1
                    && copyBackendIds[0] != applicationBackendId,
                "The COPY OUT leg must use exactly one of its own sessions, not the application's checked-out connection.");

            // Leg 4: PostgreSQL-native binary COPY IN through the same data source, two complete COPY batches in one
            // run. Primary keys make a repeated COPY observable, so the final row count proves that each batch
            // envelope completed exactly one COPY.
            var copyInDefinition = BatchDefinitions
                .FromBatches(
                    new PipelineKey("postgresql-efcore-composition-copy-in"),
                    [
                        new List<ItemRow>
                        {
                            new() { Id = 3, Name = "Linus", Total = 1.25m },
                            new() { Id = 4, Name = "Margaret", Total = 2.50m },
                        },
                        new List<ItemRow>
                        {
                            new() { Id = 5, Name = "Barbara", Total = 3.75m },
                            new() { Id = 6, Name = "Ken", Total = 5.00m },
                        },
                    ])
                .ToPostgreSqlBinaryCopy(
                    dataSource,
                    CopyTargetCommand,
                    ItemCallbacks.WriteItemAsync,
                    new PostgreSqlBinaryCopySinkOptions
                    {
                        OperationName = "postgresql-efcore-composition-copy-in",
                        MaxRowsPerBatch = 2,
                    });

            await PipelineCompletion.RunAsync(copyInDefinition);
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {EfCoreScenario.ItemsTable}") == 6,
                "The COPY IN leg did not commit exactly one complete COPY per batch envelope.");

            // Leg 5: an earlier committed batch must survive a later failing batch, and PostgreSQL must revert the
            // unfinished COPY of the failing batch. The first row of the failing batch is streamed before the primary
            // key conflict is raised, so its absence proves the aborted COPY was reverted rather than partly applied.
            var failingDefinition = BatchDefinitions
                .FromBatches(
                    new PipelineKey("postgresql-efcore-composition-copy-in-failure"),
                    [
                        new List<ItemRow> { new() { Id = 7, Name = "Committed first", Total = 6.25m } },
                        new List<ItemRow>
                        {
                            new() { Id = 8, Name = "Streamed then reverted", Total = 7.50m },
                            new() { Id = 7, Name = "Duplicate key", Total = 8.00m },
                        },
                    ])
                .ToPostgreSqlBinaryCopy(
                    dataSource,
                    CopyTargetCommand,
                    ItemCallbacks.WriteItemAsync,
                    new PostgreSqlBinaryCopySinkOptions
                    {
                        OperationName = "postgresql-efcore-composition-copy-in-failure",
                        MaxRowsPerBatch = 2,
                    });

            var failingOutcome = await FailingPipeline.ObserveAsync(failingDefinition);
            ConsumerCheck.Require(
                failingOutcome.ObservedFailure,
                $"A batch that violates the primary key must not report a successful COPY: failed outputs={failingOutcome.FailedOutputs}, failure={failingOutcome.Failure?.GetType().Name}.");
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {EfCoreScenario.ItemsTable} WHERE id = 7") == 1,
                "The batch committed before the failing batch did not survive the failure.");
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {EfCoreScenario.ItemsTable} WHERE id = 8") == 0,
                "Rows streamed by the aborted COPY were not reverted by PostgreSQL.");
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(application, $"SELECT count(*) FROM {EfCoreScenario.ItemsTable}") == 7,
                "The failing leg left the table in an unexpected state.");

            // Leg 6: Entity Framework Core again, now over a fresh context, and now observing the COPY IN rows.
            var probes = await PipelineDrain.ReadAllAsync(probeDefinition);
            ConsumerCheck.Require(
                probes.Count == 1,
                $"The compiled Entity Framework Core probe published {probes.Count} results.");
            ConsumerCheck.Require(
                probes[0].VisibleRows == 7,
                $"The Entity Framework Core probe did not observe the COPY IN rows: visible={probes[0].VisibleRows}.");
            ConsumerCheck.Require(
                probes[0].BackendProcessId != applicationBackendId,
                "The Entity Framework Core context reused the application's checked-out session.");
            ConsumerCheck.Require(
                contextFactory.CreatedCount == 2 && contextFactory.DisposedCount == 2,
                $"Every Entity Framework Core run owns exactly one context: created={contextFactory.CreatedCount}, disposed={contextFactory.DisposedCount}.");

            // The listening session started in leg 1 is still alive after both contexts and both COPY connections
            // have been released, which is what independent lifetimes mean here.
            await ConsumerSql.ExecuteAsync(application, $"NOTIFY {EfCoreScenario.Channel}, 'after-efcore'");
            var afterEfCore = await listenerRun.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            ConsumerCheck.Require(afterEfCore.Result.IsSuccess, "The LISTEN source published a failing notification result.");

            var firstNotification = beforeEfCore.Result.Value!;
            var secondNotification = afterEfCore.Result.Value!;
            ConsumerCheck.Require(
                firstNotification.Channel == EfCoreScenario.Channel
                    && firstNotification.Payload == "before-efcore"
                    && firstNotification.BackendProcessId == applicationBackendId,
                "The first LISTEN delivery did not match the notification the application raised.");
            ConsumerCheck.Require(
                secondNotification.Channel == EfCoreScenario.Channel
                    && secondNotification.Payload == "after-efcore"
                    && secondNotification.BackendProcessId == applicationBackendId,
                "The listening session did not survive the Entity Framework Core and COPY lifetimes.");

            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(application, "SELECT count(*) FROM pg_listening_channels()") == 0,
                "The application's own connection must not be the session that listens.");
            ConsumerCheck.Require(
                firstNotification.BackendProcessId == applicationBackendId
                    && secondNotification.BackendProcessId == applicationBackendId,
                "Both deliveries must originate from a NOTIFY raised on the application's own session.");

            // Neither production package gained a dependency on the other; the Entity Framework Core package stays
            // provider-neutral and never references Npgsql.
            ConsumerAssemblyGuard.RequireNoReference(
                typeof(PostgreSqlPipelineComponents).Assembly,
                "SmartPipe.Extensions.EntityFrameworkCore");
            ConsumerAssemblyGuard.RequireNoReference(
                typeof(EfCorePipelineDefinitionBuilder).Assembly,
                "SmartPipe.Extensions.PostgreSql");
            ConsumerAssemblyGuard.RequireNoReference(
                typeof(EfCorePipelineDefinitionBuilder).Assembly,
                "Npgsql");
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

        // The application-owned data source is still usable and still open after every pipeline run.
        await using (var reuse = await dataSource.OpenConnectionAsync())
        {
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(reuse, $"SELECT count(*) FROM {EfCoreScenario.ItemsTable}") == 7,
                "The application-owned data source was no longer usable after every leg had run.");
        }
    }
    finally
    {
        await application.DisposeAsync();
    }
}
finally
{
    await ConsumerSql.DropSchemaAsync(dataSource, EfCoreScenario.Schema);
}

Console.WriteLine("CONSUMER_OK postgresql-efcore-composition");
return 0;
