using System.ComponentModel.DataAnnotations;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Validates items using DataAnnotations. Returns Failure with validation errors for invalid items.</summary>
public class ValidationTransform<T> : ITransformer<T, T>
{
    private readonly List<Func<T, string?>> _rules = new();

    public ValidationTransform<T> Require(Func<T, bool> condition, string message)
    {
        _rules.Add(x => condition(x) ? null : message);
        return this;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        var errors = new List<string>();

        // DataAnnotations
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(ctx.Payload!);
        if (!Validator.TryValidateObject(ctx.Payload!, validationContext, validationResults, true))
            errors.AddRange(validationResults.Select(r => r.ErrorMessage ?? "Validation failed"));

        // Custom rules
        foreach (var rule in _rules)
        {
            var error = rule(ctx.Payload);
            if (error != null) errors.Add(error);
        }

        if (errors.Count == 0)
            return ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));

        return ValueTask.FromResult(ProcessingResult<T>.Failure(
            new SmartPipeError(string.Join("; ", errors), ErrorType.Permanent, "Validation"), ctx.TraceId));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
