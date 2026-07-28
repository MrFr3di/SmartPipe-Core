#nullable enable

namespace SmartPipe.Core;

internal sealed class ActivatedStage
{
    public required ITypedPipelineStage RuntimeStage { get; init; }

    public required ActivatedComponentLease Lease { get; init; }
}

internal sealed class ActivatedPipelineGraph<TInput, TOutput>
{
    public required IPipelineSource<TInput> Source { get; init; }

    public required IReadOnlyList<ITypedPipelineStage> Stages { get; init; }

    public IPipelineSink<TOutput>? Sink { get; init; }

    public required IReadOnlyList<PipelineObserverRegistration> Observers { get; init; }

    public required PipelineActivationLedger Lifetime { get; init; }
}
