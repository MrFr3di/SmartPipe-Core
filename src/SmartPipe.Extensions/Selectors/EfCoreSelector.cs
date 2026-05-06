using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>
/// Entity Framework Core data source that streams entities via IAsyncEnumerable.
/// Supports cancellation and logging.
/// </summary>
/// <typeparam name="T">Entity type from DbContext.</typeparam>
public class EfCoreSelector<T> : ISource<T> where T : class
{
    private readonly DbContext _dbContext;
    private readonly ILogger<EfCoreSelector<T>>? _logger;
    private IQueryable<T>? _query;

    /// <summary>Create EF Core source for given DbContext.</summary>
    /// <param name="dbContext">EF Core database context.</param>
    /// <param name="logger">Optional logger.</param>
    public EfCoreSelector(DbContext dbContext, ILogger<EfCoreSelector<T>>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger;
    }

    /// <summary>Configure query before reading (filtering, ordering, etc.).</summary>
    public EfCoreSelector<T> WithQuery(Func<DbSet<T>, IQueryable<T>> configure)
    {
        _query = configure(_dbContext.Set<T>());
        return this;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var query = _query ?? _dbContext.Set<T>();
        var entities = query.AsAsyncEnumerable().WithCancellation(ct);

        await foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ProcessingContext<T>(entity);
        }

        _logger?.LogInformation("EFCore source completed for {EntityType}", typeof(T).Name);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
