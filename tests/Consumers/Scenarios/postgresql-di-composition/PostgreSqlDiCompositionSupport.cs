using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql;

/// <summary>Deterministic assertions for a process-backed consumer scenario.</summary>
internal static class ConsumerCheck
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

/// <summary>A row of the scenario's PostgreSQL table.</summary>
internal sealed record Row(int Id, string Name);

/// <summary>Reads the application's PostgreSQL configuration from the environment.</summary>
internal static class ConsumerEnvironment
{
    private const string ConnectionStringVariable = "SMARTPIPE_POSTGRES_CONNECTION_STRING";

    public static string RequireConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is required; the consumer scenario never falls back to a hardcoded connection string.");

        return connectionString;
    }
}

/// <summary>Plain NpgsqlCommand helpers owned by the application, never by a pipeline component.</summary>
internal static class ConsumerSql
{
    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
    }

    public static async Task DropSchemaAsync(NpgsqlDataSource dataSource, string schema)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup only: a failing drop must never replace the primary scenario result, and the next run
            // recreates the schema from scratch.
        }
    }
}

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
