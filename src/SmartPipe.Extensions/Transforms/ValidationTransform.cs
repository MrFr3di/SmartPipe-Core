using System.ComponentModel.DataAnnotations;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Validates items using DataAnnotations attributes and custom validation rules.
/// Returns <see cref="StageResult{T}.Failure"/> with validation errors for invalid items.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type to validate.</typeparam>
public class ValidationTransform<T> : IPipelineTransformer<T, T>
{
    private readonly List<Func<T, string?>> _rules = [];

    /// <summary>
    /// Adds a custom validation rule to the transform.
    /// </summary>
    /// <param name="condition">The condition that must be true for validation to pass.</param>
    /// <param name="message">The error message if the condition fails.</param>
    /// <returns>This transform instance for fluent chaining.</returns>
    public ValidationTransform<T> Require(Func<T, bool> condition, string message)
    {
        _rules.Add(x => condition(x) ? null : message);
        return this;
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default
    )
    {
        var errors = new List<string>();

        // DataAnnotations
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(envelope.Payload!);
        if (!Validator.TryValidateObject(envelope.Payload!, validationContext, validationResults, true))
            errors.AddRange(validationResults.Select(r => r.ErrorMessage ?? "Validation failed"));

        // Custom rules
        foreach (var rule in _rules)
        {
            var error = rule(envelope.Payload);
            if (error != null)
                errors.Add(error);
        }

        if (errors.Count == 0)
            return ValueTask.FromResult(StageResult<T>.Success(envelope.Payload));

        return ValueTask.FromResult(
            StageResult<T>.Failure(
                new SmartPipeError(string.Join("; ", errors), ErrorType.Permanent, "Validation")
            )
        );
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
