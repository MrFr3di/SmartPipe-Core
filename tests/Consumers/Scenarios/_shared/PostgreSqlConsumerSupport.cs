using System.Runtime.CompilerServices;
using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Consumer.PostgreSql;

/// <summary>Deterministic assertions for a process-backed consumer scenario.</summary>
internal static class ConsumerCheck
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

/// <summary>A row shared by the direct, DI, and Dapper consumer scenarios.</summary>
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
internal static partial class ConsumerSql
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

    public static async Task<int> ReadBackendProcessIdAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0);
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
    public static async Task<List<T>> ReadAllAsync<T>(
        PipelineDefinition<T, T> definition,
        Func<T, Task>? verifyAsync = null)
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

/// <summary>Stops a deliberately infinite or in-flight run and proves cooperative cancellation.</summary>
internal static class RunCancellation
{
    public static async Task RequireCancelledAsync<T>(PipelineRun<T> run, string description)
    {
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await run.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelling an intentionally infinite or in-flight run is the expected stop.
        }

        ConsumerCheck.Require(
            run.State == PipelineRunState.Cancelled,
            $"{description} did not end in a cooperative cancellation: state={run.State}.");
    }
}
