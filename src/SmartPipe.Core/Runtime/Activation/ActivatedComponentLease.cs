#nullable enable

namespace SmartPipe.Core;

internal sealed class ActivatedComponentLease
{
    public required string Role { get; init; }

    public required PipelineComponentOwnership Ownership { get; init; }

    public PipelineStageKey? StageKey { get; init; }

    public Func<ValueTask>? RuntimeOwnedCleanup { get; init; }
}
