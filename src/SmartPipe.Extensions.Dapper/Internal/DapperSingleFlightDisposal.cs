#nullable enable

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Runs one asynchronous cleanup exactly once and shares its completion with every caller.</summary>
/// <remarks>
/// The cleanup task is created once under the lock, so every caller observes the same completion and the same
/// failure, and a failed cleanup is never retried.
/// </remarks>
internal sealed class DapperSingleFlightDisposal
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
