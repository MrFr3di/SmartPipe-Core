using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;

namespace SmartPipe.Perf.Dapper;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Evolution", "Dapper")]
public class DapperEvolutionBenchmarks
{
    private DapperEvolutionTarget? _target;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _target = await DapperEvolutionTarget.CreateAsync().ConfigureAwait(false);

        QueryObservation all = await Target.ReadHundredRowsAsync().ConfigureAwait(false);
        if (all.Count != 100 || all.Checksum != 5050)
        {
            throw new InvalidOperationException(
                $"Dapper all-rows precheck expected count=100 checksum=5050, got count={all.Count} checksum={all.Checksum}.");
        }

        QueryObservation single = await Target.ReadSingleParameterizedAsync().ConfigureAwait(false);
        if (single.Count != 1 || single.Checksum != 42)
        {
            throw new InvalidOperationException(
                $"Dapper parameter precheck expected count=1 checksum=42, got count={single.Count} checksum={single.Checksum}.");
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
    public Task<QueryObservation> ReadHundredRows() =>
        Target.ReadHundredRowsAsync();

    [Benchmark]
    public Task<QueryObservation> ReadSingleParameterized() =>
        Target.ReadSingleParameterizedAsync();

    private DapperEvolutionTarget Target =>
        _target ?? throw new InvalidOperationException("Dapper benchmark target is not initialized.");
}

public readonly record struct QueryObservation(int Count, long Checksum);

internal sealed class DapperRow
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

internal sealed class SqliteBenchmarkDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _anchor;

    private SqliteBenchmarkDatabase(string connectionString, SqliteConnection anchor)
    {
        ConnectionString = connectionString;
        _anchor = anchor;
    }

    internal string ConnectionString { get; }

    internal static async Task<SqliteBenchmarkDatabase> CreateAsync()
    {
        var databaseName = $"smartpipe-perf-dapper-{Guid.NewGuid():N}";
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseName,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();

        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync().ConfigureAwait(false);

        try
        {
            await using var create = anchor.CreateCommand();
            create.CommandText =
                "create table Rows (Id integer primary key, Name text not null);";
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);

            await using var transaction =
                (SqliteTransaction)await anchor.BeginTransactionAsync().ConfigureAwait(false);
            for (int id = 1; id <= 100; id++)
            {
                await using var insert = anchor.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "insert into Rows (Id, Name) values ($id, $name);";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$name", $"row-{id:D3}");
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            return new SqliteBenchmarkDatabase(connectionString, anchor);
        }
        catch
        {
            await anchor.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal SqliteConnection CreateConnection() => new(ConnectionString);

    public ValueTask DisposeAsync() => _anchor.DisposeAsync();
}
