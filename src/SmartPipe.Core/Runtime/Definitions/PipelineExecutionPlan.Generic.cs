#nullable enable

namespace SmartPipe.Core;

internal sealed class PipelineExecutionPlan<TInput, TOutput>
{
    public required PipelineKey Key { get; init; }

    public required PipelineComponent<IPipelineSource<TInput>> Source { get; init; }

    public required IReadOnlyList<IPipelineStageDescriptor> Stages { get; init; }

    public PipelineComponent<IPipelineSink<TOutput>>? Sink { get; init; }

    public required IReadOnlyList<PipelineObserverRegistration> Observers { get; init; }

    public required PipelineRuntimeOptionsSnapshot RuntimeOptions { get; init; }

    public required LineageMode LineageMode { get; init; }

    public required bool ForcePipelineId { get; init; }

    public required bool IsReusable { get; init; }

    public required bool RequiresServices { get; init; }
}
