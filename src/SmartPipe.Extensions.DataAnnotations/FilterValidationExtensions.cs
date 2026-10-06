using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Extension methods for converting validation transforms to filters.</summary>
public static class FilterValidationExtensions
{
    private const string ReflectionContract =
        "Reflection-based DataAnnotations validation is not trimming-safe.";

    /// <summary>
    /// Converts a validation transform into a filter. Invalid items are filtered out.
    /// </summary>
    /// <typeparam name="T">The data type.</typeparam>
    /// <param name="validator">The validation transform to convert.</param>
    /// <returns>A token-aware filter backed by the validation transform.</returns>
    [RequiresUnreferencedCode(ReflectionContract)]
    public static FilterTransform<T> ToFilter<T>(this ValidationTransform<T> validator) =>
        new FilterTransform<T>(async (item, ct) =>
        {
            var result = await validator.TransformAsync(
                ProcessingEnvelope<T>.Create(item), ct).ConfigureAwait(false);
            return result.IsSuccess;
        });
}
