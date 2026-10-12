#nullable enable

namespace SmartPipe.Core;

/// <summary>Configures the shared retry token budget for each pipeline run.</summary>
public sealed class RetryBudgetOptions
{
    /// <summary>Creates a budget that starts full; zero refill provides a finite retry allowance.</summary>
    /// <param name="capacity">Maximum available and reserved tokens combined.</param>
    /// <param name="refillTokensPerSecond">Finite, nonnegative token refill rate.</param>
    public RetryBudgetOptions(int capacity, double refillTokensPerSecond)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (!double.IsFinite(refillTokensPerSecond) || refillTokensPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(refillTokensPerSecond));
        Capacity = capacity;
        RefillTokensPerSecond = refillTokensPerSecond;
    }

    /// <summary>Gets the maximum available and reserved tokens combined.</summary>
    public int Capacity { get; }

    /// <summary>Gets the lazy refill rate in tokens per second.</summary>
    public double RefillTokensPerSecond { get; }
}
