using System.Data.Common;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper;

namespace SmartPipe.Perf.Dapper;

internal sealed class DapperEvolutionTarget : IAsyncDisposable
{
    private const string ReadAllSql =
        "select Id, Name from Rows order by Id;";
    private const string ReadSingleSql =
        "select Id, Name from Rows where Id = @Id;";

    private readonly SqliteBenchmarkDatabase _database;
    private readonly PipelineDefinition<DapperRow, DapperRow> _readAll;
    private readonly PipelineDefinition<DapperRow, DapperRow> _readSingle;

    private DapperEvolutionTarget(SqliteBenchmarkDatabase database)
    {
        _database = database;
        _readAll = CreateDefinition(
            new PipelineKey("perf-dapper-all"),
            ReadAllSql,
            parametersFactory: null,
            "read-all");
        _readSingle = CreateDefinition(
            new PipelineKey("perf-dapper-single"),
            ReadSingleSql,
            static _ => new { Id = 42L },
            "read-single");
    }

    internal static async Task<DapperEvolutionTarget> CreateAsync() =>
        new(await SqliteBenchmarkDatabase.CreateAsync().ConfigureAwait(false));

    internal Task<QueryObservation> ReadHundredRowsAsync() =>
        RunAsync(_readAll);

    internal Task<QueryObservation> ReadSingleParameterizedAsync() =>
        RunAsync(_readSingle);

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private PipelineDefinition<DapperRow, DapperRow> CreateDefinition(
        PipelineKey key,
        string sql,
        Func<PipelineActivationContext, object?>? parametersFactory,
        string operationName)
    {
        var source = DapperPipelineComponents.QuerySource<DapperRow>(
            (_, _) => ValueTask.FromResult<DbConnection>(_database.CreateConnection()),
            sql,
            new DapperQueryOptions { OperationName = operationName },
            parametersFactory,
            MapRow);

        return PipelineDefinitionBuilder
            .From(key, source)
            .Build();
    }

    private static async Task<QueryObservation> RunAsync(
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
                    $"Dapper candidate run produced non-success result '{result.Kind}'.");
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
}
