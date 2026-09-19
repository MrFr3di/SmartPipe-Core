using System.Data.Common;
using Microsoft.Data.Sqlite;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper;

var inputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-dapper-direct-{Guid.NewGuid():N}-in.db");
var outputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-dapper-direct-{Guid.NewGuid():N}-out.db");
var inputConnectionString = $"Data Source={inputPath};Pooling=False";
var outputConnectionString = $"Data Source={outputPath};Pooling=False";
try
{
    await using (var seed = new SqliteConnection(inputConnectionString))
    await using (var command = seed.CreateCommand())
    {
        await seed.OpenAsync();
        command.CommandText = """
            CREATE TABLE "In" (Id INTEGER NOT NULL, Name TEXT NOT NULL);
            INSERT INTO "In" (Id, Name) VALUES (1, 'Ada');
            """;
        await command.ExecuteNonQueryAsync();
    }

    await using (var seed = new SqliteConnection(outputConnectionString))
    await using (var command = seed.CreateCommand())
    {
        await seed.OpenAsync();
        command.CommandText = """CREATE TABLE "Out" (Id INTEGER NOT NULL, Name TEXT NOT NULL);""";
        await command.ExecuteNonQueryAsync();
    }

    var definition = DapperPipelineDefinitionBuilder
        .FromQuery<Row>(
            new PipelineKey("dapper-direct"),
            (context, cancellationToken) => ValueTask.FromResult<DbConnection>(new SqliteConnection(inputConnectionString)),
            "SELECT Id, Name FROM \"In\" ORDER BY Id",
            new DapperQueryOptions { OperationName = "dapper-direct" },
            rowMapper: reader => new Row(reader.GetInt32(0), reader.GetString(1)))
        .ToCommand(
            (context, cancellationToken) => ValueTask.FromResult<DbConnection>(new SqliteConnection(outputConnectionString)),
            "INSERT INTO \"Out\" (Id, Name) VALUES (@Id, @Name)",
            new DapperSinkOptions { OperationName = "dapper-direct" },
            parameterFactory: envelope => new { envelope.Payload.Id, envelope.Payload.Name });

    await using (var run = await definition.StartAsync())
    {
        await run.Completion;
    }

    await using (var verification = new SqliteConnection(outputConnectionString))
    await using (var command = verification.CreateCommand())
    {
        await verification.OpenAsync();
        command.CommandText = "SELECT COUNT(*) FROM \"Out\" WHERE Id = 1 AND Name = 'Ada'";
        if ((long)(await command.ExecuteScalarAsync() ?? 0L) != 1L)
            return 1;
    }
}
finally
{
    File.Delete(inputPath);
    File.Delete(outputPath);
}

Console.WriteLine("CONSUMER_OK dapper-direct");
return 0;

internal sealed record Row(int Id, string Name);
