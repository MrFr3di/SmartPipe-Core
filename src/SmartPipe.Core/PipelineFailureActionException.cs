#nullable enable

namespace SmartPipe.Core;

/// <summary>Exception thrown when a configured stage failure action faults the pipeline.</summary>
public sealed class PipelineFailureActionException : Exception
{
    /// <summary>Creates an exception for a policy-driven pipeline fault.</summary>
    /// <param name="stageId">Stage identifier that produced the terminal failure.</param>
    /// <param name="stageName">Stage name that produced the terminal failure.</param>
    /// <param name="error">Structured stage error.</param>
    public PipelineFailureActionException(string stageId, string stageName, SmartPipeError error)
        : base($"Stage '{stageName}' ({stageId}) faulted the pipeline: {error.Message}")
    {
        StageId = stageId;
        StageName = stageName;
        Error = error;
    }

    /// <summary>Gets the stage identifier.</summary>
    public string StageId { get; }

    /// <summary>Gets the stage name.</summary>
    public string StageName { get; }

    /// <summary>Gets the structured stage error.</summary>
    public SmartPipeError Error { get; }
}
