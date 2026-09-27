using System.Diagnostics;
using System.Reflection;
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

/// <summary>The names this scenario owns inside the shared test database.</summary>
internal static class TelemetryScenario
{
    public const string Schema = "sp_consumer_postgresql_opentelemetry_composition";
    public const string RowsTable = Schema + ".telemetry_rows";
    public const string CopyInPipelineId = "postgresql-opentelemetry-composition-copy-in";
    public const string CopyOutPipelineId = "postgresql-opentelemetry-composition-copy-out";
}

/// <summary>Proves a deployed assembly carries no dependency on a forbidden assembly name fragment.</summary>
internal static class ConsumerAssemblyGuard
{
    public static void RequireNoReferenceContaining(Assembly assembly, string forbiddenFragment)
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is not null
                && reference.Name.Contains(forbiddenFragment, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The deployed assembly '{assembly.GetName().Name}' references '{reference.Name}'.");
            }
        }
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

/// <summary>A row of the scenario's table.</summary>
internal sealed record TelemetryRow(int Id, string Name);

/// <summary>Walks the exported activity graph without assuming a fixed stage depth.</summary>
internal static class ActivityGraph
{
    private const int MaximumDepth = 32;

    public static Activity? FindSmartPipeAncestor(
        Activity activity,
        IReadOnlyDictionary<ActivitySpanId, Activity> activitiesBySpanId)
    {
        var parentSpanId = activity.ParentSpanId;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (parentSpanId == default || !activitiesBySpanId.TryGetValue(parentSpanId, out var parent))
                return null;

            if (string.Equals(parent.Source.Name, SmartPipeDiagnostics.ActivitySourceName, StringComparison.Ordinal))
                return parent;

            parentSpanId = parent.ParentSpanId;
        }

        return null;
    }

    /// <summary>True when the activity carries any database client attribute.</summary>
    public static bool CarriesDatabaseAttribute(Activity activity)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Key.StartsWith("db.", StringComparison.Ordinal)
                || tag.Key.StartsWith("server.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? Text(Activity activity, string attributeName) =>
        activity.GetTagItem(attributeName) as string;
}

/// <summary>The per-row COPY callbacks used by this scenario.</summary>
internal static class TelemetryCallbacks
{
    public static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, TelemetryRow row, CancellationToken cancellationToken)
    {
        await importer.WriteAsync(row.Id, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<TelemetryRow> ReadRowAsync(
        NpgsqlBinaryExporter exporter,
        int columnCount,
        CancellationToken cancellationToken)
    {
        ConsumerCheck.Require(columnCount == 2, "The COPY OUT export reported an unexpected column count.");
        var id = await exporter.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        var name = await exporter.ReadAsync<string>(NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        return new TelemetryRow(id, name);
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
    public static async Task<List<T>> ReadAllAsync<T>(PipelineDefinition<T, T> definition)
    {
        var values = new List<T>();
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        {
            ConsumerCheck.Require(output.Result.IsSuccess, "The pipeline published a failing result.");
            values.Add(output.Result.Value!);
        }

        await run.Completion.ConfigureAwait(false);
        return values;
    }
}

/// <summary>
/// Runs a sink-only definition to completion. Core's default output policy suppresses successful outputs once a sink
/// is attached, so a completed COPY IN is observed through the database, never through the output channel.
/// </summary>
internal static class PipelineCompletion
{
    public static async Task RunAsync<T>(PipelineDefinition<T, T> definition)
    {
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
            ConsumerCheck.Require(output.Result.IsSuccess, "The sink-only pipeline published a failing result.");
        await run.Completion.ConfigureAwait(false);
    }
}
