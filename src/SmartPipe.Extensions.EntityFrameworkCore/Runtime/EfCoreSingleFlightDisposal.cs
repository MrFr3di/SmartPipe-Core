#nullable enable

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Runs one asynchronous cleanup exactly once and shares its completion with every caller.</summary>
/// <remarks>
/// The cleanup task is created once under the lock, so every caller observes the same completion, and a
/// failed cleanup is never retried. A second caller therefore receives an empty cleanup-failure list and
/// does not report the same failure twice.
/// </remarks>
internal sealed class EfCoreSingleFlightDisposal
{
    private readonly object _sync = new();

    private Task? _task;

    /// <summary>Starts the cleanup on first use and returns the shared completion on every call.</summary>
    internal ValueTask DisposeAsync(Func<Task> cleanup)
    {
        Task task;
        lock (_sync)
        {
            task = _task ??= cleanup();
        }

        return new ValueTask(task);
    }
}
