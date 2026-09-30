using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql;

namespace SmartPipe.Consumer.PostgreSql;





/// <summary>The per-row COPY callback used by this scenario.</summary>
internal static class CopyCallbacks
{
    public static async ValueTask<Row> ReadRowAsync(
        NpgsqlBinaryExporter exporter,
        int columnCount,
        CancellationToken cancellationToken)
    {
        ConsumerCheck.Require(columnCount == 2, "The COPY OUT export reported an unexpected column count.");
        var id = await exporter.ReadAsync<int>(cancellationToken).ConfigureAwait(false);
        var name = await exporter.ReadAsync<string>(cancellationToken).ConfigureAwait(false);
        return new Row(id, name);
    }
}

/// <summary>Observes the static package boundary of the PostgreSQL integration from inside a consumer process.</summary>
internal static class PostgreSqlPackageBoundary
{
    public static IReadOnlyList<string> ReferencedSmartPipeAssemblies() =>
        typeof(PostgreSqlPipelineDefinitionBuilder).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("SmartPipe.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
