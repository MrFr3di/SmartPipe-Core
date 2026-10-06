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
        var lifetime = new PipelineActivationLedger();
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "source",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = source.DisposeAsync,
        });
        var manager = new PipelineComponentLifetimeManager<int, int>(
            lifetime,
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

    [Fact]
    public async Task DisposeAsync_UsesActivationLedgerInReverseOrder()
    {
        var calls = new List<string>();
        var lifetime = new PipelineActivationLedger();
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "source",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => RecordAsync(calls, "source"),
        });
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "stage",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            StageKey = new PipelineStageKey("normalize"),
            RuntimeOwnedCleanup = () => RecordAsync(calls, "stage"),
        });
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "sink",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => RecordAsync(calls, "sink"),
        });

        var manager = new PipelineComponentLifetimeManager<int, int>(
            lifetime,
            new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance)));

        var result = await manager.DisposeAsync();

        calls.Should().Equal("sink", "stage", "source");
        result.CompletionErrors.Should().BeEmpty();
        result.DisposeErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_SkipsNonRuntimeOwnedLeases()
    {
        var calls = new List<string>();
        var lifetime = new PipelineActivationLedger();
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "source",
            Ownership = PipelineComponentOwnership.ScopeOwned,
        });
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "stage",
            Ownership = PipelineComponentOwnership.ExternallyOwned,
        });
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "sink",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            RuntimeOwnedCleanup = () => RecordAsync(calls, "sink"),
        });

        var manager = new PipelineComponentLifetimeManager<int, int>(
            lifetime,
            new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance)));

        var result = await manager.DisposeAsync();

        calls.Should().Equal("sink");
        result.CompletionErrors.Should().BeEmpty();
        result.DisposeErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_DefersStageLeaseUntilLateAttemptCompletes()
    {
        var calls = new List<string>();
        var lifetime = new PipelineActivationLedger();
        lifetime.Append(new ActivatedComponentLease
        {
            Role = "stage",
            Ownership = PipelineComponentOwnership.RuntimeOwned,
            StageKey = new PipelineStageKey("normalize"),
            RuntimeOwnedCleanup = () => RecordAsync(calls, "stage"),
        });
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        using var timeoutCancellation = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(
            "normalize",
            "Normalize",
            traceId: 42,
            attempt: 1,
            execution.Task,
            timeoutCancellation,
            TimeSpan.Zero);

        var manager = new PipelineComponentLifetimeManager<int, int>(lifetime, registry);
        var result = await manager.DisposeAsync();

        result.CompletionErrors.Should().ContainSingle().Which.Should().BeOfType<TimeoutException>();
        calls.Should().BeEmpty();

        execution.SetResult();
        var deferredErrors = await manager.DisposeDeferredStagesAsync();

        deferredErrors.Should().BeEmpty();
        calls.Should().Equal("stage");
    }

    private static ValueTask RecordAsync(List<string> calls, string role)
    {
        calls.Add(role);
        return ValueTask.CompletedTask;
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
