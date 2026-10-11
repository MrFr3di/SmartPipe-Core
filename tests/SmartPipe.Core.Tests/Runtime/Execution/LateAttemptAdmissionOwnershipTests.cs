using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

[Trait("Category", "CorrectnessRegression")]
public sealed class LateAttemptAdmissionOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryOwnsSlotUntilObservationAndCtsDisposal_IncludingRejectedRegistration(bool sealedRegistry)
    {
        var time = new PipelineTime(SystemPipelineClock.Instance);
        var limiter = new TimedAttemptAdmissionLimiter(new TimedAttemptAdmissionOptions(1, TimedAttemptAdmissionMode.Wait), time);
        var lease = await limiter.AcquireAsync(null, default);
        var registry = new LateStageAttemptRegistry(time);
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource();
        if (sealedRegistry) registry.Seal();
        void Register() => registry.Register("held", "held", 1, 0, execution.Task, timeout,
            TimeSpan.Zero, timedLease: lease);
        if (sealedRegistry) Assert.Throws<InvalidOperationException>(Register);
        else Register();
        var next = limiter.AcquireAsync(null, default).AsTask();
        Assert.False(next.IsCompleted);
        if (!sealedRegistry)
        {
            Assert.IsType<TimeoutException>(Assert.Single(await registry.WaitForAllAsync()));
            Assert.False(next.IsCompleted);
        }
        execution.SetException(new IOException("late"));
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(acquired);
        Assert.Throws<ObjectDisposedException>(() => timeout.Token);
        lease!.Dispose(); // Repeated ownership release cannot exceed capacity.
        using var stop = new CancellationTokenSource();
        var blocked = limiter.AcquireAsync(null, stop.Token).AsTask();
        Assert.False(blocked.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }
}
