using System.Runtime.ExceptionServices;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Applies the Core failure-precedence policy: the primary operation failure is always surfaced first and cleanup
/// failures never replace it. A single primary failure keeps its original stack trace.
/// </summary>
internal static class PostgreSqlFailures
{
    /// <summary>Throws the primary failure, or the combined primary and cleanup failures, or the cleanup failures.</summary>
    internal static void Throw(Exception? primary, IReadOnlyList<Exception> cleanupFailures, string combinedMessage, string cleanupOnlyMessage)
    {
        if (primary is not null)
        {
            if (cleanupFailures.Count != 0)
                throw new AggregateException(combinedMessage, new[] { primary }.Concat(cleanupFailures));

            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1)
            throw new AggregateException(cleanupOnlyMessage, cleanupFailures);
    }
}
