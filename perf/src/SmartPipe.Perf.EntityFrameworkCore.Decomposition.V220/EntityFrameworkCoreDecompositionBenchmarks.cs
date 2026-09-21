using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore;

namespace SmartPipe.Perf.EntityFrameworkCoreDecomposition;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("V220Only", "EntityFrameworkCore", "Decomposition")]
public class EntityFrameworkCoreDecompositionBenchmarks
{
    private static readonly Func<DecompositionContext, long, IAsyncEnumerable<DecompositionRow>> CompiledSingle =
        EF.CompileAsyncQuery(
            (DecompositionContext context, long id) =>
                context.Rows.AsNoTracking().Where(row => row.Id == id));

    private static readonly Func<DecompositionContext, long, IAsyncEnumerable<DecompositionRow>> CompiledHundred =
        EF.CompileAsyncQuery(
            (DecompositionContext context, long minId) =>
                context.Rows.AsNoTracking().Where(row => row.Id >= minId).OrderBy(row => row.Id));

    private DecompositionDatabase? _database;
    private PipelineDefinition<DecompositionRow, DecompositionRow>? _querySingle;
    private PipelineDefinition<DecompositionRow, DecompositionRow>? _queryHundred;
    private PipelineDefinition<DecompositionRow, DecompositionRow>? _compiledSingle;
    private PipelineDefinition<DecompositionRow, DecompositionRow>? _compiledHundred;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await DecompositionDatabase.CreateAsync().ConfigureAwait(false);

        _querySingle = BuildQueryDefinition(
            new PipelineKey("ef-decomp-query-single"),
            static context => context.Rows.Where(static row => row.Id == 42),
            "query-single");

        _queryHundred = BuildQueryDefinition(
            new PipelineKey("ef-decomp-query-hundred"),
            static context => context.Rows.OrderBy(static row => row.Id),
            "query-hundred");

        _compiledSingle = BuildCompiledDefinition(
            new PipelineKey("ef-decomp-compiled-single"),
            static context => CompiledSingle(context, 42),
            "compiled-single");

        _compiledHundred = BuildCompiledDefinition(
            new PipelineKey("ef-decomp-compiled-hundred"),
            static context => CompiledHundred(context, 1),
            "compiled-hundred");

        await AssertObservation(RawSingle(), 1, 42, nameof(RawSingle)).ConfigureAwait(false);
        await AssertObservation(PipelineSingle(), 1, 42, nameof(PipelineSingle)).ConfigureAwait(false);
        await AssertObservation(RawCompiledSingle(), 1, 42, nameof(RawCompiledSingle)).ConfigureAwait(false);
        await AssertObservation(CompiledPipelineSingle(), 1, 42, nameof(CompiledPipelineSingle)).ConfigureAwait(false);
        await AssertObservation(RawHundredRows(), 100, 5050, nameof(RawHundredRows)).ConfigureAwait(false);
        await AssertObservation(PipelineHundredRows(), 100, 5050, nameof(PipelineHundredRows)).ConfigureAwait(false);
        await AssertObservation(RawCompiledHundredRows(), 100, 5050, nameof(RawCompiledHundredRows)).ConfigureAwait(false);
        await AssertObservation(CompiledPipelineHundredRows(), 100, 5050, nameof(CompiledPipelineHundredRows)).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync().ConfigureAwait(false);

        _database = null;
        _querySingle = null;
        _queryHundred = null;
        _compiledSingle = null;
        _compiledHundred = null;
    }

    [Benchmark]
    public async Task<EfDecompositionObservation> RawSingle()
    {
        await using DecompositionContext context = Database.CreateContext();
        return await ObserveAsync(
            context.Rows
                .AsNoTracking()
                .Where(static row => row.Id == 42)
                .AsAsyncEnumerable()).ConfigureAwait(false);
    }

    [Benchmark]
    public Task<EfDecompositionObservation> PipelineSingle() =>
        ObservePipelineAsync(QuerySingle);

    [Benchmark]
    public async Task<EfDecompositionObservation> RawCompiledSingle()
    {
        await using DecompositionContext context = Database.CreateContext();
        return await ObserveAsync(CompiledSingle(context, 42)).ConfigureAwait(false);
    }

    [Benchmark]
    public Task<EfDecompositionObservation> CompiledPipelineSingle() =>
        ObservePipelineAsync(CompiledSingleDefinition);

    [Benchmark]
    public async Task<EfDecompositionObservation> RawHundredRows()
    {
        await using DecompositionContext context = Database.CreateContext();
        return await ObserveAsync(
            context.Rows
                .AsNoTracking()
                .OrderBy(static row => row.Id)
                .AsAsyncEnumerable()).ConfigureAwait(false);
    }

    [Benchmark]
    public Task<EfDecompositionObservation> PipelineHundredRows() =>
        ObservePipelineAsync(QueryHundred);

    [Benchmark]
    public async Task<EfDecompositionObservation> RawCompiledHundredRows()
    {
        await using DecompositionContext context = Database.CreateContext();
        return await ObserveAsync(CompiledHundred(context, 1)).ConfigureAwait(false);
    }

    [Benchmark]
    public Task<EfDecompositionObservation> CompiledPipelineHundredRows() =>
        ObservePipelineAsync(CompiledHundredDefinition);

    private DecompositionDatabase Database =>
        _database ?? throw new InvalidOperationException("EF decomposition database is not initialized.");

    private PipelineDefinition<DecompositionRow, DecompositionRow> QuerySingle =>
        _querySingle ?? throw new InvalidOperationException("Query single definition is not initialized.");

    private PipelineDefinition<DecompositionRow, DecompositionRow> QueryHundred =>
        _queryHundred ?? throw new InvalidOperationException("Query hundred definition is not initialized.");

    private PipelineDefinition<DecompositionRow, DecompositionRow> CompiledSingleDefinition =>
        _compiledSingle ?? throw new InvalidOperationException("Compiled single definition is not initialized.");

    private PipelineDefinition<DecompositionRow, DecompositionRow> CompiledHundredDefinition =>
        _compiledHundred ?? throw new InvalidOperationException("Compiled hundred definition is not initialized.");

    private PipelineDefinition<DecompositionRow, DecompositionRow> BuildQueryDefinition(
        PipelineKey key,
        Func<DecompositionContext, IQueryable<DecompositionRow>> query,
        string operationName)
    {
        var source = EfCorePipelineComponents.QuerySource<DecompositionContext, DecompositionRow>(
            (_, _) => ValueTask.FromResult(Database.CreateContext()),
            (context, _) => query(context),
            new EfCoreQueryOptions
            {
                OperationName = operationName,
                TrackingMode = EfCoreQueryTrackingMode.NoTracking,
            });

        return PipelineDefinitionBuilder.From(key, source).Build();
    }

    private PipelineDefinition<DecompositionRow, DecompositionRow> BuildCompiledDefinition(
        PipelineKey key,
        Func<DecompositionContext, IAsyncEnumerable<DecompositionRow>> query,
        string operationName)
    {
        var source = EfCorePipelineComponents.CompiledQuerySource<DecompositionContext, DecompositionRow>(
            (_, _) => ValueTask.FromResult(Database.CreateContext()),
            (context, _, _) => query(context),
            new EfCoreCompiledQueryOptions { OperationName = operationName });

        return PipelineDefinitionBuilder.From(key, source).Build();
    }

    private static async Task<EfDecompositionObservation> ObservePipelineAsync(
        PipelineDefinition<DecompositionRow, DecompositionRow> definition)
    {
        await using PipelineRun<DecompositionRow> run =
            await definition.StartAsync().ConfigureAwait(false);

        int count = 0;
        long checksum = 0;

        await foreach (PipelineResult<DecompositionRow> result in run.ReadResultsAsync().ConfigureAwait(false))
        {
            if (!result.IsSuccess || result.Value is null)
                throw new InvalidOperationException(
                    $"EF decomposition pipeline returned non-success result '{result.Kind}'.");

            count++;
            checksum += result.Value.Id;
        }

        await run.Completion.ConfigureAwait(false);
        return new EfDecompositionObservation(count, checksum);
    }

    private static async Task<EfDecompositionObservation> ObserveAsync(
        IAsyncEnumerable<DecompositionRow> rows)
    {
        int count = 0;
        long checksum = 0;

        await foreach (DecompositionRow row in rows.ConfigureAwait(false))
        {
            count++;
            checksum += row.Id;
        }

        return new EfDecompositionObservation(count, checksum);
    }

    private static async Task AssertObservation(
        Task<EfDecompositionObservation> observationTask,
        int expectedCount,
        long expectedChecksum,
        string operation)
    {
        EfDecompositionObservation observation = await observationTask.ConfigureAwait(false);
        if (observation.Count != expectedCount || observation.Checksum != expectedChecksum)
        {
            throw new InvalidOperationException(
                $"{operation} expected count={expectedCount}, checksum={expectedChecksum}; got count={observation.Count}, checksum={observation.Checksum}.");
        }
    }
}

public readonly record struct EfDecompositionObservation(int Count, long Checksum);

public sealed class DecompositionRow
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class DecompositionContext(DbContextOptions<DecompositionContext> options)
    : DbContext(options)
{
    internal DbSet<DecompositionRow> Rows => Set<DecompositionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DecompositionRow>().HasKey(static row => row.Id);
}

internal sealed class DecompositionDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private DecompositionDatabase(SqliteConnection connection) =>
        _connection = connection;

    internal static async Task<DecompositionDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);

        var database = new DecompositionDatabase(connection);

        try
        {
            await using DecompositionContext context = database.CreateContext();
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

            for (int id = 1; id <= 100; id++)
            {
                context.Rows.Add(
                    new DecompositionRow
                    {
                        Id = id,
                        Name = $"row-{id:D3}",
                    });
            }

            await context.SaveChangesAsync().ConfigureAwait(false);
            return database;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal DecompositionContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DecompositionContext>()
            .UseSqlite(_connection)
            .Options;

        return new DecompositionContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await using (DecompositionContext context = CreateContext())
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
