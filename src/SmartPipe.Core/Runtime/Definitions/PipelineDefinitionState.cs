#nullable enable

namespace SmartPipe.Core;

internal sealed class PipelineDefinitionState<TInput, TOutput>
{
    public PipelineDefinitionState(
        PipelineKey key,
        PipelineComponent<IPipelineSource<TInput>> source,
        IPipelineStageDescriptor[] stages,
        PipelineObserverRegistration[] observers,
        PipelineRuntimeOptionsSnapshot runtimeOptions,
        LineageMode lineageMode,
        bool forcePipelineId = true)
    {
        Key = key;
        Source = source;
        Stages = Copy(stages);
        Observers = Copy(observers);
        RuntimeOptions = runtimeOptions;
        LineageMode = lineageMode;
        ForcePipelineId = forcePipelineId;
    }

    public PipelineKey Key { get; }

    public PipelineComponent<IPipelineSource<TInput>> Source { get; }

    public IPipelineStageDescriptor[] Stages { get; }

    public PipelineObserverRegistration[] Observers { get; }

    public PipelineRuntimeOptionsSnapshot RuntimeOptions { get; }

    public LineageMode LineageMode { get; }

    public bool ForcePipelineId { get; }

    public PipelineDefinitionState<TInput, TOutput> WithRuntimeOptions(
        PipelineRuntimeOptionsSnapshot runtimeOptions) =>
        new(Key, Source, Stages, Observers, runtimeOptions, LineageMode, ForcePipelineId);

    public PipelineDefinitionState<TInput, TOutput> WithLineageMode(LineageMode lineageMode) =>
        new(Key, Source, Stages, Observers, RuntimeOptions, lineageMode, ForcePipelineId);

    public PipelineDefinitionState<TInput, TOutput> WithForcePipelineId(bool forcePipelineId) =>
        new(Key, Source, Stages, Observers, RuntimeOptions, LineageMode, forcePipelineId);

    public PipelineDefinitionState<TInput, TOutput> WithObserver(
        PipelineObserverRegistration observer)
    {
        var observers = new PipelineObserverRegistration[Observers.Length + 1];
        Array.Copy(Observers, observers, Observers.Length);
        observers[^1] = observer;
        return new(Key, Source, Stages, observers, RuntimeOptions, LineageMode, ForcePipelineId);
    }

    public PipelineDefinitionState<TInput, TNext> Append<TNext>(
        PipelineStageDescriptor<TOutput, TNext> stage)
    {
        var stages = new IPipelineStageDescriptor[Stages.Length + 1];
        Array.Copy(Stages, stages, Stages.Length);
        stages[^1] = stage;
        return new(Key, Source, stages, Observers, RuntimeOptions, LineageMode, ForcePipelineId);
    }

    private static T[] Copy<T>(T[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new T[values.Length];
        Array.Copy(values, copy, values.Length);
        return copy;
    }
}
