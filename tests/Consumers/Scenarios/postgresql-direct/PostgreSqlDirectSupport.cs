using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;

/// <summary>Deterministic assertions for a process-backed consumer scenario.</summary>
internal static class ConsumerCheck
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

/// <summary>A row of the scenario's PostgreSQL tables.</summary>
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

/// <summary>Emits application-formed batches once per activation.</summary>
internal sealed class BatchSource<T>(IReadOnlyList<IReadOnlyList<T>> batches) : IPipelineSource<IReadOnlyList<T>>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<IReadOnlyList<T>>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ProcessingEnvelope<IReadOnlyList<T>>.Create(batch);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Builds Core definitions from application-formed batches.</summary>
internal static class BatchDefinitions
{
    public static PipelineDefinitionBuilder<IReadOnlyList<T>> FromBatches<T>(
        PipelineKey key,
        IReadOnlyList<IReadOnlyList<T>> batches) =>
        PipelineDefinitionBuilder.From(
            key,
            PipelineComponent.RuntimeOwned<IPipelineSource<IReadOnlyList<T>>>(
                (_, _) => ValueTask.FromResult<IPipelineSource<IReadOnlyList<T>>>(new BatchSource<T>(batches))));
}

/// <summary>Runs one definition to completion and collects its successful outputs.</summary>
internal static class PipelineDrain
{
    public static async Task<List<T>> ReadAllAsync<T>(PipelineDefinition<T, T> definition, Func<T, Task>? verifyAsync = null)
    {
        var values = new List<T>();
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        {
            ConsumerCheck.Require(output.Result.IsSuccess, "The pipeline published a failing result.");
            var value = output.Result.Value!;
            values.Add(value);
            if (verifyAsync is not null)
                await verifyAsync(value).ConfigureAwait(false);
        }

        await run.Completion.ConfigureAwait(false);
        return values;
    }
}
