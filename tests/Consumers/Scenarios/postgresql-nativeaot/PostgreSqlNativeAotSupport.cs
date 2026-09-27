using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;

namespace SmartPipe.Consumer.PostgreSql;



/// <summary>The names this scenario owns inside the shared test database.</summary>
internal static class AotScenario
{
    public const string Schema = "sp_consumer_postgresql_nativeaot";
    public const string RowsTable = Schema + ".aot_rows";
    public const string Channel = "sp_aot_channel";
    public const string CopyInPipelineId = "postgresql-nativeaot-copy-in";
    public const string CopyOutPipelineId = "postgresql-nativeaot-copy-out";
    public const string StreamingPipelineId = "postgresql-nativeaot-copy-out-cancellation";
    public const string ListenPipelineId = "postgresql-nativeaot-listen";
}


/// <summary>A row of the scenario's table. Only built-in primitive mappings are involved.</summary>
internal sealed record AotRow(int Id, string Name);

/// <summary>Static COPY callbacks: no dynamic JSON, no unmapped or composite mapping, no reflection.</summary>
internal static class AotCallbacks
{
    public static async ValueTask<AotRow> ReadRowAsync(
        NpgsqlBinaryExporter exporter,
        int columnCount,
        CancellationToken cancellationToken)
    {
        // The exporter has no ColumnCount property: the column count is the reader's second argument.
        ConsumerCheck.Require(columnCount == 2, "The COPY OUT export reported an unexpected column count.");
        var id = await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        var name = await exporter.ReadAsync<string>(NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        return new AotRow(id, name);
    }

    public static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, AotRow row, CancellationToken cancellationToken)
    {
        await importer.WriteAsync(row.Id, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
    }
}
