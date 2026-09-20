using Microsoft.Data.Sqlite;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

var databasePath = Path.Combine(Path.GetTempPath(), $"smartpipe-dapper-facade-binary-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={databasePath};Pooling=False";
try
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            CREATE TABLE "In" (Id INTEGER NOT NULL, Name TEXT NOT NULL);
            CREATE TABLE "Out" (Id INTEGER NOT NULL, Name TEXT NOT NULL);
            INSERT INTO "In" (Id, Name) VALUES (1, 'Ada');
            """;
        await command.ExecuteNonQueryAsync();
    }

    await using var selector = new DapperSelector<Row>(connection, "SELECT Id, Name FROM \"In\"", reader => new Row(reader.GetInt32(0), reader.GetString(1)));
    await selector.InitializeAsync();
    Row? captured = null;
    await foreach (var envelope in selector.ReadEnvelopesAsync())
        captured = envelope.Payload;
    if (captured is null)
        return 1;

    await using (var sink = new DbSink<Row>(connection, "INSERT INTO \"Out\" (Id, Name) VALUES (@Id, @Name)", leaveOpen: true))
    {
        await sink.InitializeAsync();
        await sink.WriteAsync(ProcessingEnvelope<Row>.Create(captured), CancellationToken.None);
    }

    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT COUNT(*) FROM \"Out\" WHERE Id = 1 AND Name = 'Ada'";
        if ((long)(await command.ExecuteScalarAsync() ?? 0L) != 1L)
            return 1;
    }
}
finally
{
    File.Delete(databasePath);
}

Console.WriteLine("CONSUMER_OK dapper-facade-binary-2.1.2");
return 0;

internal sealed record Row(int Id, string Name);
