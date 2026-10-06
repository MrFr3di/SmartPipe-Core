#nullable enable

using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Describes one completed or failed source run for the outcome log line.</summary>
/// <param name="Kind">The source kind, for example <c>query</c> or <c>compiled query</c>.</param>
/// <param name="OperationName">The validated caller-supplied operation name.</param>
/// <param name="ResultTypeName">The result type name.</param>
/// <param name="ItemCount">The number of items produced before completion or failure.</param>
/// <param name="Elapsed">The elapsed run time.</param>
internal readonly record struct EfCoreOutcome(
    string Kind,
    string OperationName,
    string ResultTypeName,
    long ItemCount,
    TimeSpan Elapsed);

/// <summary>Writes the payload-free Entity Framework Core outcome log line.</summary>
/// <remarks>
/// Query text, parameter values, connection strings, and payload contents are never logged.
/// </remarks>
internal static class EfCoreLogging
{
    /// <summary>Logs one completed or failed source run using identity and count metadata only.</summary>
    internal static void LogOutcome(
        ILogger? logger,
        PipelineActivationContext activation,
        EfCoreOutcome outcome,
        Exception? primaryFailure)
    {
        if (logger is null)
            return;

        if (primaryFailure is null)
        {
            logger.LogInformation(
                "EF Core {Kind} {OperationName} completed for {ResultType} in pipeline {PipelineKey} run {RunId}: items={ItemCount} durationMs={DurationMs}.",
                outcome.Kind,
                outcome.OperationName,
                outcome.ResultTypeName,
                activation.PipelineKey.Value,
                activation.RunId,
                outcome.ItemCount,
                outcome.Elapsed.TotalMilliseconds);
            return;
        }

        logger.LogError(
            primaryFailure,
            "EF Core {Kind} {OperationName} failed for {ResultType} in pipeline {PipelineKey} run {RunId} after {ItemCount} items.",
            outcome.Kind,
            outcome.OperationName,
            outcome.ResultTypeName,
            activation.PipelineKey.Value,
            activation.RunId,
            outcome.ItemCount);
    }
}
