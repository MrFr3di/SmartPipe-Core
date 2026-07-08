#nullable enable

using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

[Trait("Category", "CorrectnessRegression")]
public sealed class PipelineComponentLifetimeManagerTests
{
    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task DisposeAsync_ConcurrentCallers_ShouldShareInFlightCleanupResult()
    {
        var cleanup = new InvalidOperationException("source cleanup boom");
        var source = new BlockingDisposeSource<int>(cleanup);
        var manager = new PipelineComponentLifetimeManager<int, int>(
            source,
            [],
            sink: null,
            ComponentOwnershipOptions.Default,
            new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance)));

        var first = manager.DisposeAsync().AsTask();
        await source.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = manager.DisposeAsync().AsTask();

        second.IsCompleted.Should().BeFalse();

        source.ReleaseDispose();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        results[0].CompletionErrors.Should().ContainSingle().Which.Should().BeSameAs(cleanup);
        results[1].CompletionErrors.Should().ContainSingle().Which.Should().BeSameAs(cleanup);
        source.DisposeCalls.Should().Be(1);
    }

    private sealed class BlockingDisposeSource<T> : IPipelineSource<T>
    {
        private readonly Exception _exception;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        public BlockingDisposeSource(Exception exception)
        {
            _exception = exception;
        }

        public TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void ReleaseDispose() => _release.TrySetResult();

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            throw _exception;
        }
    }
}
