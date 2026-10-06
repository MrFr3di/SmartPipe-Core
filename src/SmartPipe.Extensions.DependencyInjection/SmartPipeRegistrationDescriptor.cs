using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Provides immutable metadata for one registered pipeline.</summary>
public sealed record SmartPipeRegistrationDescriptor
{
    /// <summary>Gets the globally unique pipeline key.</summary>
    public required PipelineKey Key { get; init; }

    /// <summary>Gets the pipeline input type.</summary>
    public required Type InputType { get; init; }

    /// <summary>Gets the pipeline output type.</summary>
    public required Type OutputType { get; init; }

    /// <summary>Gets the closed public pipeline definition type.</summary>
    public required Type DefinitionType { get; init; }

    /// <summary>Gets the closed public run factory service type.</summary>
    public required Type FactoryType { get; init; }

    /// <summary>Gets the display name, equal to the pipeline key value.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the zero-based successful registration order.</summary>
    public required int RegistrationOrder { get; init; }

    /// <summary>Gets a value indicating whether the definition supports multiple runs.</summary>
    public required bool IsReusable { get; init; }
}
