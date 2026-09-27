using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.PostgreSql;
using SmartPipe.Consumer.PostgreSql;

// The application builds the data source and the PostgreSQL definition; SmartPipe.Extensions.DependencyInjection
// only registers the finished definition, and the PostgreSQL package itself stays free of any DI dependency.
var connectionString = ConsumerEnvironment.RequireConnectionString();
const string Schema = "sp_consumer_postgresql_di";

await using var dataSource = NpgsqlDataSource.Create(connectionString);
try
{
    var verification = await dataSource.OpenConnectionAsync();
    try
    {
        await ConsumerSql.ExecuteAsync(
            verification,
            $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.rows (id integer PRIMARY KEY, name text NOT NULL);
            INSERT INTO {Schema}.rows (id, name) VALUES (1, 'Ada'), (2, 'Grace');
            """);

        var key = new PipelineKey("postgresql-di-composition");
        var definition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<Row>(
                key,
                dataSource,
                $"COPY (SELECT id, name FROM {Schema}.rows ORDER BY id) TO STDOUT (FORMAT BINARY)",
                CopyCallbacks.ReadRowAsync,
                new PostgreSqlBinaryCopySourceOptions
                {
                    OperationName = "postgresql-di-composition",
                    ExpectedColumnCount = 2,
                })
            .Build();

        // The application also registers the data source it owns, so the scenario can prove that dependency
        // injection and the definition work with the exact same instance.
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSmartPipe().AddPipeline(definition);
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var resolvedDataSource = provider.GetRequiredService<NpgsqlDataSource>();
        ConsumerCheck.Require(
            ReferenceEquals(resolvedDataSource, dataSource),
            "Dependency injection did not resolve the exact application-owned data source instance the definition captures.");
        ConsumerCheck.Require(
            ReferenceEquals(provider.GetRequiredKeyedService<PipelineDefinition<Row, Row>>(key.Value), definition),
            "Dependency injection did not register the exact PostgreSQL definition instance.");

        var factory = provider.GetRequiredService<ISmartPipeFactoryProvider>().GetFactory<Row, Row>(key);
        var rows = new List<Row>();
        await using (var run = await factory.StartAsync())
        {
            await foreach (var output in run.Outputs.ReadAllAsync())
            {
                ConsumerCheck.Require(output.Result.IsSuccess, "The dependency-injection registered pipeline published a failure.");
                rows.Add(output.Result.Value!);
            }

            await run.Completion;
            ConsumerCheck.Require(run.PipelineKey == key, "The dependency-injection registered run did not preserve the pipeline key.");
        }

        ConsumerCheck.Require(
            rows.SequenceEqual([new Row(1, "Ada"), new Row(2, "Grace")]),
            "The dependency-injection registered pipeline did not stream the seeded rows in order from the real server.");

        // The PostgreSQL package stays provider-native: it must not reference the DI integration at all.
        var referencedSmartPipeAssemblies = PostgreSqlPackageBoundary.ReferencedSmartPipeAssemblies();
        ConsumerCheck.Require(
            referencedSmartPipeAssemblies.SequenceEqual(["SmartPipe.Core"]),
            $"SmartPipe.Extensions.PostgreSql must reference only SmartPipe.Core; observed [{string.Join(", ", referencedSmartPipeAssemblies)}].");

        // A fresh acquisition through the DI-resolved instance proves the data source is still open and usable after
        // the registered pipeline ran, which is the observable proof that nothing disposed it.
        await using (var reuse = await resolvedDataSource.OpenConnectionAsync())
        {
            ConsumerCheck.Require(
                await ConsumerSql.CountAsync(reuse, $"SELECT count(*) FROM {Schema}.rows") == 2,
                "The dependency-injection resolved data source was no longer usable after the registered pipeline ran.");
        }
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

Console.WriteLine("CONSUMER_OK postgresql-di-composition");
return 0;
