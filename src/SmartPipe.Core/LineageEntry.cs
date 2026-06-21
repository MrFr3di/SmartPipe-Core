#nullable enable

namespace SmartPipe.Core;

/// <summary>Controls how much per-item lineage the runtime records.</summary>
/// <remarks>
/// Full lineage is useful for diagnostics and memory integration, but can be expensive for
/// high-throughput pipelines. Production hot paths should prefer <see cref="Minimal"/> or
/// <see cref="ErrorsOnly"/> unless the extra detail is required.
/// </remarks>
public enum LineageMode
{
    /// <summary>Do not record per-item lineage entries.</summary>
    Off,

    /// <summary>Record lineage only for failed items.</summary>
    ErrorsOnly,

    /// <summary>Record compact stage identifiers and outcomes.</summary>
    Minimal,

    /// <summary>Record full stage timing and type information for every item.</summary>
    Full,
}

/// <summary>Represents the outcome of one pipeline stage for one item.</summary>
public enum StageOutcome
{
    /// <summary>The stage started processing.</summary>
    Started,

    /// <summary>The stage completed successfully.</summary>
    Succeeded,

    /// <summary>The stage failed.</summary>
    Failed,

    /// <summary>The stage was skipped by policy.</summary>
    Skipped,

    /// <summary>The stage was cancelled.</summary>
    Cancelled,

    /// <summary>The stage exceeded its timeout.</summary>
    TimedOut,

    /// <summary>The stage filtered the item out as normal control flow.</summary>
    Filtered,
}

/// <summary>Serializable lineage information for one stage execution.</summary>
/// <param name="StageId">Stable stage identifier within the pipeline definition.</param>
/// <param name="StageName">Human-readable stage name.</param>
/// <param name="InputTypeName">Input type name stored as text for serialization and AOT compatibility.</param>
/// <param name="OutputTypeName">Output type name stored as text for serialization and AOT compatibility.</param>
/// <param name="StartedAtUtc">UTC timestamp when the stage started.</param>
/// <param name="CompletedAtUtc">UTC timestamp when the stage completed, if known.</param>
/// <param name="Outcome">Observed stage outcome.</param>
public sealed record LineageEntry(
    string StageId,
    string StageName,
    string InputTypeName,
    string OutputTypeName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    StageOutcome Outcome
);
