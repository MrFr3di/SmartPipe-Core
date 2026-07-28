#nullable enable

namespace SmartPipe.Core;

internal delegate ValueTask<TComponent> PipelineComponentActivator<TComponent>(
    PipelineActivationContext context,
    CancellationToken cancellationToken)
    where TComponent : class;

/// <summary>Describes how a pipeline component is activated and owned.</summary>
/// <typeparam name="TComponent">The component reference type.</typeparam>
public sealed class PipelineComponent<TComponent>
    where TComponent : class
{
    internal PipelineComponent(
        PipelineComponentOwnership ownership,
        bool initialize,
        bool isPerRun,
        PipelineComponentActivator<TComponent> activator)
    {
        Ownership = ownership;
        Initialize = initialize;
        IsPerRun = isPerRun;
        Activator = activator;
    }

    /// <summary>Gets who owns the activated component.</summary>
    public PipelineComponentOwnership Ownership { get; }

    /// <summary>Gets whether Core initializes the component after activation.</summary>
    public bool Initialize { get; }

    internal bool IsPerRun { get; }

    internal PipelineComponentActivator<TComponent> Activator { get; }
}

/// <summary>Creates component descriptors with explicit ownership semantics.</summary>
public static class PipelineComponent
{
    /// <summary>Creates a per-run component owned by the Core runtime.</summary>
    /// <typeparam name="TComponent">The component reference type.</typeparam>
    /// <param name="factory">The lazy per-run component factory.</param>
    /// <returns>A runtime-owned component descriptor.</returns>
    public static PipelineComponent<TComponent> RuntimeOwned<TComponent>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TComponent>> factory)
        where TComponent : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(
            PipelineComponentOwnership.RuntimeOwned,
            initialize: true,
            isPerRun: true,
            factory.Invoke);
    }

    /// <summary>Creates a per-run component owned by an external scope.</summary>
    /// <typeparam name="TComponent">The component reference type.</typeparam>
    /// <param name="factory">The lazy per-run component factory.</param>
    /// <returns>A scope-owned component descriptor.</returns>
    public static PipelineComponent<TComponent> ScopeOwned<TComponent>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TComponent>> factory)
        where TComponent : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(
            PipelineComponentOwnership.ScopeOwned,
            initialize: true,
            isPerRun: true,
            factory.Invoke);
    }

    /// <summary>Creates a descriptor for an externally owned component instance.</summary>
    /// <typeparam name="TComponent">The component reference type.</typeparam>
    /// <param name="instance">The exact externally owned instance.</param>
    /// <param name="initialize">Whether Core initializes the borrowed instance.</param>
    /// <returns>An externally owned single-instance component descriptor.</returns>
    public static PipelineComponent<TComponent> Borrowed<TComponent>(
        TComponent instance,
        bool initialize = false)
        where TComponent : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new(
            PipelineComponentOwnership.ExternallyOwned,
            initialize,
            isPerRun: false,
            (_, _) => ValueTask.FromResult(instance));
    }
}
