#nullable enable

using System.Runtime.ExceptionServices;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Keeps the primary Entity Framework Core failure first and appends cleanup failures in order.</summary>
/// <remarks>
/// Cleanup failures never replace or hide the failure that triggered cleanup: the primary exception is
/// always the first entry of the reported aggregate, followed by every cleanup failure in the exact order
/// in which the cleanup steps ran (enumerator disposal, then context disposal).
/// </remarks>
internal static class EfCoreCleanup
{
    /// <summary>Rethrows the collected failures, with the primary failure always first.</summary>
    internal static void ThrowPrimaryFirst(
        Exception? primaryFailure,
        IReadOnlyList<Exception> cleanupFailures,
        string aggregateMessage)
    {
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count != 0)
                throw new AggregateException(aggregateMessage, new[] { primaryFailure }.Concat(cleanupFailures));

            return;
        }

        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1)
            throw new AggregateException(aggregateMessage, cleanupFailures);
    }
}
