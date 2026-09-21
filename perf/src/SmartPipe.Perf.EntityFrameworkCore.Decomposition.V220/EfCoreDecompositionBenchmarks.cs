using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore;

namespace SmartPipe.Perf.EfCoreDecomposition;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("V220Only", "EntityFrameworkCore", "Decomposition")]
public class EfCoreDecompositionBenchmarks
{
    private static readonly Func<EfBenchmarkContext, long, IAsyncEnumerable<EfBenchmarkRow>> CompiledFromId =
        EF.CompileAsyncQuery(
            (EfBenchmarkContext context, long minId) =>
                context.Rows
                    .AsNoTracking()
                    .Where(row => row.Id >= minId)
                    .OrderBy(row => row.Id));

    private static readonly Func<EfBenchmarkContext, long, IAsyncEnumerable<EfBenchmarkRow>> CompiledById =
        EF.CompileAsyncQuery(
            (EfBenchmarkContext context, long id) =>
                context.Rows
                    .AsNoTracking()
                    .Where(row => row.Id == id));

    private EfBenchmarkDatabase? _database;
    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow>? _normalAll;
    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow>? _normalSingle;
    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow>? _compiledAll;
    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow>? _compiledSingle;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await EfBenchmarkDatabase.CreateAsync().ConfigureAwait(false);

        _normalAll = CreateNormalDefinition(
            new PipelineKey("perf-ef-decomp-normal-all"),
            static (context, _) => context.Rows.OrderBy(static row => row.Id),
            "decomp-normal-all");

        _normalSingle = CreateNormalDefinition(
            new PipelineKey("perf-ef-decomp-normal-single"),
            static (context, _) => context.Rows.Where(static row => row.Id == 42),
            "decomp-normal-single");

        _compiledAll = CreateCompiledDefinition(
            new PipelineKey("perf-ef-decomp-compiled-all"),
            static (context, _, _) => CompiledFromId(context, 1),
            "decomp-compiled-all");

        _compiledSingle = CreateCompiledDefinition(
            new PipelineKey("perf-ef-decomp-compiled-single"),
            static (context, _, _) => CompiledById(context, 42),
            "decomp-compiled-single");

        AssertObservation(await RawSingle().ConfigureAwait(false), 1, 42, nameof(RawSingle));
        AssertObservation(await PipelineSingle().ConfigureAwait(false), 1, 42, nameof(PipelineSingle));
        AssertObservation(await RawCompiledSingle().ConfigureAwait(false), 1, 42, nameof(RawCompiledSingle));
        AssertObservation(await CompiledPipelineSingle().ConfigureAwait(false), 1, 42, nameof(CompiledPipelineSingle));
        AssertObservation(await RawHundredRows().ConfigureAwait(false), 100, 5050, nameof(RawHundredRows));
        AssertObservation(await PipelineHundredRows().ConfigureAwait(false), 100, 5050, nameof(PipelineHundredRows));
        AssertObservation(await RawCompiledHundredRows().ConfigureAwait(false), 100, 5050, nameof(RawCompiledHundredRows));
        AssertObservation(await CompiledPipelineHundredRows().ConfigureAwait(false), 100, 5050, nameof(CompiledPipelineHundredRows));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync().ConfigureAwait(false);

        _database = null;
        _normalAll = null;
        _normalSingle = null;
        _compiledAll = null;
        _compiledSingle = null;
    }

    [Benchmark(Baseline = true)]
    public Task<EfObservation> RawSingle() =>
        RawQueryAsync(static context => context.Rows.AsNoTracking().Where(static row => row.Id == 42));

    [Benchmark]
    public Task<EfObservation> PipelineSingle() =>
        RunPipelineAsync(NormalSingleDefinition);

    [Benchmark]
    public Task<EfObservation> RawCompiledSingle() =>
        RawCompiledQueryAsync(static context => CompiledById(context, 42));

    [Benchmark]
    public Task<EfObservation> CompiledPipelineSingle() =>
        RunPipelineAsync(CompiledSingleDefinition);

    [Benchmark]
    public Task<EfObservation> RawHundredRows() =>
        RawQueryAsync(static context => context.Rows.AsNoTracking().OrderBy(static row => row.Id));

    [Benchmark]
    public Task<EfObservation> PipelineHundredRows() =>
        RunPipelineAsync(NormalAllDefinition);

    [Benchmark]
    public Task<EfObservation> RawCompiledHundredRows() =>
        RawCompiledQueryAsync(static context => CompiledFromId(context, 1));

    [Benchmark]
    public Task<EfObservation> CompiledPipelineHundredRows() =>
        RunPipelineAsync(CompiledAllDefinition);

    private EfBenchmarkDatabase Database =>
        _database ?? throw new InvalidOperationException("EF decomposition database is not initialized.");

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> NormalAllDefinition =>
        _normalAll ?? throw new InvalidOperationException("Normal all-rows definition is not initialized.");

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> NormalSingleDefinition =>
        _normalSingle ?? throw new InvalidOperationException("Normal single-row definition is not initialized.");

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> CompiledAllDefinition =>
        _compiledAll ?? throw new InvalidOperationException("Compiled all-rows definition is not initialized.");

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> CompiledSingleDefinition =>
        _compiledSingle ?? throw new InvalidOperationException("Compiled single-row definition is not initialized.");

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> CreateNormalDefinition(
        PipelineKey key,
        Func<EfBenchmarkContext, PipelineActivationContext, IQueryable<EfBenchmarkRow>> queryFactory,
        string operationName)
    {
        var source = EfCorePipelineComponents.QuerySource<EfBenchmarkContext, EfBenchmarkRow>(
            (_, _) => ValueTask.FromResult(Database.CreateContext()),
            queryFactory,
            new EfCoreQueryOptions
            {
                OperationName = operationName,
                TrackingMode = EfCoreQueryTrackingMode.NoTracking,
            });

        return PipelineDefinitionBuilder.From(key, source).Build();
    }

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> CreateCompiledDefinition(
        PipelineKey key,
        Func<EfBenchmarkContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<EfBenchmarkRow>> compiledQuery,
        string operationName)
    {
        var source = EfCorePipelineComponents.CompiledQuerySource<EfBenchmarkContext, EfBenchmarkRow>(
            (_, _) => ValueTask.FromResult(Database.CreateContext()),
            compiledQuery,
            new EfCoreCompiledQueryOptions { OperationName = operationName });

        return PipelineDefinitionBuilder.From(key, source).Build();
    }

    private async Task<EfObservation> RawQueryAsync(
        Func<EfBenchmarkContext, IQueryable<EfBenchmarkRow>> queryFactory)
    {
        await using EfBenchmarkContext context = Database.CreateContext();
        IQueryable<EfBenchmarkRow> query = queryFactory(context);

        int count = 0;
        long checksum = 0;

        await foreach (EfBenchmarkRow row in query.AsAsyncEnumerable().ConfigureAwait(false))
        {
            count++;
            checksum += row.Id;
        }

        return new EfObservation(count, checksum);
    }

    private async Task<EfObservation> RawCompiledQueryAsync(
        Func<EfBenchmarkContext, IAsyncEnumerable<EfBenchmarkRow>> queryFactory)
    {
        await using EfBenchmarkContext context = Database.CreateContext();

        int count = 0;
        long checksum = 0;

        await foreach (EfBenchmarkRow row in queryFactory(context).ConfigureAwait(false))
        {
            count++;
            checksum += row.Id;
        }

        return new EfObservation(count, checksum);
    }

    private static async Task<EfObservation> RunPipelineAsync(
        PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> definition)
    {
        await using PipelineRun<EfBenchmarkRow> run =
            await definition.StartAsync().ConfigureAwait(false);

        int count = 0;
        long checksum = 0;

        await foreach (PipelineResult<EfBenchmarkRow> result in run.ReadResultsAsync().ConfigureAwait(false))
        {
            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(
                    $"EF decomposition pipeline produced non-success result '{result.Kind}'.");
            }

            count++;
            checksum += result.Value.Id;
        }

        await run.Completion.ConfigureAwait(false);
        return new EfObservation(count, checksum);
    }

    private static void AssertObservation(
        EfObservation observation,
        int expectedCount,
        long expectedChecksum,
        string operation)
    {
        if (observation.Count != expectedCount || observation.Checksum != expectedChecksum)
        {
            throw new InvalidOperationException(
                $"{operation} expected count={expectedCount}, checksum={expectedChecksum}; got count={observation.Count}, checksum={observation.Checksum}.");
        }
    }
}

public readonly record struct EfObservation(int Count, long Checksum);

public sealed class EfBenchmarkRow
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

internal sealed class EfBenchmarkContext : DbContext
{
    internal EfBenchmarkContext(DbContextOptions<EfBenchmarkContext> options)
        : base(options)
    {
    }

    internal DbSet<EfBenchmarkRow> Rows => Set<EfBenchmarkRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EfBenchmarkRow>().HasKey(static row => row.Id);
    }
}

internal sealed class EfBenchmarkDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<EfBenchmarkContext> _options;

    private EfBenchmarkDatabase(
        SqliteConnection connection,
        DbContextOptions<EfBenchmarkContext> options)
    {
        _connection = connection;
        _options = options;
    }

    internal static async Task<EfBenchmarkDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<EfBenchmarkContext>()
            .UseSqlite(connection)
            .Options;

        var database = new EfBenchmarkDatabase(connection, options);

        try
        {
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

            for (int id = 1; id <= 100; id++)
            {
                context.Rows.Add(
                    new EfBenchmarkRow
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

    internal EfBenchmarkContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await using (var context = CreateContext())
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
