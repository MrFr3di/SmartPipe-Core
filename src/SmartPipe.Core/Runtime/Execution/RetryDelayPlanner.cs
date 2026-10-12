#nullable enable

namespace SmartPipe.Core;

internal interface IRetryRandomSource
{
    long NextInt64(long exclusiveUpperBound);
}

internal sealed class SharedRetryRandomSource : IRetryRandomSource
{
    public long NextInt64(long exclusiveUpperBound) => Random.Shared.NextInt64(exclusiveUpperBound);
}

internal sealed class RetryDelayPlanner(IRetryRandomSource random)
{
    public TimeSpan GetDelay(RetryPolicy policy, int nextAttempt)
    {
        var delay = policy.GetDelay(nextAttempt);
        return policy.Jitter == RetryJitterMode.Full && delay.Ticks > 0
            ? TimeSpan.FromTicks(random.NextInt64(delay.Ticks))
            : delay;
    }
}
