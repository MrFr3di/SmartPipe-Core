#nullable enable

using System.Collections.ObjectModel;

namespace SmartPipe.Core;

/// <summary>Reports an activation failure accompanied by rollback failures.</summary>
public sealed class PipelineActivationException : Exception
{
    /// <summary>Creates an activation exception with ordered rollback failures.</summary>
    public PipelineActivationException(
        PipelineKey pipelineKey,
        Guid runId,
        Exception primaryException,
        IEnumerable<Exception> cleanupExceptions)
        : base(CreateMessage(pipelineKey, runId), primaryException)
    {
        PipelineKeyGuard.ThrowIfInvalid(pipelineKey);
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId must not be empty.", nameof(runId));
        ArgumentNullException.ThrowIfNull(primaryException);
        ArgumentNullException.ThrowIfNull(cleanupExceptions);

        var copiedExceptions = cleanupExceptions.ToArray();
        if (copiedExceptions.Any(static exception => exception is null))
        {
            throw new ArgumentException(
                "Cleanup exceptions must not contain null entries.",
                nameof(cleanupExceptions));
        }

        PipelineKey = pipelineKey;
        RunId = runId;
        CleanupExceptions = new ReadOnlyCollection<Exception>(copiedExceptions);
    }

    /// <summary>Gets the definition key whose activation failed.</summary>
    public PipelineKey PipelineKey { get; }

    /// <summary>Gets the run identifier whose activation failed.</summary>
    public Guid RunId { get; }

    /// <summary>Gets rollback failures in attempted cleanup order.</summary>
    public IReadOnlyList<Exception> CleanupExceptions { get; }

    private static string CreateMessage(PipelineKey pipelineKey, Guid runId) =>
        $"Activation of pipeline '{pipelineKey}' for run '{runId}' failed and rollback reported errors.";
}
