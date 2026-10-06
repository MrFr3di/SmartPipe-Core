#nullable enable

namespace SmartPipe.Core;

/// <summary>Describes one resource-free stage in a pipeline definition.</summary>
public sealed class PipelineStageMetadata
{
    /// <summary>Creates immutable stage metadata.</summary>
    public PipelineStageMetadata(
        PipelineStageKey key,
        string name,
        Type inputType,
        Type outputType,
        StageFailureOptions failureOptions)
        : this(
            key,
            name,
            inputType,
            outputType,
            StageFailureOptionsSnapshot.Create(failureOptions))
    {
    }

    internal PipelineStageMetadata(
        PipelineStageKey key,
        string name,
        Type inputType,
        Type outputType,
        StageFailureOptionsSnapshot failureOptions)
    {
        PipelineStageKeyGuard.ThrowIfInvalid(key, nameof(key));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(outputType);
        ArgumentNullException.ThrowIfNull(failureOptions);
        failureOptions.Validate();

        Key = key;
        Name = name;
        InputType = inputType;
        OutputType = outputType;
        FailureOptions = failureOptions.Materialize();
    }

    /// <summary>Gets the stable stage key.</summary>
    public PipelineStageKey Key { get; }

    /// <summary>Gets the exact activity name.</summary>
    public string Name { get; }

    /// <summary>Gets the input payload type.</summary>
    public Type InputType { get; }

    /// <summary>Gets the output payload type.</summary>
    public Type OutputType { get; }

    /// <summary>Gets a defensive copy of the stage failure policy.</summary>
    public StageFailureOptions FailureOptions { get; }
}
