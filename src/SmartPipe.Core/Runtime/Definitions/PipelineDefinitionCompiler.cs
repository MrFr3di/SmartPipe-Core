#nullable enable

using System.Collections.ObjectModel;

namespace SmartPipe.Core;

internal static class PipelineDefinitionCompiler
{
    public static PipelineExecutionPlan<TInput, TOutput> Compile<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state,
        PipelineComponent<IPipelineSink<TOutput>>? sink)
    {
        Validate(state, sink);

        return new()
        {
            Key = state.Key,
            Source = state.Source,
            Stages = AsReadOnlyCopy(state.Stages),
            Sink = sink,
            Observers = AsReadOnlyCopy(state.Observers),
            RuntimeOptions = state.RuntimeOptions,
            LineageMode = state.LineageMode,
            ForcePipelineId = state.ForcePipelineId,
            IsReusable = IsReusable(state, sink),
            RequiresServices = RequiresServices(state, sink),
        };
    }

    public static void Validate<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state,
        PipelineComponent<IPipelineSink<TOutput>>? sink)
    {
        ArgumentNullException.ThrowIfNull(state);

        ValidateDefinitionState(state);
        var topology = BuildValidatedTopology(state);
        ValidateTopologyTypes<TInput, TOutput>(topology, nameof(state));
        ValidateObservers(state);

        _ = sink;
    }

    public static bool IsReusable<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state,
        PipelineComponent<IPipelineSink<TOutput>>? sink) =>
        state.Source.IsPerRun
        && state.Stages.All(stage => stage.IsPerRun && !stage.HasDeadLetterOptions)
        && (sink?.IsPerRun ?? true)
        && state.Observers.Length == 0;

    private static void ValidateDefinitionState<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state)
    {
        PipelineKeyGuard.ThrowIfInvalid(state.Key, nameof(state));
        if (state.Source is null)
            throw new ArgumentException("Definition source must not be null.", nameof(state));
        if (state.RuntimeOptions is null)
            throw new ArgumentException("Definition runtime options must not be null.", nameof(state));

        state.RuntimeOptions.Validate();

        if (!Enum.IsDefined(state.LineageMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state.LineageMode,
                "Lineage mode is invalid.");
        }
    }

    private static PipelineStageTopologyEntry[] BuildValidatedTopology<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state)
    {
        var topology = new PipelineStageTopologyEntry[state.Stages.Length];
        for (var index = 0; index < state.Stages.Length; index++)
        {
            var stage = state.Stages[index]
                ?? throw new ArgumentException(
                    $"Stage descriptor at index {index} is null.",
                    nameof(state));

            PipelineStageKeyGuard.ThrowIfInvalid(stage.Key, nameof(state));
            ValidateStageDescriptor(stage, index, nameof(state));
            stage.FailureOptions.Validate();

            topology[index] = new(
                stage.Key.Value,
                stage.Name,
                stage.InputType,
                stage.OutputType);
        }

        PipelineStageTopologyValidator.Validate(topology);
        return topology;
    }

    private static void ValidateStageDescriptor(
        IPipelineStageDescriptor stage,
        int index,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(stage.Name))
            throw new ArgumentException(
                $"Stage name at index {index} must not be empty or whitespace.",
                parameterName);
        if (stage.InputType is null)
            throw new ArgumentException(
                $"Stage input type at index {index} must not be null.",
                parameterName);
        if (stage.OutputType is null)
            throw new ArgumentException(
                $"Stage output type at index {index} must not be null.",
                parameterName);
        if (stage.FailureOptions is null)
            throw new ArgumentException(
                $"Stage failure options at index {index} must not be null.",
                parameterName);
        if (stage.Metadata is null)
            throw new ArgumentException(
                $"Stage metadata at index {index} must not be null.",
                parameterName);
    }

    private static void ValidateTopologyTypes<TInput, TOutput>(
        PipelineStageTopologyEntry[] topology,
        string parameterName)
    {
        if (topology.Length > 0 && topology[0].InputType != typeof(TInput))
        {
            throw new ArgumentException(
                $"Stage '{topology[0].StageId}' at index 0 expects input type "
                + $"'{topology[0].InputType}', but the definition input type is '{typeof(TInput)}'.",
                parameterName);
        }

        var actualOutput = topology.Length == 0 ? typeof(TInput) : topology[^1].OutputType;
        if (actualOutput != typeof(TOutput))
        {
            throw new ArgumentException(
                $"Definition output type '{typeof(TOutput)}' does not match the final stage output "
                + $"type '{actualOutput}'.",
                parameterName);
        }
    }

    private static void ValidateObservers<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state)
    {
        for (var index = 0; index < state.Observers.Length; index++)
        {
            var registration = state.Observers[index]
                ?? throw new ArgumentException(
                    $"Observer registration at index {index} is null.",
                    nameof(state));

            ValidateObserverRegistration(registration, index, nameof(state));
        }
    }

    private static void ValidateObserverRegistration(
        PipelineObserverRegistration registration,
        int index,
        string parameterName)
    {
        if (registration.Observer is null)
            throw new ArgumentException(
                $"Observer at index {index} must not be null.",
                parameterName);
        if (!Enum.IsDefined(registration.Reliability))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                registration.Reliability,
                $"Observer reliability at index {index} is invalid.");
        }

        if (!Enum.IsDefined(registration.FailurePolicy))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                registration.FailurePolicy,
                $"Observer failure policy at index {index} is invalid.");
        }
    }

    private static bool RequiresServices<TInput, TOutput>(
        PipelineDefinitionState<TInput, TOutput> state,
        PipelineComponent<IPipelineSink<TOutput>>? sink) =>
        state.Source.Ownership == PipelineComponentOwnership.ScopeOwned
        || state.Stages.Any(stage => stage.RequiresServices)
        || sink?.Ownership == PipelineComponentOwnership.ScopeOwned;

    private static ReadOnlyCollection<T> AsReadOnlyCopy<T>(T[] values)
    {
        var copy = new T[values.Length];
        Array.Copy(values, copy, values.Length);
        return Array.AsReadOnly(copy);
    }
}

internal readonly record struct PipelineStageTopologyEntry(
    string StageId,
    string StageName,
    Type InputType,
    Type OutputType);

internal static class PipelineStageTopologyValidator
{
    public static void Validate(IReadOnlyList<PipelineStageTopologyEntry> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var indexesById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < stages.Count; index++)
        {
            var current = stages[index];
            if (string.IsNullOrWhiteSpace(current.StageId)
                || string.IsNullOrWhiteSpace(current.StageName)
                || current.InputType is null
                || current.OutputType is null)
            {
                throw new ArgumentException(
                    $"Stage topology entry at index {index} is incomplete.",
                    nameof(stages));
            }

            if (indexesById.TryGetValue(current.StageId, out var firstIndex))
            {
                throw new InvalidOperationException(
                    $"Duplicate stage ID '{current.StageId}' at indexes {firstIndex} and {index}.");
            }

            indexesById.Add(current.StageId, index);
            if (index == 0)
                continue;

            var previous = stages[index - 1];
            if (previous.OutputType != current.InputType)
            {
                throw new InvalidOperationException(
                    $"Stage '{current.StageName}' expects input type '{current.InputType.FullName}', "
                    + $"but previous stage '{previous.StageName}' outputs '{previous.OutputType.FullName}'.");
            }
        }
    }
}
