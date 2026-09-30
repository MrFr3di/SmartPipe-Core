#nullable enable

namespace SmartPipe.Core;

internal static class PipelineDrainTimeout
{
    // Largest finite timeout accepted by Task.WaitAsync and timer-backed waits.
    private const double MaxSupportedMilliseconds = uint.MaxValue - 1d;

    public static void ThrowIfInvalid(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return;

        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > MaxSupportedMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Drain timeout must be non-negative and at most 4294967294 milliseconds, or Timeout.InfiniteTimeSpan.");
        }
    }
}
