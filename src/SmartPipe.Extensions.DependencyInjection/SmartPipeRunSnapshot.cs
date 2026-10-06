using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Provides immutable point-in-time metadata for an active run.</summary>
public sealed record SmartPipeRunSnapshot
{
    /// <summary>Gets the composite run identity.</summary>
    public required SmartPipeRunIdentity Identity { get; init; }

    /// <summary>Gets the pipeline input type.</summary>
    public required Type InputType { get; init; }

    /// <summary>Gets the pipeline output type.</summary>
    public required Type OutputType { get; init; }

    /// <summary>Gets the UTC start timestamp.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Gets the current run state.</summary>
    public required PipelineRunState State { get; init; }

    /// <summary>Gets an immutable metrics snapshot.</summary>
    public required SmartPipeMetricsSnapshot Metrics { get; init; }

    /// <summary>Gets the effective input channel capacity.</summary>
    public required int InputCapacity { get; init; }

    /// <summary>Gets the effective output channel capacity.</summary>
    public required int OutputCapacity { get; init; }
}
