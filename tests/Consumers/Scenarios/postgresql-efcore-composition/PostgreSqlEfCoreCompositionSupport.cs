using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;

namespace SmartPipe.Consumer.PostgreSql;



/// <summary>The names this scenario owns inside the shared test database.</summary>
internal static class EfCoreScenario
{
    public const string Schema = "sp_consumer_postgresql_efcore_composition";
    public const string ItemsTable = Schema + ".items";
    public const string Channel = "sp_efcore_composition_channel";
}

/// <summary>Proves the direction of the package dependency graph from the deployed assemblies.</summary>
internal static class ConsumerAssemblyGuard
{
    public static void RequireNoReference(Assembly assembly, string forbiddenSimpleName)
    {
        var referenced = assembly.GetReferencedAssemblies();
        foreach (var reference in referenced)
        {
            if (string.Equals(reference.Name, forbiddenSimpleName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The deployed assembly '{assembly.GetName().Name}' references '{forbiddenSimpleName}'.");
            }
        }
    }
}

/// <summary>Reads Entity Framework Core scenario rows from the application-owned connection.</summary>
internal static partial class ConsumerSql
{
    public static async Task<List<ItemRow>> ReadItemsAsync(NpgsqlConnection connection, string sql)
    {
        var rows = new List<ItemRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new ItemRow
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Total = reader.GetDecimal(2),
            });
        }

        return rows;
    }
}

/// <summary>A row of the scenario's table, mapped by both Entity Framework Core and binary COPY.</summary>
internal sealed class ItemRow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Total { get; set; }
}

/// <summary>The application's provider-neutral Entity Framework Core model.</summary>
internal sealed class ItemsContext(DbContextOptions<ItemsContext> options, Action onDisposed) : DbContext(options)
{
    private int _disposed;

    public DbSet<ItemRow> Items => Set<ItemRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ItemRow>(entity =>
        {
            entity.ToTable("items", EfCoreScenario.Schema);
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.Name).HasColumnName("name").IsRequired();
            entity.Property(item => item.Total).HasColumnName("total");
        });
    }

    public override void Dispose()
    {
        base.Dispose();
        RecordDisposal();
    }

    public override ValueTask DisposeAsync()
    {
        var disposal = base.DisposeAsync();
        RecordDisposal();
        return disposal;
    }

    private void RecordDisposal()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            onDisposed();
    }
}

/// <summary>
/// The application's context factory. It counts context creation and disposal so the scenario can prove that the
/// runtime — not the application and not the COPY or LISTEN connection — owns the run's context lifetime.
/// </summary>
internal sealed class TrackingItemsContextFactory(DbContextOptions<ItemsContext> options) : IDbContextFactory<ItemsContext>
{
    private int _created;
    private int _disposed;

    public int CreatedCount => Volatile.Read(ref _created);

    public int DisposedCount => Volatile.Read(ref _disposed);

    public ItemsContext CreateDbContext() => CreateTrackedContext();

    public Task<ItemsContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateTrackedContext());
    }

    private ItemsContext CreateTrackedContext()
    {
        Interlocked.Increment(ref _created);
        return new ItemsContext(options, () => Interlocked.Increment(ref _disposed));
    }
}

/// <summary>What one compiled Entity Framework Core probe observed on its own session.</summary>
internal sealed record EfCoreProbe(int BackendProcessId, int VisibleRows);

/// <summary>The compiled Entity Framework Core query used by this scenario.</summary>
internal static class EfCoreProbeQuery
{
    public static async IAsyncEnumerable<EfCoreProbe> ProbeAsync(
        ItemsContext context,
        PipelineActivationContext activation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = activation;
        var backendProcessId = await context.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var visibleRows = await context.Items.CountAsync(cancellationToken).ConfigureAwait(false);
        yield return new EfCoreProbe(backendProcessId, visibleRows);
    }
}

/// <summary>The per-row COPY callbacks used by this scenario.</summary>
internal static class ItemCallbacks
{
    public static async ValueTask WriteItemAsync(NpgsqlBinaryImporter importer, ItemRow row, CancellationToken cancellationToken)
    {
        await importer.WriteAsync(row.Id, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Total, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
    }
}





/// <summary>Observes a definition whose later batch is expected to fail after an earlier batch committed.</summary>
internal static class FailingPipeline
{
    public static async Task<FailingPipelineOutcome> ObserveAsync<T>(PipelineDefinition<T, T> definition)
    {
        var successfulOutputs = 0;
        var failedOutputs = 0;
        Exception? failure = null;
        var run = await definition.StartAsync().ConfigureAwait(false);
        try
        {
            await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
            {
                if (output.Result.IsSuccess)
                    successfulOutputs++;
                else
                    failedOutputs++;
            }

            await run.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }

        return new FailingPipelineOutcome(successfulOutputs, failedOutputs, failure);
    }
}

/// <summary>What a deliberately failing run reported.</summary>
internal sealed record FailingPipelineOutcome(int SuccessfulOutputs, int FailedOutputs, Exception? Failure)
{
    public bool ObservedFailure => Failure is not null || FailedOutputs > 0;
}
