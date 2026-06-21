#nullable enable

namespace SmartPipe.Core;

/// <summary>Describes how a component was registered in a pipeline definition.</summary>
/// <param name="ComponentType">Registered component type.</param>
/// <param name="Lifetime">Component lifetime.</param>
/// <param name="OwnsResources">Whether the runtime owns resources for this component.</param>
/// <param name="IsFactoryBased">Whether the component is created by a factory for each run.</param>
public sealed record PipelineComponentRegistration(
    Type ComponentType,
    PipelineComponentLifetime Lifetime,
    bool OwnsResources,
    bool IsFactoryBased
);

/// <summary>Describes one stage in a pipeline definition.</summary>
/// <param name="StageId">Stable stage identifier.</param>
/// <param name="StageName">Human-readable stage name.</param>
/// <param name="InputType">Stage input type.</param>
/// <param name="OutputType">Stage output type.</param>
/// <param name="FailureOptions">Stage failure policy.</param>
public sealed record PipelineStageDefinition(
    string StageId,
    string StageName,
    Type InputType,
    Type OutputType,
    StageFailureOptions FailureOptions
);

/// <summary>Immutable declarative description of a pipeline topology.</summary>
/// <remarks>
/// A definition may be factory-based or instance-based. Factory-based definitions can create
/// multiple runtimes safely. Instance-based definitions are treated as single-use unless all
/// registered components explicitly declare reusable or external singleton lifetime.
/// </remarks>
public sealed class PipelineDefinition
{
    private int _runtimeCreated;

    internal PipelineDefinition(
        string pipelineId,
        PipelineRuntimeOptions runtimeOptions,
        IEnumerable<PipelineComponentRegistration>? components = null,
        IEnumerable<PipelineStageDefinition>? stages = null,
        ComponentOwnershipOptions? ownershipOptions = null,
        LineageMode lineageMode = LineageMode.Minimal
    )
        : this(pipelineId, components, stages, ownershipOptions, lineageMode)
    {
        RuntimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        RuntimeOptions.Validate();
    }

    /// <summary>Creates a pipeline definition.</summary>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <param name="components">Registered components.</param>
    /// <param name="stages">Pipeline stages.</param>
    /// <param name="ownershipOptions">Component ownership options.</param>
    /// <param name="lineageMode">Lineage recording mode.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pipelineId"/> is empty.</exception>
    public PipelineDefinition(
        string pipelineId,
        IEnumerable<PipelineComponentRegistration>? components = null,
        IEnumerable<PipelineStageDefinition>? stages = null,
        ComponentOwnershipOptions? ownershipOptions = null,
        LineageMode lineageMode = LineageMode.Minimal
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        PipelineId = pipelineId;
        Components = (components ?? []).ToArray();
        Stages = (stages ?? []).ToArray();
        OwnershipOptions = ownershipOptions ?? new ComponentOwnershipOptions();
        LineageMode = lineageMode;
        RuntimeOptions = new PipelineRuntimeOptions();
    }

    /// <summary>Gets the pipeline identifier.</summary>
    public string PipelineId { get; }

    /// <summary>Gets registered component metadata.</summary>
    public IReadOnlyList<PipelineComponentRegistration> Components { get; }

    /// <summary>Gets declared pipeline stages.</summary>
    public IReadOnlyList<PipelineStageDefinition> Stages { get; }

    /// <summary>Gets component ownership options.</summary>
    public ComponentOwnershipOptions OwnershipOptions { get; }

    /// <summary>Gets lineage recording mode.</summary>
    public LineageMode LineageMode { get; }

    /// <summary>Gets runtime execution options.</summary>
    public PipelineRuntimeOptions RuntimeOptions { get; }

    /// <summary>Gets a value indicating whether this definition can safely create multiple runtimes.</summary>
    public bool IsReusable =>
        Components.All(c =>
            c.IsFactoryBased
            || c.Lifetime
                is PipelineComponentLifetime.Reusable
                    or PipelineComponentLifetime.SingletonExternal
        );

    /// <summary>Marks that a runtime is being created and validates reuse rules.</summary>
    /// <exception cref="InvalidOperationException">Thrown when a single-use definition is reused.</exception>
    public void MarkRuntimeCreated()
    {
        if (IsReusable)
            return;

        if (Interlocked.Exchange(ref _runtimeCreated, 1) == 1)
            throw new InvalidOperationException(
                "This pipeline definition contains single-use component instances and cannot create multiple runtimes. "
                    + "Use factory-based registration or components that declare reusable lifetime."
            );
    }
}
