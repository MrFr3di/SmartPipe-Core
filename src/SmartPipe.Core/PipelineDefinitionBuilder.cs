#nullable enable

namespace SmartPipe.Core;

/// <summary>Creates immutable typed pipeline definitions.</summary>
public static class PipelineDefinitionBuilder
{
    /// <summary>Begins a typed definition with an explicit key and source descriptor.</summary>
    public static PipelineDefinitionBuilder<TInput> From<TInput>(
        PipelineKey pipelineKey,
        PipelineComponent<IPipelineSource<TInput>> source)
    {
        PipelineKeyGuard.ThrowIfInvalid(pipelineKey);
        ArgumentNullException.ThrowIfNull(source);

        return new(
            new(
                pipelineKey,
                source,
                [],
                [],
                PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions()),
                LineageMode.Minimal));
    }

    internal static PipelineStageDescriptor<TInput, TOutput> CreateStage<TInput, TOutput>(
        PipelineStageKey stageKey,
        PipelineComponent<IPipelineTransformer<TInput, TOutput>> transformer,
        StageFailureOptions? failureOptions,
        StageDeadLetterOptions<TInput>? deadLetterOptions,
        string? stageName)
    {
        PipelineStageKeyGuard.ThrowIfInvalid(stageKey);
        ArgumentNullException.ThrowIfNull(transformer);
        if (stageName is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(stageName);

        return new(
            stageKey,
            transformer,
            StageFailureOptionsSnapshot.Create(failureOptions ?? StageFailureOptions.Default),
            deadLetterOptions,
            stageName ?? stageKey.Value);
    }

    internal static PipelineObserverRegistration CreateObserverRegistration(
        IPipelineObserver observer,
        ObserverReliability reliability,
        ObserverFailurePolicy failurePolicy)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (!Enum.IsDefined(reliability))
            throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "Observer reliability is invalid.");
        if (!Enum.IsDefined(failurePolicy))
            throw new ArgumentOutOfRangeException(nameof(failurePolicy), failurePolicy, "Observer failure policy is invalid.");

        return new(observer, reliability, failurePolicy);
    }

    internal static void ValidateLineageMode(LineageMode lineageMode)
    {
        if (!Enum.IsDefined(lineageMode))
            throw new ArgumentOutOfRangeException(nameof(lineageMode), lineageMode, "Lineage mode is invalid.");
    }
}

/// <summary>Builds a typed definition before its first transform.</summary>
public sealed class PipelineDefinitionBuilder<TInput>
{
    private readonly PipelineDefinitionState<TInput, TInput> _state;

    internal PipelineDefinitionBuilder(PipelineDefinitionState<TInput, TInput> state) =>
        _state = state;

    internal PipelineDefinitionBuilder<TInput, TInput> AsTyped() => new(_state);

    internal PipelineDefinitionBuilder<TInput> WithForcePipelineId(bool forcePipelineId) =>
        new(_state.WithForcePipelineId(forcePipelineId));

    /// <summary>Returns a branch using a defensive runtime-options snapshot.</summary>
    public PipelineDefinitionBuilder<TInput> WithRuntimeOptions(PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(_state.WithRuntimeOptions(PipelineRuntimeOptionsSnapshot.Create(options)));
    }

    /// <summary>Returns a branch using the specified lineage mode.</summary>
    public PipelineDefinitionBuilder<TInput> WithLineageMode(LineageMode lineageMode)
    {
        PipelineDefinitionBuilder.ValidateLineageMode(lineageMode);
        return new(_state.WithLineageMode(lineageMode));
    }

    /// <summary>Adds a borrowed observer instance and returns a single-use branch.</summary>
    public PipelineDefinitionBuilder<TInput> WithObserver(
        IPipelineObserver observer,
        ObserverReliability reliability = ObserverReliability.BestEffort,
        ObserverFailurePolicy failurePolicy = ObserverFailurePolicy.Log)
    {
        var registration = PipelineDefinitionBuilder.CreateObserverRegistration(
            observer,
            reliability,
            failurePolicy);
        return new(_state.WithObserver(registration));
    }

    /// <summary>Appends a typed transform without activating its component.</summary>
    public PipelineDefinitionBuilder<TInput, TNext> Transform<TNext>(
        PipelineStageKey stageKey,
        PipelineComponent<IPipelineTransformer<TInput, TNext>> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TInput>? deadLetterOptions = null,
        string? stageName = null)
    {
        var descriptor = PipelineDefinitionBuilder.CreateStage(
            stageKey,
            transformer,
            failureOptions,
            deadLetterOptions,
            stageName);
        return new(_state.Append(descriptor));
    }

    /// <summary>Finalizes a structurally valid sinkless definition.</summary>
    public PipelineDefinition<TInput, TInput> Build() =>
        PipelineDefinition<TInput, TInput>.Create(_state, sink: null);

    internal PipelineDefinition<TInput, TInput> Build(PipelineStartClaim startClaim) =>
        PipelineDefinition<TInput, TInput>.Create(_state, sink: null, startClaim);

    /// <summary>Finalizes a structurally valid definition with a sink.</summary>
    public PipelineDefinition<TInput, TInput> To(
        PipelineComponent<IPipelineSink<TInput>> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return PipelineDefinition<TInput, TInput>.Create(_state, sink);
    }

    internal PipelineDefinition<TInput, TInput> To(
        PipelineComponent<IPipelineSink<TInput>> sink,
        PipelineStartClaim startClaim)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return PipelineDefinition<TInput, TInput>.Create(_state, sink, startClaim);
    }

}

/// <summary>Builds a typed definition after one or more transforms.</summary>
public sealed class PipelineDefinitionBuilder<TInput, TOutput>
{
    private readonly PipelineDefinitionState<TInput, TOutput> _state;

    internal PipelineDefinitionBuilder(PipelineDefinitionState<TInput, TOutput> state) =>
        _state = state;

    /// <summary>Returns a branch using a defensive runtime-options snapshot.</summary>
    public PipelineDefinitionBuilder<TInput, TOutput> WithRuntimeOptions(
        PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(_state.WithRuntimeOptions(PipelineRuntimeOptionsSnapshot.Create(options)));
    }

    /// <summary>Returns a branch using the specified lineage mode.</summary>
    public PipelineDefinitionBuilder<TInput, TOutput> WithLineageMode(LineageMode lineageMode)
    {
        PipelineDefinitionBuilder.ValidateLineageMode(lineageMode);
        return new(_state.WithLineageMode(lineageMode));
    }

    internal PipelineDefinitionBuilder<TInput, TOutput> WithForcePipelineId(
        bool forcePipelineId) =>
        new(_state.WithForcePipelineId(forcePipelineId));

    /// <summary>Adds a borrowed observer instance and returns a single-use branch.</summary>
    public PipelineDefinitionBuilder<TInput, TOutput> WithObserver(
        IPipelineObserver observer,
        ObserverReliability reliability = ObserverReliability.BestEffort,
        ObserverFailurePolicy failurePolicy = ObserverFailurePolicy.Log)
    {
        var registration = PipelineDefinitionBuilder.CreateObserverRegistration(
            observer,
            reliability,
            failurePolicy);
        return new(_state.WithObserver(registration));
    }

    /// <summary>Appends a typed transform without activating its component.</summary>
    public PipelineDefinitionBuilder<TInput, TNext> Transform<TNext>(
        PipelineStageKey stageKey,
        PipelineComponent<IPipelineTransformer<TOutput, TNext>> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TOutput>? deadLetterOptions = null,
        string? stageName = null)
    {
        var descriptor = PipelineDefinitionBuilder.CreateStage(
            stageKey,
            transformer,
            failureOptions,
            deadLetterOptions,
            stageName);
        return new(_state.Append(descriptor));
    }

    /// <summary>Finalizes a structurally valid sinkless definition.</summary>
    public PipelineDefinition<TInput, TOutput> Build() =>
        PipelineDefinition<TInput, TOutput>.Create(_state, sink: null);

    internal PipelineDefinition<TInput, TOutput> Build(PipelineStartClaim startClaim) =>
        PipelineDefinition<TInput, TOutput>.Create(_state, sink: null, startClaim);

    /// <summary>Finalizes a structurally valid definition with a sink.</summary>
    public PipelineDefinition<TInput, TOutput> To(
        PipelineComponent<IPipelineSink<TOutput>> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return PipelineDefinition<TInput, TOutput>.Create(_state, sink);
    }

    internal PipelineDefinition<TInput, TOutput> To(
        PipelineComponent<IPipelineSink<TOutput>> sink,
        PipelineStartClaim startClaim)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return PipelineDefinition<TInput, TOutput>.Create(_state, sink, startClaim);
    }

}
