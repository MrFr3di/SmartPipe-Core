using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore;

namespace SmartPipe.Perf.EntityFrameworkCore;

internal sealed class EntityFrameworkCoreEvolutionTarget : IAsyncDisposable
{
    private readonly EfBenchmarkDatabase _database;
    private readonly PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> _readAll;
    private readonly PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> _readSingle;

    private EntityFrameworkCoreEvolutionTarget(EfBenchmarkDatabase database)
    {
        _database = database;
        _readAll = CreateDefinition(
            new PipelineKey("perf-ef-all"),
            static (context, _) => context.Rows.OrderBy(static row => row.Id),
            "read-all");
        _readSingle = CreateDefinition(
            new PipelineKey("perf-ef-single"),
            static (context, _) => context.Rows.Where(static row => row.Id == 42),
            "read-single");
    }

    internal static async Task<EntityFrameworkCoreEvolutionTarget> CreateAsync() =>
        new(await EfBenchmarkDatabase.CreateAsync().ConfigureAwait(false));

    internal Task<EfQueryObservation> ReadHundredRowsAsync() =>
        RunAsync(_readAll);

    internal Task<EfQueryObservation> ReadSingleFilteredAsync() =>
        RunAsync(_readSingle);

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private PipelineDefinition<EfBenchmarkRow, EfBenchmarkRow> CreateDefinition(
        PipelineKey key,
        Func<EfBenchmarkContext, PipelineActivationContext, IQueryable<EfBenchmarkRow>> queryFactory,
        string operationName)
    {
        var source = EfCorePipelineComponents.QuerySource<EfBenchmarkContext, EfBenchmarkRow>(
            (_, _) => ValueTask.FromResult(_database.CreateContext()),
            queryFactory,
            new EfCoreQueryOptions
            {
                OperationName = operationName,
                TrackingMode = EfCoreQueryTrackingMode.NoTracking,
            });

        return PipelineDefinitionBuilder
            .From(key, source)
            .Build();
    }

    private static async Task<EfQueryObservation> RunAsync(
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
                    $"EF Core candidate run produced non-success result '{result.Kind}'.");
            }

            count++;
            checksum += result.Value.Id;
        }

        await run.Completion.ConfigureAwait(false);
        return new EfQueryObservation(count, checksum);
    }
}
