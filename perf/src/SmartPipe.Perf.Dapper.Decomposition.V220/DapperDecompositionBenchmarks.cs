using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.Data.Sqlite;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper;

namespace SmartPipe.Perf.DapperDecomposition;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("V220Only", "Dapper", "Decomposition")]
public class DapperDecompositionBenchmarks
{
    private const string ReadAllSql = "select Id, Name from Rows order by Id;";
    private const string ReadSingleSql = "select Id, Name from Rows where Id = @Id;";

    private SqliteBenchmarkDatabase? _database;
    private PipelineDefinition<DapperRow, DapperRow>? _readAll;
    private PipelineDefinition<DapperRow, DapperRow>? _readSingle;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await SqliteBenchmarkDatabase.CreateAsync().ConfigureAwait(false);

        _readAll = CreateDefinition(
            new PipelineKey("perf-dapper-decomp-all"),
            ReadAllSql,
            parametersFactory: null,
            "decomp-read-all");

        _readSingle = CreateDefinition(
            new PipelineKey("perf-dapper-decomp-single"),
            ReadSingleSql,
            static _ => new { Id = 42L },
            "decomp-read-single");

        AssertObservation(await RawHundredRows().ConfigureAwait(false), 100, 5050, nameof(RawHundredRows));
        AssertObservation(await PipelineHundredRows().ConfigureAwait(false), 100, 5050, nameof(PipelineHundredRows));
        AssertObservation(await RawSingle().ConfigureAwait(false), 1, 42, nameof(RawSingle));
        AssertObservation(await PipelineSingle().ConfigureAwait(false), 1, 42, nameof(PipelineSingle));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync().ConfigureAwait(false);

        _database = null;
        _readAll = null;
        _readSingle = null;
    }

    [Benchmark(Baseline = true)]
    public Task<QueryObservation> RawSingle() =>
        RawQueryAsync(ReadSingleSql, new { Id = 42L });

    [Benchmark]
    public Task<QueryObservation> PipelineSingle() =>
        RunPipelineAsync(ReadSingleDefinition);

    [Benchmark]
    public Task<QueryObservation> RawHundredRows() =>
        RawQueryAsync(ReadAllSql, parameters: null);

    [Benchmark]
    public Task<QueryObservation> PipelineHundredRows() =>
        RunPipelineAsync(ReadAllDefinition);

    private SqliteBenchmarkDatabase Database =>
        _database ?? throw new InvalidOperationException("Dapper decomposition database is not initialized.");

    private PipelineDefinition<DapperRow, DapperRow> ReadAllDefinition =>
        _readAll ?? throw new InvalidOperationException("Dapper all-rows definition is not initialized.");

    private PipelineDefinition<DapperRow, DapperRow> ReadSingleDefinition =>
        _readSingle ?? throw new InvalidOperationException("Dapper single-row definition is not initialized.");

    private PipelineDefinition<DapperRow, DapperRow> CreateDefinition(
        PipelineKey key,
        string sql,
        Func<PipelineActivationContext, object?>? parametersFactory,
        string operationName)
    {
        var source = DapperPipelineComponents.QuerySource<DapperRow>(
            (_, _) => ValueTask.FromResult<DbConnection>(Database.CreateConnection()),
            sql,
            new DapperQueryOptions { OperationName = operationName },
            parametersFactory,
            MapRow);

        return PipelineDefinitionBuilder
            .From(key, source)
            .Build();
    }

    private async Task<QueryObservation> RawQueryAsync(string sql, object? parameters)
    {
        await using SqliteConnection connection = Database.CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        var command = new CommandDefinition(sql, parameters);
        await using DbDataReader reader =
            await connection.ExecuteReaderAsync(command).ConfigureAwait(false);

        int count = 0;
        long checksum = 0;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            DapperRow row = MapRow(reader);
            count++;
            checksum += row.Id;
        }

        return new QueryObservation(count, checksum);
    }

    private static async Task<QueryObservation> RunPipelineAsync(
        PipelineDefinition<DapperRow, DapperRow> definition)
    {
        await using PipelineRun<DapperRow> run =
            await definition.StartAsync().ConfigureAwait(false);

        int count = 0;
        long checksum = 0;

        await foreach (PipelineResult<DapperRow> result in run.ReadResultsAsync().ConfigureAwait(false))
        {
            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(
                    $"Dapper decomposition pipeline produced non-success result '{result.Kind}'.");
            }

            count++;
            checksum += result.Value.Id;
        }

        await run.Completion.ConfigureAwait(false);
        return new QueryObservation(count, checksum);
    }

    private static DapperRow MapRow(DbDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
        };

    private static void AssertObservation(
        QueryObservation observation,
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

public readonly record struct QueryObservation(int Count, long Checksum);

public sealed class DapperRow
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
        var databaseName = $"smartpipe-perf-dapper-decomp-{Guid.NewGuid():N}";
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
            create.CommandText = "create table Rows (Id integer primary key, Name text not null);";
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
