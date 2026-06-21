using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Filters items by predicate. Returns <see cref="StageResult{T}.Filtered"/> for non-matching items.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class FilterTransform<T> : IPipelineTransformer<T, T>
{
    private readonly Func<T, bool>? _predicate;
    private readonly Func<T, Task<bool>>? _asyncPredicate;

    /// <summary>
    /// Initializes a new instance of <see cref="FilterTransform{T}"/> with a synchronous predicate.
    /// </summary>
    /// <param name="predicate">The synchronous predicate to filter items.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="predicate"/> is null.</exception>
    public FilterTransform(Func<T, bool> predicate) =>
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    /// <summary>
    /// Initializes a new instance of <see cref="FilterTransform{T}"/> with an asynchronous predicate.
    /// </summary>
    /// <param name="asyncPredicate">The asynchronous predicate to filter items.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="asyncPredicate"/> is null.</exception>
    public FilterTransform(Func<T, Task<bool>> asyncPredicate) =>
        _asyncPredicate = asyncPredicate ?? throw new ArgumentNullException(nameof(asyncPredicate));

    /// <summary>
    /// Combines two filters with logical AND operator.
    /// </summary>
    public static FilterTransform<T> operator &(FilterTransform<T> a, FilterTransform<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return new(async x =>
        {
            if (!await a.EvaluateAsync(x).ConfigureAwait(false))
                return false;

            return await b.EvaluateAsync(x).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Combines two filters with logical OR operator.
    /// </summary>
    public static FilterTransform<T> operator |(FilterTransform<T> a, FilterTransform<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return new(async x =>
        {
            if (await a.EvaluateAsync(x).ConfigureAwait(false))
                return true;

            return await b.EvaluateAsync(x).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Negates the filter condition.
    /// </summary>
    public static FilterTransform<T> operator !(FilterTransform<T> a)
    {
        ArgumentNullException.ThrowIfNull(a);

        return new(async x => !await a.EvaluateAsync(x).ConfigureAwait(false));
    }

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
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default
    )
    {
        bool isMatch = await EvaluateAsync(envelope.Payload).ConfigureAwait(false);

        if (isMatch)
            return StageResult<T>.Success(envelope.Payload);

        return StageResult<T>.Filtered();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<bool> EvaluateAsync(T item) =>
        _asyncPredicate != null
            ? await _asyncPredicate(item).ConfigureAwait(false)
            : _predicate!(item);
}
