using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Extension methods for converting validation transforms to filters.</summary>
public static class FilterValidationExtensions
{
    public static FilterTransform<T> ToFilter<T>(this ValidationTransform<T> validator) =>
        new(item =>
        {
            var ctx = new ProcessingContext<T>(item);
            var result = validator.TransformAsync(ctx).Result;
            return result.IsSuccess;
        });
}
