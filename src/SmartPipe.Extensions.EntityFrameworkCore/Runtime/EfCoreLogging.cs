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

/// <summary>Writes payload-free Entity Framework Core outcome log lines.</summary>
/// <remarks>
/// Query text, parameter values, connection strings, payload contents, and provider exception details are never logged
/// by SmartPipe. The original exception is still propagated to the caller unchanged.
/// </remarks>
internal static partial class EfCoreLogging
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
            LogCompleted(
                logger,
                outcome.Kind,
                outcome.OperationName,
                outcome.ResultTypeName,
                activation.PipelineKey.Value,
                activation.RunId,
                outcome.ItemCount,
                outcome.Elapsed.TotalMilliseconds);
            return;
        }

        LogFailed(
            logger,
            outcome.Kind,
            outcome.OperationName,
            outcome.ResultTypeName,
            activation.PipelineKey.Value,
            activation.RunId,
            outcome.ItemCount,
            primaryFailure is OperationCanceledException ? "cancelled" : "failed");
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "EF Core {Kind} {OperationName} completed for {ResultType} in pipeline {PipelineKey} run {RunId}: items={ItemCount} durationMs={DurationMs}.")]
    private static partial void LogCompleted(
        ILogger logger,
        string kind,
        string operationName,
        string resultType,
        string pipelineKey,
        Guid runId,
        long itemCount,
        double durationMs);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "EF Core {Kind} {OperationName} {Outcome} for {ResultType} in pipeline {PipelineKey} run {RunId} after {ItemCount} items.")]
    private static partial void LogFailed(
        ILogger logger,
        string kind,
        string operationName,
        string resultType,
        string pipelineKey,
        Guid runId,
        long itemCount,
        string outcome);
}
