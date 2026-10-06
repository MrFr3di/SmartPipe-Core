using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms.Tests;

public sealed class FilterTransformTests
{
    [Fact]
    public async Task TokenAwarePredicateReceivesExactTokenAndCancellation()
    {
        CancellationToken observed = default;
        var filter = new FilterTransform<int>((_, token) =>
        {
            observed = token;
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            filter.TransformAsync(ProcessingEnvelope<int>.Create(1), cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, observed);
    }

    [Fact]
    public async Task AndOrAndNotShortCircuitAndPreserveExactToken()
    {
        using var cancellation = new CancellationTokenSource();
        int rightCalls = 0;
        var falseFilter = new FilterTransform<int>((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            return ValueTask.FromResult(false);
        });
        var trueFilter = new FilterTransform<int>((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            rightCalls++;
            return ValueTask.FromResult(true);
        });
        ProcessingEnvelope<int> envelope = ProcessingEnvelope<int>.Create(1);

        Assert.Equal(StageResultKind.Filtered, (await (falseFilter & trueFilter).TransformAsync(envelope, cancellation.Token)).Kind);
        Assert.Equal(0, rightCalls);
        Assert.True((await (trueFilter | falseFilter).TransformAsync(envelope, cancellation.Token)).IsSuccess);
        Assert.Equal(1, rightCalls);
        Assert.True((await (!falseFilter).TransformAsync(envelope, cancellation.Token)).IsSuccess);
    }

    [Fact]
    public async Task ExistingSyncAndTaskConstructorsKeepTheirBehavior()
    {
        var synchronous = new FilterTransform<int>(static value => value > 0);
        var taskBased = new FilterTransform<int>(static value => Task.FromResult(value > 0));

        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.True((await synchronous.TransformAsync(ProcessingEnvelope<int>.Create(1), token)).IsSuccess);
        Assert.True((await taskBased.TransformAsync(ProcessingEnvelope<int>.Create(1), token)).IsSuccess);
        Assert.Equal(StageResultKind.Filtered, (await synchronous.TransformAsync(ProcessingEnvelope<int>.Create(0), token)).Kind);
        Assert.Equal(StageResultKind.Filtered, (await taskBased.TransformAsync(ProcessingEnvelope<int>.Create(0), token)).Kind);
    }

    [Fact]
    public async Task LegacyTaskPredicateIsNotCanceledBehindTheDelegate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filter = new FilterTransform<int>(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        });
        using var cancellation = new CancellationTokenSource();

        Task operation = filter.TransformAsync(
            ProcessingEnvelope<int>.Create(1), cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.False(operation.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }
}
