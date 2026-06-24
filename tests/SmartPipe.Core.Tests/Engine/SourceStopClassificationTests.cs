#nullable enable

using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class SourceStopClassificationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DrainCancellation_RemainsGraceful_WhenRuntimeCancellationArrivesLater(
        int maxConcurrency)
    {
        var source = new ControlledCancellationSource<int>("drain source cancellation");
        var run = CreateRun(source, maxConcurrency);

        try
        {
            await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var drainTask = run.TryDrainAsync(TimeSpan.FromSeconds(5)).AsTask();
            await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await run.CancelAsync();
            source.ReleaseException();

            var completionException = await Record.ExceptionAsync(
                async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            var drainResult = await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

            completionException.Should().NotBeSameAs(source.Exception);
            if (completionException is not null)
                completionException.Should().BeAssignableTo<OperationCanceledException>();
            drainResult.Exception.Should().NotBeSameAs(source.Exception);
            run.State.Should().Be(PipelineRunState.Cancelled);
        }
        finally
        {
            source.ReleaseException();
            await run.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RuntimeCancellation_RemainsNonGraceful_WhenDrainArrivesLater(
        int maxConcurrency)
    {
        var source = new ControlledCancellationSource<int>("runtime source cancellation");
        var run = CreateRun(source, maxConcurrency);

        try
        {
            await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var cancelTask = run.CancelAsync().AsTask();
            await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var drainTask = run.TryDrainAsync(TimeSpan.FromSeconds(5)).AsTask();
            source.ReleaseException();

            await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
            var completionException = await Record.ExceptionAsync(
                async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            var drainResult = await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

            completionException.Should().BeSameAs(source.Exception);
            drainResult.Status.Should().Be(PipelineDrainStatus.Faulted);
            run.State.Should().Be(PipelineRunState.Cancelled);
        }
        finally
        {
            source.ReleaseException();
            await run.DisposeAsync();
        }
    }

    private static PipelineRun<int> CreateRun(
        ControlledCancellationSource<int> source,
        int maxConcurrency)
    {
        return PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(value => value))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = maxConcurrency,
            })
            .Run();
    }

    private sealed class ControlledCancellationSource<T> : IPipelineSource<T>
    {
        private readonly TaskCompletionSource _waitForCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseException =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledCancellationSource(string exceptionMessage)
        {
            Exception = new OperationCanceledException(exceptionMessage);
        }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCanceledException Exception { get; }

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadStarted.TrySetResult();

            try
            {
                await _waitForCancellation.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await _releaseException.Task.ConfigureAwait(false);
                throw Exception;
            }

            yield break;
        }

        public void ReleaseException() => _releaseException.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
