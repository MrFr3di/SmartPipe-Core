#nullable enable

using System;

namespace SmartPipe.Core;

/// <summary>Backoff strategies for retry delays.</summary>
public enum BackoffStrategy
{
    /// <summary>Fixed delay between retries.</summary>
    Fixed,
    /// <summary>Linearly increasing delay (delay * attempt).</summary>
    Linear,
    /// <summary>Exponentially increasing delay (delay * 2^(attempt-1)).</summary>
    Exponential
}

/// <summary>Defines retry behavior with configurable backoff strategy.</summary>
/// <remarks>
/// Default retry condition: <see cref="ErrorType.Transient"/> errors only.
/// Use <see cref="GetDelay"/> to calculate delay for a given retry attempt.
/// </remarks>
public class RetryPolicy
{
    /// <summary>Maximum number of retry attempts.</summary>
    public int MaxRetries { get; }

    /// <summary>Base delay between retries.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Maximum allowed delay (caps exponential growth).</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>Backoff strategy for calculating delays.</summary>
    public BackoffStrategy Strategy { get; }

    /// <summary>Predicate to determine if an error should be retried.</summary>
    public Predicate<SmartPipeError> RetryOn { get; }

    /// <summary>Callback invoked before each retry attempt.</summary>
    public Action<ProcessingContext<object>, SmartPipeError, int>? OnRetry { get; }

    /// <summary>Creates a new retry policy.</summary>
    /// <param name="maxRetries">Maximum retry attempts (must be > 0).</param>
    /// <param name="delay">Base delay between retries.</param>
    /// <param name="maxDelay">Maximum delay cap.</param>
    /// <param name="strategy">Backoff strategy.</param>
    /// <param name="retryOn">Predicate for retryable errors (default: transient only).</param>
    /// <param name="onRetry">Callback before retry.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when maxRetries ≤ 0.</exception>
    public RetryPolicy(
        int maxRetries = 3,
        TimeSpan? delay = null,
        TimeSpan? maxDelay = null,
        BackoffStrategy strategy = BackoffStrategy.Exponential,
        Predicate<SmartPipeError>? retryOn = null,
        Action<ProcessingContext<object>, SmartPipeError, int>? onRetry = null)
    {
        MaxRetries = maxRetries > 0 ? maxRetries : throw new ArgumentOutOfRangeException(nameof(maxRetries));
        Delay = delay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        Strategy = strategy;
        RetryOn = retryOn ?? (error => error.Type == ErrorType.Transient);
        OnRetry = onRetry;
    }

    /// <summary>Checks if the error should be retried.</summary>
    /// <param name="error">Error to evaluate.</param>
    /// <returns>True if the error is retryable.</returns>
    public bool ShouldRetry(SmartPipeError error) => RetryOn(error);

    /// <summary>Calculates the delay for a given retry attempt.</summary>
    /// <param name="retryCount">Current retry attempt (1-based).</param>
    /// <returns>Delay before next retry.</returns>
    /// <remarks>Result is capped at <see cref="MaxDelay"/>.</remarks>
    public TimeSpan GetDelay(int retryCount)
    {
        if (retryCount < 1) return TimeSpan.Zero;

        long ticks;
        try
        {
            ticks = Strategy switch
            {
                BackoffStrategy.Fixed => Delay.Ticks,
                BackoffStrategy.Linear => checked(Delay.Ticks * retryCount),
                BackoffStrategy.Exponential => checked(Delay.Ticks * (long)Math.Pow(2, Math.Min(retryCount - 1, 62))),
                _ => Delay.Ticks
            };
        }
        catch (OverflowException)
        {
            return MaxDelay; // Overflow means we're way past MaxDelay
        }

        return TimeSpan.FromTicks(Math.Min(Math.Max(ticks, 1), MaxDelay.Ticks));
    }
}
