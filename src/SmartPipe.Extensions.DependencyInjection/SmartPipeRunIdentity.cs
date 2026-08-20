using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Identifies one pipeline run.</summary>
public sealed record SmartPipeRunIdentity
{
    /// <summary>Gets the pipeline key.</summary>
    public required PipelineKey PipelineKey { get; init; }

    /// <summary>Gets the run identifier.</summary>
    public required Guid RunId { get; init; }
}
