using System.Data.Common;
using SmartPipe.Extensions.Selectors;

namespace SmartPipe.Perf.Dapper;

internal sealed class DapperEvolutionTarget : IAsyncDisposable
{
    private const string ReadAllSql =
        "select Id, Name from Rows order by Id;";
    private const string ReadSingleSql =
        "select Id, Name from Rows where Id = @Id;";

    private readonly SqliteBenchmarkDatabase _database;

    private DapperEvolutionTarget(SqliteBenchmarkDatabase database) =>
        _database = database;

    internal static async Task<DapperEvolutionTarget> CreateAsync() =>
        new(await SqliteBenchmarkDatabase.CreateAsync().ConfigureAwait(false));

    internal Task<QueryObservation> ReadHundredRowsAsync() =>
        ReadAsync(ReadAllSql, parameters: null);

    internal Task<QueryObservation> ReadSingleParameterizedAsync() =>
        ReadAsync(ReadSingleSql, new { Id = 42L });

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private async Task<QueryObservation> ReadAsync(string sql, object? parameters)
    {
        var connection = _database.CreateConnection();
        await using var selector = new DapperSelector<DapperRow>(
            connection,
            sql,
            MapRow,
            parameters,
            leaveOpen: false,
            commandTimeout: 30,
            logger: null);

        await selector.InitializeAsync().ConfigureAwait(false);

        int count = 0;
        long checksum = 0;
        await foreach (var envelope in selector.ReadEnvelopesAsync().ConfigureAwait(false))
        {
            count++;
            checksum += envelope.Payload.Id;
        }

        return new QueryObservation(count, checksum);
    }

    private static DapperRow MapRow(DbDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
        };
}
