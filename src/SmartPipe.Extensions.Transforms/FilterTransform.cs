using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Filters items with synchronous or asynchronous predicates.</summary>
public class FilterTransform<T> : IPipelineTransformer<T, T>
{
    private readonly Func<T, CancellationToken, ValueTask<bool>> _predicate;

    /// <summary>Initializes a filter from a synchronous predicate.</summary>
    public FilterTransform(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicate = (item, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            bool result = predicate(item);
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        };
    }

    /// <summary>Initializes a filter from a legacy asynchronous predicate without token support.</summary>
    public FilterTransform(Func<T, Task<bool>> asyncPredicate)
    {
        ArgumentNullException.ThrowIfNull(asyncPredicate);
        _predicate = async (item, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            bool result = await asyncPredicate(item).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        };
    }

    /// <summary>Initializes a filter from the canonical token-aware predicate.</summary>
    public FilterTransform(Func<T, CancellationToken, ValueTask<bool>> predicate) =>
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    /// <summary>Combines two filters with logical AND.</summary>
    public static FilterTransform<T> operator &(FilterTransform<T> a, FilterTransform<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new FilterTransform<T>(async (item, ct) =>
            await a.EvaluateAsync(item, ct).ConfigureAwait(false)
            && await b.EvaluateAsync(item, ct).ConfigureAwait(false));
    }

    /// <summary>Combines two filters with logical OR.</summary>
    public static FilterTransform<T> operator |(FilterTransform<T> a, FilterTransform<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new FilterTransform<T>(async (item, ct) =>
            await a.EvaluateAsync(item, ct).ConfigureAwait(false)
            || await b.EvaluateAsync(item, ct).ConfigureAwait(false));
    }

    /// <summary>Negates a filter.</summary>
    public static FilterTransform<T> operator !(FilterTransform<T> a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new FilterTransform<T>(async (item, ct) =>
            !await a.EvaluateAsync(item, ct).ConfigureAwait(false));
    }

    /// <summary>Combines this filter with another using logical AND.</summary>
    public FilterTransform<T> And(FilterTransform<T> other) => this & other;

    /// <summary>Combines this filter with another using logical OR.</summary>
    public FilterTransform<T> Or(FilterTransform<T> other) => this | other;

    /// <summary>Negates this filter.</summary>
    public FilterTransform<T> Not() => !this;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default) =>
        await EvaluateAsync(envelope.Payload, ct).ConfigureAwait(false)
            ? StageResult<T>.Success(envelope.Payload)
            : StageResult<T>.Filtered();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ValueTask<bool> EvaluateAsync(T item, CancellationToken ct) => _predicate(item, ct);
}
