using System.Runtime.ExceptionServices;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class PipelineProcessingSupervisorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ProducerPrimary_IsPreservedAndAllIndependentWorkersAreJoined()
    {
        using var supervisor = new PipelineProcessingSupervisor(CancellationToken.None, () => { });
        var source = new IOException("source failed");
        var first = new InvalidOperationException("first worker failed");
        var second = new InvalidOperationException("second worker failed");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new[] { FailAsync(first), FailAsync(second) };
        var joined = supervisor.JoinAsync(workers, ExceptionDispatchInfo.Capture(source)).AsTask();
        Assert.False(joined.IsCompleted);
        release.TrySetResult();
        var result = await joined.WaitAsync(Deadline);
        Assert.Same(source, result.PrimaryError!.SourceException);
        Assert.Contains(first, result.SecondaryErrors);
        Assert.Contains(second, result.SecondaryErrors);
        Assert.All(workers, worker => Assert.True(worker.IsCompleted));

        async Task FailAsync(Exception error)
        {
            await release.Task.ConfigureAwait(false);
            supervisor.ReportWorkerFailure(error);
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [Fact]
    public async Task FirstReportedWorkerFailure_IsPrimaryRegardlessOfTaskOrder()
    {
        var sourceStops = 0;
        using var supervisor = new PipelineProcessingSupervisor(CancellationToken.None, () => sourceStops++);
        var first = new IOException("first reported");
        var later = new InvalidOperationException("later reported");
        supervisor.ReportWorkerFailure(first);
        supervisor.ReportWorkerFailure(later);
        var result = await supervisor.JoinAsync([Task.FromException(later), Task.FromException(first)], null);
        Assert.Same(first, result.PrimaryError!.SourceException);
        Assert.Same(later, Assert.Single(result.SecondaryErrors));
        Assert.True(sourceStops > 0);
        Assert.True(supervisor.WorkerToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ThrowingSourceAndWorkerStopCallbacks_DoNotBypassJoin()
    {
        var sourceStop = new IOException("source cancellation callback");
        var workerStop = new InvalidOperationException("worker cancellation callback");
        var primary = new IOException("producer primary");
        using var supervisor = new PipelineProcessingSupervisor(CancellationToken.None, () => throw sourceStop);
        using var registration = supervisor.WorkerToken.Register(() => throw workerStop);
        supervisor.ReportWorkerFailure(primary);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var join = supervisor.JoinAsync([release.Task], ExceptionDispatchInfo.Capture(primary)).AsTask();
        Assert.True(supervisor.WorkerToken.IsCancellationRequested);
        Assert.False(join.IsCompleted);
        release.TrySetResult();
        var result = await join.WaitAsync(Deadline);
        Assert.Same(primary, result.PrimaryError!.SourceException);
        var errors = new AggregateException(result.SecondaryErrors).Flatten().InnerExceptions;
        Assert.Contains(sourceStop, errors);
        Assert.Contains(workerStop, errors);
    }

    [Fact]
    public async Task Join_FiltersOnlyRequestedWorkerTokenCancellation()
    {
        using var supervisor = new PipelineProcessingSupervisor(CancellationToken.None, () => { });
        supervisor.RequestStop();
        var primary = new IOException("producer primary");
        var expected = new OperationCanceledException(supervisor.WorkerToken);
        var unexpected = new OperationCanceledException("user code OCE", CancellationToken.None);
        var result = await supervisor.JoinAsync(
            [Task.FromException(expected), Task.FromException(unexpected)], ExceptionDispatchInfo.Capture(primary));
        Assert.Same(primary, result.PrimaryError!.SourceException);
        Assert.Same(unexpected, Assert.Single(result.SecondaryErrors));
    }
}
