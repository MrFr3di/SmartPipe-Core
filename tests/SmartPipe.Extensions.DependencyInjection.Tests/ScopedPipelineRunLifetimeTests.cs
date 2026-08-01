using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class ScopedPipelineRunLifetimeTests
{
    [Fact]
    public async Task NaturalCompletionDuringConcurrentDispose_SharesOneCleanupOutcome()
    {
        var innerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerDisposeCalls = 0;
        var cleanupOrder = new List<string>();
        var lease = new CountingLease(recorder: cleanupOrder);
        var scope = new CountingScope(recorder: cleanupOrder);
        var inner = CreateRun(
            innerCompletion.Task,
            async () =>
            {
                Interlocked.Increment(ref innerDisposeCalls);
                cleanupOrder.Add("inner");
                cleanupEntered.SetResult();
                await cleanupRelease.Task;
            });
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            inner,
            lease,
            new AsyncServiceScope(scope));

        var disposals = Enumerable.Range(0, 64)
            .Select(_ => lifetime.DisposeAsync().AsTask())
            .ToArray();
        await cleanupEntered.Task;
        innerCompletion.SetResult();
        cleanupRelease.SetResult();

        await Task.WhenAll(disposals.Append(lifetime.Completion));

        Assert.All(disposals, task => Assert.Same(disposals[0], task));
        Assert.Equal(1, innerDisposeCalls);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.Equal(1, scope.DisposeCalls);
        Assert.Equal(["inner", "registry", "scope"], cleanupOrder);
    }

    [Fact]
    public async Task CompletionFailure_WithCleanupFailures_AggregatesInCleanupOrder()
    {
        var terminalError = new InvalidOperationException("terminal");
        var innerDisposeError = new InvalidOperationException("inner dispose");
        var leaseError = new InvalidOperationException("registry lease");
        var scopeError = new InvalidOperationException("scope dispose");
        var inner = CreateRun(
            Task.FromException(terminalError),
            () => ValueTask.FromException(innerDisposeError));
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            inner,
            new CountingLease(leaseError),
            new AsyncServiceScope(new CountingScope(scopeError)));

        var error = await Assert.ThrowsAsync<AggregateException>(() => lifetime.Completion);

        Assert.Equal(
            [terminalError, innerDisposeError, leaseError, scopeError],
            error.InnerExceptions);
    }

    private static PipelineRun<int> CreateRun(Task completion, Func<ValueTask> dispose) =>
        new(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion,
            static () => PipelineRunState.Running,
            dispose: dispose);

    private sealed class CountingLease : IDisposable
    {
        private readonly Exception? _error;
        private readonly List<string>? _recorder;

        internal CountingLease(Exception? error = null, List<string>? recorder = null)
        {
            _error = error;
            _recorder = recorder;
        }

        internal int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            _recorder?.Add("registry");
            if (_error is not null)
            {
                throw _error;
            }
        }
    }

    private sealed class CountingScope : IServiceScope, IAsyncDisposable
    {
        private readonly Exception? _error;
        private readonly List<string>? _recorder;

        internal CountingScope(Exception? error = null, List<string>? recorder = null)
        {
            _error = error;
            _recorder = recorder;
        }

        internal int DisposeCalls { get; private set; }

        public IServiceProvider ServiceProvider => throw new NotSupportedException();

        public void Dispose() => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            _recorder?.Add("scope");
            return _error is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_error);
        }
    }
}
