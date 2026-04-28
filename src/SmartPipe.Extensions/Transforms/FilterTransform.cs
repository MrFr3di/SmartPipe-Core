using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Filters items by predicate. Returns Failure with Category="Filtered" for non-matching items.</summary>
public class FilterTransform<T> : ITransformer<T, T>
{
    private readonly Func<T, bool> _predicate;

    public FilterTransform(Func<T, bool> predicate) => _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    public static FilterTransform<T> operator &(FilterTransform<T> a, FilterTransform<T> b) => new(x => a._predicate(x) && b._predicate(x));
    public static FilterTransform<T> operator |(FilterTransform<T> a, FilterTransform<T> b) => new(x => a._predicate(x) || b._predicate(x));
    public static FilterTransform<T> operator !(FilterTransform<T> a) => new(x => !a._predicate(x));

    public FilterTransform<T> And(FilterTransform<T> other) => this & other;
    public FilterTransform<T> Or(FilterTransform<T> other) => this | other;
    public FilterTransform<T> Not() => !this;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        if (_predicate(ctx.Payload))
            return ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));

        return ValueTask.FromResult(ProcessingResult<T>.Failure(
            new SmartPipeError("Filtered out", ErrorType.Permanent, "Filtered"), ctx.TraceId));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
