#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
public sealed class ObserverDispatcherTests
{
    [Fact]
    public async Task BufferedObserver_UseRegistrationPolicy_FaultPipelineObserverFaultsRun()
    {
        var observer = new ThrowingObserver();

        var run = CreateObservedRun(
            observer,
            ObserverReliability.BestEffort,
            ObserverFailurePolicy.FaultPipeline,
            ObserverFailureMode.UseRegistrationPolicy);

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task BufferedObserver_UseRegistrationPolicy_CriticalObserverFaultsRun()
    {
        var observer = new ThrowingObserver();

        var run = CreateObservedRun(
            observer,
            ObserverReliability.Critical,
            ObserverFailurePolicy.Ignore,
            ObserverFailureMode.UseRegistrationPolicy);

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task BufferedObserver_UseRegistrationPolicy_RemoveObserverDisablesObserver()
    {
        var failingObserver = new ThrowingObserver();
        var recordingObserver = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1, 2]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                failingObserver,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.RemoveObserver)
            .WithObserver(recordingObserver)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                ObserverDispatch = BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            })
            .Run();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        failingObserver.Calls.Should().Be(1);
        recordingObserver.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task BufferedObserver_IgnoreMode_DoesNotFaultRun()
    {
        var observer = new ThrowingObserver();

        var run = CreateObservedRun(
            observer,
            ObserverReliability.Critical,
            ObserverFailurePolicy.FaultPipeline,
            ObserverFailureMode.Ignore);

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        observer.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InlineObserver_FaultPipelineObserverFaultsRun()
    {
        var observer = new ThrowingObserver();

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .Run();

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task InlineObserver_UserThrownOperationCanceledExceptionFaultsRun()
    {
        var observer = new ThrowingObserver(new OperationCanceledException("observer cancelled itself"));

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .Run();

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Observer dispatch failed.");
        exception.Which.InnerException.Should().BeOfType<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task InlineObserver_ShutdownCancellationRemainsCancellation()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(new OperationCanceledException("shutdown")),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            ObserverDispatchOptions.Inline,
            SystemPipelineClock.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ObserverDispatcher_CompleteAsync_PropagatesBufferedFault()
    {
        var expected = new InvalidOperationException("observer failure");

        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(expected),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var act = async () => await dispatcher.CompleteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Should().BeSameAs(expected);

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_CompleteAsync_UserThrownOperationCanceledExceptionFaults()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(new OperationCanceledException("observer cancelled itself")),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var act = async () => await dispatcher.CompleteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Observer dispatch failed.");
        exception.Which.InnerException.Should().BeOfType<OperationCanceledException>();

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_EmitAsyncAfterBufferedFault_ThrowsOriginalObserverException()
    {
        var expected = new InvalidOperationException("observer failure");
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(expected),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var exception = await WaitUntilEmitThrowsAsync<InvalidOperationException>(
            dispatcher,
            new PipelineCompletedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5));

        exception.Should().BeSameAs(expected);

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_EmitAsyncAfterNormalDispose_DoesNotThrow()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.DisposeAsync();

        var act = async () => await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_DisposeAsyncAfterRecordedBufferedFault_DoesNotThrow()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await WaitUntilEmitThrowsAsync<InvalidOperationException>(
            dispatcher,
            new PipelineCompletedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5));

        var act = async () => await dispatcher.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ObserverDispatcher_DisposeAsyncDuringBufferedObserverCancellation_DoesNotRecordFailure()
    {
        var observer = new CancellationObservingObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    observer,
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = async () => await dispatcher.DisposeAsync();

        await act.Should().NotThrowAsync();
        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task BufferedBestEffort_CompleteWithoutFlush_DoesNotWaitForBlockedObserver()
    {
        var observer = new CancellationObservingObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            BestEffortNoFlushOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.CompleteAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        observer.CancellationObserved.Task.IsCompleted.Should().BeFalse();

        await dispatcher.DisposeAsync();
        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task BufferedBestEffort_DropOldest_DiagnosticDoesNotDisplaceLatestEvent()
    {
        var first = new PipelineStartedEvent("pipeline", "first", DateTimeOffset.UtcNow);
        var second = new PipelineStartedEvent("pipeline", "second", DateTimeOffset.UtcNow);
        var third = new PipelineStartedEvent("pipeline", "third", DateTimeOffset.UtcNow);
        var observer = new GatedRecordingObserver(third);
        var dropped = new ConcurrentQueue<PipelineEvent>();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.BufferedBestEffort,
                Capacity = 1,
                FullMode = BoundedChannelFullMode.DropOldest,
                FailureMode = ObserverFailureMode.UseRegistrationPolicy,
                FlushOnCompletion = false,
                EmitDroppedObserverEvents = true,
            },
            SystemPipelineClock.Instance,
            dropped.Enqueue);

        await dispatcher.EmitAsync(first, CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.EmitAsync(second, CancellationToken.None);
        await dispatcher.EmitAsync(third, CancellationToken.None);

        observer.Release();
        await observer.ExpectedEventObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.DropDiagnosticObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        dropped.Should().ContainSingle().Which.Should().BeSameAs(second);
        observer.Events.Should().Contain(first);
        observer.Events.Should().Contain(third);
        observer.Events.Should().NotContain(second);
        observer.Events.OfType<ObserverEventDroppedEvent>().Should().ContainSingle();

        await dispatcher.CompleteAsync(CancellationToken.None);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task BufferedBestEffort_CompleteWithoutFlush_ThenDispose_CancelsAndAwaitsWorker()
    {
        var observer = new CancellationObservingObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            BestEffortNoFlushOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.CompleteAsync(CancellationToken.None);

        await dispatcher.DisposeAsync();

        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task BufferedBestEffort_ConcurrentDisposeAfterComplete_AwaitsSameTeardownTask()
    {
        var observer = new ReleasableCancellationObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            BestEffortNoFlushOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.CompleteAsync(CancellationToken.None);

        var first = dispatcher.DisposeAsync().AsTask();
        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = dispatcher.DisposeAsync().AsTask();

        second.IsCompleted.Should().BeFalse();

        observer.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await observer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task BufferedBestEffort_DisposeAfterComplete_DoesNotLoseRecordedObserverFault()
    {
        var observer = new ReleasableFaultingObserver();
        var expected = observer.Exception;
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    observer,
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BestEffortNoFlushOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.CompleteAsync(CancellationToken.None);

        observer.Release();
        var firstException = await WaitUntilEmitThrowsAsync<InvalidOperationException>(
            dispatcher,
            new PipelineCompletedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5));
        firstException.Should().BeSameAs(expected);

        await dispatcher.DisposeAsync();

        var act = async () => await dispatcher.EmitAsync(
            new PipelineCompletedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var secondException = await act.Should().ThrowAsync<InvalidOperationException>();
        secondException.Which.Should().BeSameAs(expected);
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task BufferedBestEffort_DisposeBeforeComplete_RemainsIdempotent()
    {
        var observer = new ReleasableCancellationObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            BestEffortNoFlushOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = dispatcher.DisposeAsync().AsTask();
        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = dispatcher.DisposeAsync().AsTask();
        observer.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.CompleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ObserverDispatcher_DisposeAsyncWithoutEvents_DoesNotThrow()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        var act = async () => await dispatcher.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ObserverDispatcher_CompleteAsync_ConcurrentCallersWaitForSameBufferedCompletion()
    {
        var observer = new ReleasableObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = dispatcher.CompleteAsync(CancellationToken.None).AsTask();
        var second = dispatcher.CompleteAsync(CancellationToken.None).AsTask();

        second.IsCompleted.Should().BeFalse();

        observer.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ObserverDispatcher_DisposeAsync_ConcurrentCallersWaitForSameBufferedTeardown()
    {
        var observer = new ReleasableCancellationObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = dispatcher.DisposeAsync().AsTask();
        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = dispatcher.DisposeAsync().AsTask();

        second.IsCompleted.Should().BeFalse();

        observer.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    public void BufferedReliable_DropFullMode_ShouldFailValidation(BoundedChannelFullMode fullMode)
    {
        var options = new ObserverDispatchOptions
        {
            Mode = ObserverDispatchMode.BufferedReliable,
            FullMode = fullMode,
            FlushOnCompletion = true,
        };

        var act = () => PipelineObserverDispatcher.Create([], options, SystemPipelineClock.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*BufferedReliable*Wait*");
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    public void BufferedBestEffort_DropFullModeWithFlush_ShouldFailValidation(
        BoundedChannelFullMode fullMode)
    {
        var options = new ObserverDispatchOptions
        {
            Mode = ObserverDispatchMode.BufferedBestEffort,
            FullMode = fullMode,
            FlushOnCompletion = true,
        };

        var act = () => PipelineObserverDispatcher.Create([], options, SystemPipelineClock.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*BufferedBestEffort*FlushOnCompletion*");
    }

    [Fact]
    public async Task BufferedObserverFailure_ShouldNotifyRemainingObservers()
    {
        var failingObserver = new ThrowingObserver();
        var recordingObserver = new RecordingObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    failingObserver,
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.RemoveObserver),
                new PipelineObserverRegistration(
                    recordingObserver,
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.Ignore),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.CompleteAsync(CancellationToken.None);
        await dispatcher.DisposeAsync();

        recordingObserver.Events.OfType<ObserverFailedEvent>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(ObserverDispatchMode.Inline, false)]
    [InlineData(ObserverDispatchMode.BufferedReliable, false)]
    [InlineData(ObserverDispatchMode.Inline, true)]
    [InlineData(ObserverDispatchMode.BufferedReliable, true)]
    public async Task FailureNotification_CriticalRecipientFailure_ShouldFaultPipeline(
        ObserverDispatchMode mode,
        bool throwOperationCanceledException)
    {
        var primaryObserver = new ThrowingObserver();
        Exception recipientException = throwOperationCanceledException
            ? new OperationCanceledException("recipient cancelled itself")
            : new InvalidOperationException("failure notification boom");
        var recipientObserver = new ThrowingOnEventTypeObserver(
            typeof(ObserverFailedEvent),
            recipientException);
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    primaryObserver,
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.Ignore),
                new PipelineObserverRegistration(
                    recipientObserver,
                    ObserverReliability.Critical,
                    ObserverFailurePolicy.Ignore),
            ],
            Options(mode, ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        try
        {
            var act = async () =>
            {
                await dispatcher.EmitAsync(
                    new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await dispatcher.CompleteAsync(CancellationToken.None);
            };

            if (throwOperationCanceledException)
            {
                var exception = await act.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("Observer dispatch failed.");
                exception.Which.InnerException.Should().BeSameAs(recipientException);
            }
            else
            {
                var exception = await act.Should().ThrowAsync<InvalidOperationException>();
                exception.Which.Should().BeSameAs(recipientException);
            }
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ObserverDispatchMode.Inline, ObserverFailurePolicy.Ignore, 2)]
    [InlineData(ObserverDispatchMode.BufferedReliable, ObserverFailurePolicy.Ignore, 2)]
    [InlineData(ObserverDispatchMode.Inline, ObserverFailurePolicy.RemoveObserver, 1)]
    [InlineData(ObserverDispatchMode.BufferedReliable, ObserverFailurePolicy.RemoveObserver, 1)]
    public async Task FailureNotification_NonFaultingRecipientPolicy_ShouldNotFaultPipeline(
        ObserverDispatchMode mode,
        ObserverFailurePolicy recipientPolicy,
        int expectedRecipientCalls)
    {
        var primaryObserver = new ThrowingObserver();
        var recipientObserver = new ThrowingOnEventTypeObserver(typeof(ObserverFailedEvent));
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    primaryObserver,
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.Ignore),
                new PipelineObserverRegistration(
                    recipientObserver,
                    ObserverReliability.BestEffort,
                    recipientPolicy),
            ],
            Options(mode, ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        try
        {
            await dispatcher.EmitAsync(
                new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
                CancellationToken.None);
            await dispatcher.EmitAsync(
                new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
                CancellationToken.None);
            await dispatcher.CompleteAsync(CancellationToken.None);

            recipientObserver.Calls.Should().Be(expectedRecipientCalls);
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task InputDroppedEvent_BestEffortEmissionFailure_RecordsObserverDropAndDoesNotFaultRun()
    {
        var observer = new ThrowingOnEventTypeObserver(typeof(InputDroppedEvent));
        var transformer = new BlockingTransformer<int>(expectedConcurrentCalls: 2);

        var run = PipelineBuilder
            .From(new EnumerableSource<int>(Enumerable.Range(0, 64).ToArray()))
            .Transform(transformer)
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                InputCapacity = 1,
                InputFullMode = BoundedChannelFullMode.DropWrite,
                ObserverDispatch = new ObserverDispatchOptions
                {
                    Mode = ObserverDispatchMode.Inline,
                    FailureMode = ObserverFailureMode.UseRegistrationPolicy,
                },
            })
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.EventObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.Release();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        run.Metrics.ItemsDropped.Should().BeGreaterThan(0);
        run.Metrics.ObserverEventsDropped.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OutputDroppedEvent_BestEffortEmissionFailure_RecordsObserverDropAndDoesNotFaultRun()
    {
        var observer = new ThrowingOnEventTypeObserver(typeof(OutputDroppedEvent));

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1, 2, 3]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputCapacity = 1,
                OutputFullMode = BoundedChannelFullMode.DropOldest,
                ObserverDispatch = new ObserverDispatchOptions
                {
                    Mode = ObserverDispatchMode.Inline,
                    FailureMode = ObserverFailureMode.UseRegistrationPolicy,
                },
            })
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        outputs.Should().ContainSingle();
        run.Metrics.OutputItemsDropped.Should().BeGreaterThan(0);
        run.Metrics.ObserverEventsDropped.Should().BeGreaterThan(0);
    }

    private static PipelineRun<int> CreateObservedRun(
        IPipelineObserver observer,
        ObserverReliability reliability,
        ObserverFailurePolicy failurePolicy,
        ObserverFailureMode failureMode)
    {
        return PipelineBuilder
            .From(new EnumerableSource<int>([1]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(observer, reliability, failurePolicy)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                ObserverDispatch = BufferedOptions(failureMode),
            })
            .Run();
    }

    private static ObserverDispatchOptions BufferedOptions(ObserverFailureMode failureMode)
    {
        return new ObserverDispatchOptions
        {
            Mode = ObserverDispatchMode.BufferedReliable,
            Capacity = 16,
            FullMode = BoundedChannelFullMode.Wait,
            FailureMode = failureMode,
            FlushOnCompletion = true,
        };
    }

    private static ObserverDispatchOptions BestEffortNoFlushOptions(ObserverFailureMode failureMode)
    {
        return new ObserverDispatchOptions
        {
            Mode = ObserverDispatchMode.BufferedBestEffort,
            Capacity = 16,
            FullMode = BoundedChannelFullMode.Wait,
            FailureMode = failureMode,
            FlushOnCompletion = false,
        };
    }

    private static ObserverDispatchOptions Options(
        ObserverDispatchMode mode,
        ObserverFailureMode failureMode)
    {
        return mode == ObserverDispatchMode.Inline
            ? new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.Inline,
                FailureMode = failureMode,
            }
            : BufferedOptions(failureMode);
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);

        return outputs;
    }

    private static async Task<TException> WaitUntilEmitThrowsAsync<TException>(
        IPipelineObserverDispatcher dispatcher,
        PipelineEvent pipelineEvent,
        TimeSpan timeout)
        where TException : Exception
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await dispatcher.EmitAsync(pipelineEvent, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (TException ex)
            {
                return ex;
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"Dispatcher did not throw {typeof(TException).Name} for {pipelineEvent.GetType().Name} within {timeout}.");
    }

    private sealed class EnumerableSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _payloads;

        public EnumerableSource(IReadOnlyList<T> payloads)
        {
            _payloads = payloads;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _payloads.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _payloads[i],
                    "observer-dispatcher-tests",
                    "observer-dispatcher-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PassThroughTransformer<T> : IPipelineTransformer<T, T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(StageResult<T>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly int _expectedConcurrentCalls;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;

        public BlockingTransformer(int expectedConcurrentCalls)
        {
            _expectedConcurrentCalls = expectedConcurrentCalls;
        }

        public TaskCompletionSource ExpectedConcurrentCallsEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            if (active >= _expectedConcurrentCalls)
                ExpectedConcurrentCallsEntered.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                return StageResult<T>.Success(envelope.Payload);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingObserver : IPipelineObserver
    {
        private readonly Exception _exception;
        private int _calls;

        public ThrowingObserver()
            : this(new InvalidOperationException("observer failure"))
        {
        }

        public ThrowingObserver(Exception exception)
        {
            _exception = exception;
        }

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw _exception;
        }
    }

    private sealed class ThrowingOnEventTypeObserver : IPipelineObserver
    {
        private readonly Type _eventType;
        private readonly Exception _exception;
        private int _calls;

        public ThrowingOnEventTypeObserver(Type eventType, Exception? exception = null)
        {
            _eventType = eventType;
            _exception = exception ?? new InvalidOperationException("observer failure");
        }

        public TaskCompletionSource EventObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (pipelineEvent.GetType() == _eventType)
            {
                Interlocked.Increment(ref _calls);
                EventObserved.TrySetResult();
                throw _exception;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationObservingObserver : IPipelineObserver
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
    }

    private sealed class ReleasableObserver : IPipelineObserver
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class ReleasableCancellationObserver : IPipelineObserver
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw;
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
    }

    private sealed class ReleasableFaultingObserver : IPipelineObserver
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InvalidOperationException Exception { get; } =
            new("observer failure");

        public void Release() => _release.TrySetResult();

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            throw Exception;
        }
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        private readonly ConcurrentQueue<PipelineEvent> _events = [];

        public IReadOnlyCollection<PipelineEvent> Events => _events;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _events.Enqueue(pipelineEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedRecordingObserver(PipelineEvent expectedEvent) : IPipelineObserver
    {
        private readonly ConcurrentQueue<PipelineEvent> _events = [];
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public IReadOnlyCollection<PipelineEvent> Events => _events;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExpectedEventObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DropDiagnosticObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _events.Enqueue(pipelineEvent);

            if (ReferenceEquals(pipelineEvent, expectedEvent))
                ExpectedEventObserved.TrySetResult();
            if (pipelineEvent is ObserverEventDroppedEvent)
                DropDiagnosticObserved.TrySetResult();

            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
