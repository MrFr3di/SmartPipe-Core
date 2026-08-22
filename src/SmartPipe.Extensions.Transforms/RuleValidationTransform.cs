using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Validates items with reflection-free application rules.</summary>
public class RuleValidationTransform<T> : IPipelineTransformer<T, T>
{
    private readonly object _sync = new();
    private readonly List<(Func<T, bool> Condition, string Message)> _rules = [];
    private (Func<T, bool> Condition, string Message)[]? _frozenRules;

    /// <summary>Adds a rule that must return true for validation to succeed.</summary>
    public RuleValidationTransform<T> Require(Func<T, bool> condition, string message)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_sync)
        {
            if (_frozenRules is not null)
                throw new InvalidOperationException("Validation rules are frozen.");

            _rules.Add((condition, message));
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
    public ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        (Func<T, bool> Condition, string Message)[] rules = Freeze();
        List<string>? failures = null;
        foreach ((Func<T, bool> condition, string message) in rules)
        {
            if (!condition(envelope.Payload))
                (failures ??= []).Add(message);
            ct.ThrowIfCancellationRequested();
        }

        return ValueTask.FromResult(failures is null
            ? StageResult<T>.Success(envelope.Payload)
            : StageResult<T>.Failure(new SmartPipeError(
                string.Join("; ", failures), ErrorType.Permanent, "Validation")));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (Func<T, bool> Condition, string Message)[] Freeze()
    {
        lock (_sync)
            return _frozenRules ??= [.. _rules];
    }
}
