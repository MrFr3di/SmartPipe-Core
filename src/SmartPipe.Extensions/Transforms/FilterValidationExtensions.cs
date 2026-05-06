using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Extension methods for converting <see cref="ValidationTransform{T}"/> to <see cref="FilterTransform{T}"/>.
/// Enables using validation logic as a filtering mechanism in pipelines.
/// </summary>
public static class FilterValidationExtensions
{
    /// <summary>
    /// Converts a <see cref="ValidationTransform{T}"/> into a <see cref="FilterTransform{T}"/>.
    /// Items that pass validation will pass the filter; invalid items will be filtered out.
    /// </summary>
    /// <typeparam name="T">The data type.</typeparam>
    /// <param name="validator">The validation transform to convert.</param>
    /// <returns>A filter transform that uses validation results to filter items.</returns>
    public static FilterTransform<T> ToFilter<T>(this ValidationTransform<T> validator) =>
        new FilterTransform<T>(async item =>
        {
            var ctx = new ProcessingContext<T>(item);
            var result = await validator.TransformAsync(ctx).ConfigureAwait(false);
            return result.IsSuccess;
        });
}
