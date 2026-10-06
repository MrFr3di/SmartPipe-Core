using System.Reflection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Builds the per-run component instance behind a <see cref="PipelineComponent{TComponent}"/> descriptor.</summary>
/// <remarks>
/// <para>
/// Core activates a runtime-owned component by invoking the descriptor's activator with the activation context and then
/// initializing the result. The activator property is <c>internal</c> in <c>SmartPipe.Core</c>, which does not grant
/// <c>InternalsVisibleTo</c> to this assembly, so the descriptor contract is exercised through the same internal
/// property that <c>SmartPipe.Extensions.Csv.Tests</c> already uses.
/// </para>
/// <para>
/// Activation itself performs no I/O: every PostgreSQL factory returns <c>ValueTask.FromResult(new …)</c>, and only
/// <c>InitializeAsync</c> acquires a connection.
/// </para>
/// </remarks>
internal static class PipelineComponentProbe
{
    private const string ActivatorPropertyName = "Activator";

    /// <summary>Creates the per-run activation context for a probe activation.</summary>
    internal static PipelineActivationContext CreateContext(string pipelineId) =>
        new(new PipelineKey(pipelineId), Guid.NewGuid());

    /// <summary>Creates a distinct per-run component instance from a descriptor.</summary>
    /// <typeparam name="TComponent">The component contract the descriptor produces.</typeparam>
    /// <param name="descriptor">The descriptor to activate.</param>
    /// <param name="cancellationToken">The activation cancellation token.</param>
    /// <returns>A new, uninitialized component instance.</returns>
    internal static TComponent Activate<TComponent>(
        PipelineComponent<TComponent> descriptor,
        CancellationToken cancellationToken = default)
        where TComponent : class
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var property = typeof(PipelineComponent<TComponent>).GetProperty(
            ActivatorPropertyName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.GetValue(descriptor) is not Delegate activator)
        {
            throw new InvalidOperationException(
                $"PipelineComponent<{typeof(TComponent).Name}> does not expose an internal delegate "
                + $"'{ActivatorPropertyName}'; the descriptor probe must be updated.");
        }

        object? activated;
        try
        {
            activated = activator.DynamicInvoke(CreateContext("postgresql-unit"), cancellationToken);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"Activating PipelineComponent<{typeof(TComponent).Name}> failed: {exception.InnerException?.Message}",
                exception.InnerException);
        }

        if (activated is not ValueTask<TComponent> valueTask)
        {
            throw new InvalidOperationException(
                $"PipelineComponent<{typeof(TComponent).Name}>.{ActivatorPropertyName} did not return "
                + $"ValueTask<{typeof(TComponent).Name}>.");
        }

        if (!valueTask.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                $"PipelineComponent<{typeof(TComponent).Name}>.{ActivatorPropertyName} completed asynchronously; "
                + "the PostgreSQL factories are lazy constructors and must complete synchronously.");
        }

        return valueTask.GetAwaiter().GetResult();
    }
}
