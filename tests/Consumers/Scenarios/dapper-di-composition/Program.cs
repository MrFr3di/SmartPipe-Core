using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper;
using SmartPipe.Extensions.DependencyInjection;

var databasePath = Path.Combine(Path.GetTempPath(), $"smartpipe-dapper-di-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={databasePath};Pooling=False";
try
{
    await using (var seed = new SqliteConnection(connectionString))
    await using (var command = seed.CreateCommand())
    {
        await seed.OpenAsync();
        command.CommandText = """
            CREATE TABLE "In" (Id INTEGER NOT NULL, Name TEXT NOT NULL);
            INSERT INTO "In" (Id, Name) VALUES (1, 'Ada');
            """;
        await command.ExecuteNonQueryAsync();
    }

    var key = new PipelineKey("dapper-di-composition");
    var definition = DapperPipelineDefinitionBuilder
        .FromQuery<Row>(
            key,
            (context, cancellationToken) => ValueTask.FromResult<DbConnection>(new SqliteConnection(connectionString)),
            "SELECT Id, Name FROM \"In\" ORDER BY Id",
            new DapperQueryOptions { OperationName = "dapper-di-composition" },
            rowMapper: reader => new Row(reader.GetInt32(0), reader.GetString(1)))
        .Build();
    var services = new ServiceCollection();
    services.AddSmartPipe().AddPipeline(definition);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    var factory = provider.GetRequiredService<ISmartPipeFactoryProvider>().GetFactory<Row, Row>(key);
    await using var run = await factory.StartAsync();
    var result = await run.Outputs.ReadAsync();
    await run.Completion;
    if (!result.Result.IsSuccess || result.Result.Value != new Row(1, "Ada"))
        return 1;
}
finally
{
    File.Delete(databasePath);
}

Console.WriteLine("CONSUMER_OK dapper-di-composition");
return 0;

internal sealed record Row(int Id, string Name);
