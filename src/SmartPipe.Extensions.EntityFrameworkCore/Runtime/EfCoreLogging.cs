#nullable enable

using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

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
        string operationName,
        string resultTypeName,
        string kind,
        long itemCount,
        TimeSpan elapsed,
        Exception? primaryFailure)
    {
        if (logger is null)
            return;

        if (primaryFailure is null)
        {
            logger.LogInformation(
                "EF Core {Kind} {OperationName} completed for {ResultType} in pipeline {PipelineKey} run {RunId}: items={ItemCount} durationMs={DurationMs}.",
                kind,
                operationName,
                resultTypeName,
                activation.PipelineKey.Value,
                activation.RunId,
                itemCount,
                elapsed.TotalMilliseconds);
            return;
        }

        logger.LogError(
            primaryFailure,
            "EF Core {Kind} {OperationName} failed for {ResultType} in pipeline {PipelineKey} run {RunId} after {ItemCount} items.",
            kind,
            operationName,
            resultTypeName,
            activation.PipelineKey.Value,
            activation.RunId,
            itemCount);
    }
}
