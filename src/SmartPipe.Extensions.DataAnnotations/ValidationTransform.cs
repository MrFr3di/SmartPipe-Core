using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Validates items with DataAnnotations attributes and custom validation rules.
/// </summary>
/// <typeparam name="T">The data type to validate.</typeparam>
public class ValidationTransform<T> : IPipelineTransformer<T, T>
{
    private const string ReflectionContract =
        "Reflection-based DataAnnotations validation is not trimming-safe.";

    private readonly object _sync = new();
    private readonly List<Func<T, string?>> _rules = [];
    private Func<T, string?>[]? _frozenRules;

    /// <summary>Adds a custom validation rule to the transform.</summary>
    /// <param name="condition">The condition that must be true for validation to pass.</param>
    /// <param name="message">The error message if the condition fails.</param>
    /// <returns>This transform instance for fluent chaining.</returns>
    public ValidationTransform<T> Require(Func<T, bool> condition, string message)
    {
        lock (_sync)
        {
            if (_frozenRules is not null)
                throw new InvalidOperationException("Validation rules are frozen.");

            _rules.Add(x => condition(x) ? null : message);
        }

        return this;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Freeze();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
#pragma warning disable IL2046 // IPipelineTransformer predates the RUC contract; direct calls remain annotated.
    [RequiresUnreferencedCode(ReflectionContract)]
    public ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Func<T, string?>[] rules = Freeze();
        var errors = new List<string>();
        T payload = envelope.Payload!;

        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(payload!);
        if (!Validator.TryValidateObject(payload!, validationContext, validationResults, true))
            errors.AddRange(validationResults.Select(r => r.ErrorMessage ?? "Validation failed"));

        ct.ThrowIfCancellationRequested();
        foreach (Func<T, string?> rule in rules)
        {
            var error = rule(payload);
            if (error is not null)
                errors.Add(error);
            ct.ThrowIfCancellationRequested();
        }

        return errors.Count == 0
            ? ValueTask.FromResult(StageResult<T>.Success(payload))
            : ValueTask.FromResult(
                StageResult<T>.Failure(
                    new SmartPipeError(string.Join("; ", errors), ErrorType.Permanent, "Validation")));
    }
#pragma warning restore IL2046

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Func<T, string?>[] Freeze()
    {
        lock (_sync)
            return _frozenRules ??= [.. _rules];
    }
}
