#nullable enable

namespace SmartPipe.Core;

/// <summary>Describes whether a pipeline component can safely participate in multiple runs.</summary>
public enum PipelineComponentLifetime
{
    /// <summary>The component is intended for one runtime execution.</summary>
    SingleUse,

    /// <summary>The component can safely be reused across runtime executions.</summary>
    Reusable,

    /// <summary>The component is externally owned, commonly by dependency injection.</summary>
    SingletonExternal
}

/// <summary>Provides lifecycle metadata for pipeline components.</summary>
/// <remarks>
/// Components that do not implement this descriptor are treated conservatively by the runtime.
/// Sources and sinks default to <see cref="PipelineComponentLifetime.SingleUse"/>. Externally owned
/// singleton components are not disposed unless <see cref="ComponentOwnershipOptions.DisposeExternalComponents"/>
/// is enabled.
/// </remarks>
public interface IPipelineComponentDescriptor
{
    /// <summary>Gets the component lifetime.</summary>
    PipelineComponentLifetime Lifetime { get; }

    /// <summary>Gets a value indicating whether the runtime owns this component's resources.</summary>
    bool OwnsResources { get; }
}

/// <summary>Controls disposal of externally owned components.</summary>
public sealed class ComponentOwnershipOptions
{
    /// <summary>Gets default ownership options.</summary>
    public static ComponentOwnershipOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether external singleton components should be disposed by the runtime.
    /// </summary>
    /// <remarks>The default is false to avoid closing dependencies owned by a DI container.</remarks>
    public bool DisposeExternalComponents { get; init; }
}
