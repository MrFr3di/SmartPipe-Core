using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>
/// Entity Framework Core data source that streams entities via IAsyncEnumerable.
/// Uses no-tracking queries by default for read-only pipeline source scenarios.
/// Supports query customization, cancellation, and logging.
/// </summary>
/// <typeparam name="T">Entity type from DbContext.</typeparam>
public class EfCoreSelector<T> : IPipelineSource<T>
    where T : class
{
    private readonly DbContext _dbContext;
    private readonly ILogger<EfCoreSelector<T>>? _logger;
    private IQueryable<T>? _query;
    private bool _trackingEnabled;

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

    /// <summary>
    /// Configures whether entities returned by this selector are tracked by EF Core.
    /// Tracking is disabled by default.
    /// </summary>
    public EfCoreSelector<T> WithTracking(bool enabled = true)
    {
        _trackingEnabled = enabled;
        return this;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var query = _query ?? _dbContext.Set<T>();
        query = _trackingEnabled ? query.AsTracking() : query.AsNoTracking();
        var entities = query.AsAsyncEnumerable().WithCancellation(ct);

        await foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<T>.Create(entity);
        }

        _logger?.LogInformation("EFCore source completed for {EntityType}", typeof(T).Name);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
