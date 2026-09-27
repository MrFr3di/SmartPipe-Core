using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;

namespace SmartPipe.Consumer.PostgreSql;




/// <summary>Reads direct scenario rows from the application-owned connection.</summary>
internal static partial class ConsumerSql
{
    public static async Task<List<Row>> ReadRowsAsync(NpgsqlConnection connection, string sql)
    {
        var rows = new List<Row>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            rows.Add(new Row(reader.GetInt32(0), reader.GetString(1)));

        return rows;
    }
}

/// <summary>The per-row COPY callbacks used by this scenario.</summary>
internal static class CopyCallbacks
{
    public static async ValueTask<Row> ReadRowAsync(NpgsqlBinaryExporter exporter, CancellationToken cancellationToken)
    {
        var id = await exporter.ReadAsync<int>(cancellationToken).ConfigureAwait(false);
        var name = await exporter.ReadAsync<string>(cancellationToken).ConfigureAwait(false);
        return new Row(id, name);
    }

    public static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, Row row, CancellationToken cancellationToken)
    {
        await importer.WriteAsync(row.Id, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
    }
}
