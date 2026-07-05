#nullable enable

using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests.Hosting;

public sealed class SmartPipeHostedServiceTests
{
    [Fact]
    public async Task StopAsync_AttemptsDrainBaseStopAndDisposeIndependently()
    {
        var drainException = new InvalidOperationException("drain failed");
        var baseStopException = new InvalidOperationException("base stop failed");
        var disposeException = new InvalidOperationException("dispose failed");
        var run = new ControlledRun
        {
            OnDrain = (_, _) => ValueTask.FromException(drainException),
            OnDispose = () => ValueTask.FromException(disposeException),
        };
        var service = CreateService(
            new ControlledFactory(run),
            new SmartPipeHostedServiceOptions
            {
                FailureBehavior = SmartPipeHostedFailureBehavior.Rethrow,
            });

        await service.StartAsync(CancellationToken.None);
        await run.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Completion.SetException(baseStopException);

        var act = async () => await service.StopAsync(CancellationToken.None);

        var aggregate = await act.Should().ThrowAsync<AggregateException>();
        aggregate.Which.InnerExceptions.Should().Equal(
            drainException,
            baseStopException,
            disposeException);
        run.DrainCalls.Should().Be(1);
        run.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task StopAsync_WhenHostTokenAlreadyCancelled_SkipsDrainAndStillDisposes()
    {
        var run = new ControlledRun();
        var service = CreateService(new ControlledFactory(run));
        using var cts = new CancellationTokenSource();

        await service.StartAsync(CancellationToken.None);
        await run.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Completion.SetResult();
        await cts.CancelAsync();

        await service.StopAsync(cts.Token);

        run.DrainCalls.Should().Be(0);
        run.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task StopAsync_WhenHostTokenIsActive_DrainTokenLinksHostCancellation()
    {
        var run = new ControlledRun();
        using var hostCts = new CancellationTokenSource();
        var drainObservedCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        run.OnDrain = async (timeout, ct) =>
        {
            timeout.Should().Be(TimeSpan.FromMilliseconds(123));
            ct.CanBeCanceled.Should().BeTrue();
            await hostCts.CancelAsync();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                drainObservedCancellation.TrySetResult();
                throw;
            }
        };
        var service = CreateService(
            new ControlledFactory(run),
            new SmartPipeHostedServiceOptions { DrainTimeout = TimeSpan.FromMilliseconds(123) });

        await service.StartAsync(CancellationToken.None);
        await run.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Completion.SetResult();

        await service.StopAsync(hostCts.Token);

        await drainObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.DrainCalls.Should().Be(1);
        run.DisposeCalls.Should().Be(1);
    }

    private static SmartPipeHostedService<int, int> CreateService(
        ISmartPipeFactory<int, int> factory,
        SmartPipeHostedServiceOptions? options = null,
        IHostApplicationLifetime? lifetime = null)
    {
        return new SmartPipeHostedService<int, int>(
            factory,
            NullLogger<SmartPipeHostedService<int, int>>.Instance,
            Options.Create(options ?? new SmartPipeHostedServiceOptions()),
            lifetime);
    }

    private sealed class ControlledFactory : ISmartPipeFactory<int, int>
    {
        private readonly ControlledRun _run;

        public ControlledFactory(ControlledRun run)
        {
            _run = run;
        }

        public PipelineRun<int> Start(CancellationToken ct = default) => _run.ToPipelineRun();

        public Task<PipelineRun<int>> StartAsync(CancellationToken ct = default) =>
            Task.FromResult(_run.ToPipelineRun());
    }

    private sealed class ControlledRun
    {
        private readonly Channel<PipelineOutput<int>> _outputs = Channel.CreateUnbounded<PipelineOutput<int>>();

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<TimeSpan, CancellationToken, ValueTask>? OnDrain { get; set; }

        public Func<ValueTask>? OnDispose { get; set; }

        public int DrainCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public PipelineRun<int> ToPipelineRun()
        {
            Started.TrySetResult();
            return new PipelineRun<int>(
                _outputs.Reader,
                Completion.Task,
                GetState,
                drain: DrainAsync,
                dispose: DisposeAsync);
        }

        private PipelineRunState GetState()
        {
            if (Completion.Task.IsFaulted)
                return PipelineRunState.Faulted;

            if (Completion.Task.IsCanceled)
                return PipelineRunState.Cancelled;

            return Completion.Task.IsCompleted
                ? PipelineRunState.Completed
                : PipelineRunState.Running;
        }

        private async ValueTask DrainAsync(TimeSpan timeout, CancellationToken ct)
        {
            DrainCalls++;
            if (OnDrain is not null)
                await OnDrain(timeout, ct).ConfigureAwait(false);
        }

        private async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (OnDispose is not null)
                await OnDispose().ConfigureAwait(false);
        }
    }
}
