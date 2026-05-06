using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Filters items by predicate. Returns <see cref="ProcessingResult{T}.Failure"/> with Category="Filtered" for non-matching items.
/// Implements <see cref="ITransformer{T, T}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class FilterTransform<T> : ITransformer<T, T>
{
    private readonly Func<T, bool>? _predicate;
    private readonly Func<T, Task<bool>>? _asyncPredicate;

    /// <summary>
    /// Initializes a new instance of <see cref="FilterTransform{T}"/> with a synchronous predicate.
    /// </summary>
    /// <param name="predicate">The synchronous predicate to filter items.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="predicate"/> is null.</exception>
    public FilterTransform(Func<T, bool> predicate) => _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    /// <summary>
    /// Initializes a new instance of <see cref="FilterTransform{T}"/> with an asynchronous predicate.
    /// </summary>
    /// <param name="asyncPredicate">The asynchronous predicate to filter items.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="asyncPredicate"/> is null.</exception>
    public FilterTransform(Func<T, Task<bool>> asyncPredicate) => _asyncPredicate = asyncPredicate ?? throw new ArgumentNullException(nameof(asyncPredicate));

    /// <summary>
    /// Combines two filters with logical AND operator.
    /// </summary>
    public static FilterTransform<T> operator &(FilterTransform<T> a, FilterTransform<T> b) => new(x => a._predicate!(x) && b._predicate!(x));

    /// <summary>
    /// Combines two filters with logical OR operator.
    /// </summary>
    public static FilterTransform<T> operator |(FilterTransform<T> a, FilterTransform<T> b) => new(x => a._predicate!(x) || b._predicate!(x));

    /// <summary>
    /// Negates the filter condition.
    /// </summary>
    public static FilterTransform<T> operator !(FilterTransform<T> a) => new(x => !a._predicate!(x));

    /// <summary>
    /// Combines this filter with another using logical AND.
    /// </summary>
    /// <param name="other">The other filter to combine with.</param>
    /// <returns>A new filter that requires both conditions to be true.</returns>
    public FilterTransform<T> And(FilterTransform<T> other) => this & other;

    /// <summary>
    /// Combines this filter with another using logical OR.
    /// </summary>
    /// <param name="other">The other filter to combine with.</param>
    /// <returns>A new filter where either condition can be true.</returns>
    public FilterTransform<T> Or(FilterTransform<T> other) => this | other;

    /// <summary>
    /// Negates this filter condition.
    /// </summary>
    /// <returns>A new filter with inverted condition.</returns>
    public FilterTransform<T> Not() => !this;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        bool isMatch = _asyncPredicate != null
            ? await _asyncPredicate(ctx.Payload).ConfigureAwait(false)
            : _predicate!(ctx.Payload);

        if (isMatch)
            return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);

        return ProcessingResult<T>.Failure(
            new SmartPipeError("Filtered out", ErrorType.Permanent, "Filtered"), ctx.TraceId);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}
