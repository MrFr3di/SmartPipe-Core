using System;

namespace SmartPipe.Core;

public enum BackoffStrategy { Fixed, Linear, Exponential }

public class RetryPolicy
{
    public int MaxRetries { get; }
    public TimeSpan Delay { get; }
    public TimeSpan MaxDelay { get; }
    public BackoffStrategy Strategy { get; }
    public Predicate<SmartPipeError> RetryOn { get; }
    public Action<ProcessingContext<object>, SmartPipeError, int>? OnRetry { get; }

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

    public bool ShouldRetry(SmartPipeError error) => RetryOn(error);

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
