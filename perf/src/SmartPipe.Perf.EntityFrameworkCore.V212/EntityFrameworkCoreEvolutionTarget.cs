using SmartPipe.Extensions.Selectors;

namespace SmartPipe.Perf.EntityFrameworkCore;

internal sealed class EntityFrameworkCoreEvolutionTarget : IAsyncDisposable
{
    private readonly EfBenchmarkDatabase _database;

    private EntityFrameworkCoreEvolutionTarget(EfBenchmarkDatabase database) =>
        _database = database;

    internal static async Task<EntityFrameworkCoreEvolutionTarget> CreateAsync() =>
        new(await EfBenchmarkDatabase.CreateAsync().ConfigureAwait(false));

    internal Task<EfQueryObservation> ReadHundredRowsAsync() =>
        ReadAsync(static rows => rows.OrderBy(static row => row.Id));

    internal Task<EfQueryObservation> ReadSingleFilteredAsync() =>
        ReadAsync(static rows => rows.Where(static row => row.Id == 42));

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private async Task<EfQueryObservation> ReadAsync(
        Func<Microsoft.EntityFrameworkCore.DbSet<EfBenchmarkRow>, IQueryable<EfBenchmarkRow>> query)
    {
        await using var context = _database.CreateContext();
        await using var selector = new EfCoreSelector<EfBenchmarkRow>(context)
            .WithTracking(false)
            .WithQuery(query);

        await selector.InitializeAsync().ConfigureAwait(false);

        int count = 0;
        long checksum = 0;

        await foreach (var envelope in selector.ReadEnvelopesAsync().ConfigureAwait(false))
        {
            count++;
            checksum += envelope.Payload.Id;
        }

        return new EfQueryObservation(count, checksum);
    }
}
