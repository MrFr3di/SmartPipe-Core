using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace SmartPipe.Perf.EntityFrameworkCore;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Evolution", "EntityFrameworkCore")]
public class EntityFrameworkCoreEvolutionBenchmarks
{
    private EntityFrameworkCoreEvolutionTarget? _target;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _target = await EntityFrameworkCoreEvolutionTarget.CreateAsync().ConfigureAwait(false);

        EfQueryObservation all = await Target.ReadHundredRowsAsync().ConfigureAwait(false);
        if (all.Count != 100 || all.Checksum != 5050)
        {
            throw new InvalidOperationException(
                $"EF Core all-rows precheck expected count=100 checksum=5050, got count={all.Count} checksum={all.Checksum}.");
        }

        EfQueryObservation single = await Target.ReadSingleFilteredAsync().ConfigureAwait(false);
        if (single.Count != 1 || single.Checksum != 42)
        {
            throw new InvalidOperationException(
                $"EF Core filtered precheck expected count=1 checksum=42, got count={single.Count} checksum={single.Checksum}.");
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_target is not null)
            await _target.DisposeAsync().ConfigureAwait(false);
        _target = null;
    }

    [Benchmark]
    public Task<EfQueryObservation> ReadHundredRows() =>
        Target.ReadHundredRowsAsync();

    [Benchmark]
    public Task<EfQueryObservation> ReadSingleFiltered() =>
        Target.ReadSingleFilteredAsync();

    private EntityFrameworkCoreEvolutionTarget Target =>
        _target ?? throw new InvalidOperationException("EF Core benchmark target is not initialized.");
}

public readonly record struct EfQueryObservation(int Count, long Checksum);

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

    private EfBenchmarkDatabase(SqliteConnection connection) =>
        _connection = connection;

    internal static async Task<EfBenchmarkDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);

        var database = new EfBenchmarkDatabase(connection);

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

    internal EfBenchmarkContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<EfBenchmarkContext>()
            .UseSqlite(_connection)
            .Options;

        return new EfBenchmarkContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var context = CreateContext())
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
