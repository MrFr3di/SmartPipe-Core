using SmartPipe.Core;
using SmartPipe.Extensions.Hosting.Tests.Fakes;

namespace SmartPipe.Extensions.Hosting.Tests.Runtime;

[Trait("Category", "HostingLifecycle")]
public sealed class HostedPipelineControllerTests
{
    [Fact]
    public async Task StartAsync_ReturnsRunAndForwardsCancellationToken()
    {
        var run = new ControlledHostedRun("orders");
        var registration = new ControlledHostedRegistration(
            CreateDescriptor("orders"),
            _ => Task.FromResult<IHostedPipelineRun>(run));
        using var cancellation = new CancellationTokenSource();

        var started = await new HostedPipelineController().StartAsync(
            registration,
            cancellation.Token);

        Assert.Same(run, started);
        Assert.Equal(1, registration.StartCalls);
        Assert.Equal(cancellation.Token, registration.StartToken);
    }

    [Fact]
    public async Task RollbackAsync_AbortsBeforeDisposeWithNoneToken()
    {
        var run = new ControlledHostedRun("orders");

        await new HostedPipelineController().RollbackAsync(run, CreateDescriptor("orders"));

        Assert.Equal(["abort", "dispose"], run.Calls);
        Assert.Equal(CancellationToken.None, run.AbortToken);
    }

    [Fact]
    public async Task RollbackAsync_ContinuesDisposeAndOrdersErrors()
    {
        var abortError = new InvalidOperationException("abort");
        var disposeError = new InvalidOperationException("dispose");
        var run = new ControlledHostedRun("orders")
        {
            AbortError = abortError,
            DisposeError = disposeError,
        };

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => new HostedPipelineController().RollbackAsync(run, CreateDescriptor("orders")));

        Assert.Equal(["abort", "dispose"], run.Calls);
        Assert.Equal([abortError, disposeError], aggregate.InnerExceptions);
    }

    [Fact]
    public async Task StopAsync_CompletedDrainDisposesWithoutAbort()
    {
        var run = new ControlledHostedRun("orders");
        var descriptor = CreateDescriptor("orders", TimeSpan.FromSeconds(7));
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();

        await new HostedPipelineController(logger).StopAsync(
            run,
            descriptor,
            TestContext.Current.CancellationToken);

        Assert.Equal(["drain", "dispose"], run.Calls);
        Assert.Equal(TimeSpan.FromSeconds(7), run.DrainTimeout);
        Assert.Equal(TestContext.Current.CancellationToken, run.DrainToken);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && Equals(entry.Properties["PipelineKey"], "orders")
            && Equals(entry.Properties["RunId"], run.RunId)
            && Equals(entry.Properties["Order"], 0)
            && Equals(
                entry.Properties["FailureBehavior"],
                SmartPipeHostedPipelineFailureBehavior.StopApplication)
            && Equals(entry.Properties["DrainTimeout"], TimeSpan.FromSeconds(7))
            && Equals(entry.Properties["PipelineState"], PipelineRunState.Running)
            && Equals(entry.Properties["Operation"], "Drain"));
    }

    [Fact]
    public async Task StopAsync_AlreadyCompletedRunOnlyDisposes()
    {
        var run = new ControlledHostedRun("orders")
        {
            State = PipelineRunState.Completed,
            DrainError = new InvalidOperationException("must not drain"),
            AbortError = new InvalidOperationException("must not abort"),
        };

        await new HostedPipelineController().StopAsync(
            run,
            CreateDescriptor("orders"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["dispose"], run.Calls);
    }

    [Fact]
    public async Task StopAsync_CompletionWonStateRaceOnlyDisposes()
    {
        var run = new ControlledHostedRun("orders")
        {
            Completion = Task.CompletedTask,
            State = PipelineRunState.Running,
            DrainError = new InvalidOperationException("must not drain"),
            AbortError = new InvalidOperationException("must not abort"),
        };

        await new HostedPipelineController().StopAsync(
            run,
            CreateDescriptor("orders"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["dispose"], run.Calls);
    }

    [Theory]
    [InlineData(PipelineDrainStatus.TimedOutStillRunning)]
    [InlineData(PipelineDrainStatus.CancelledByCaller)]
    public async Task StopAsync_NonCompletedDrainAbortsThenDisposes(PipelineDrainStatus status)
    {
        var run = new ControlledHostedRun("orders")
        {
            DrainResult = new(status, PipelineRunState.Running, TimeSpan.Zero),
        };

        await new HostedPipelineController().StopAsync(
            run,
            CreateDescriptor("orders"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["drain", "abort", "dispose"], run.Calls);
        Assert.Equal(CancellationToken.None, run.AbortToken);
    }

    [Fact]
    public async Task StopAsync_FaultedDrainCleansUpThenRethrowsReportedError()
    {
        var drainError = new InvalidOperationException("faulted drain");
        var run = new ControlledHostedRun("orders")
        {
            DrainResult = new(
                PipelineDrainStatus.Faulted,
                PipelineRunState.Faulted,
                TimeSpan.Zero,
                drainError),
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostedPipelineController().StopAsync(
                run,
                CreateDescriptor("orders"),
                TestContext.Current.CancellationToken));

        Assert.Same(drainError, thrown);
        Assert.Equal(["drain", "abort", "dispose"], run.Calls);
    }

    [Fact]
    public async Task StopAsync_AlreadyCancelledSkipsDrainButStillCleansUp()
    {
        var run = new ControlledHostedRun("orders");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();

        await new HostedPipelineController(logger).StopAsync(
            run,
            CreateDescriptor("orders"),
            cancellation.Token);

        Assert.Equal(["abort", "dispose"], run.Calls);
        Assert.Equal(CancellationToken.None, run.AbortToken);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && Equals(entry.Properties["Operation"], "Abort"));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task StopAsync_DrainErrorStillAbortsAndDisposesThenRethrowsSameError()
    {
        var drainError = new InvalidOperationException("drain");
        var run = new ControlledHostedRun("orders") { DrainError = drainError };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostedPipelineController().StopAsync(
                run,
                CreateDescriptor("orders"),
                TestContext.Current.CancellationToken));

        Assert.Same(drainError, thrown);
        Assert.Equal(["drain", "abort", "dispose"], run.Calls);
    }

    [Fact]
    public async Task StopAsync_DisposedDrainRaceOnlyDisposes()
    {
        var run = new ControlledHostedRun("orders")
        {
            DrainError = new ObjectDisposedException("run"),
            AbortError = new InvalidOperationException("must not abort"),
        };

        await new HostedPipelineController().StopAsync(
            run,
            CreateDescriptor("orders"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["drain", "dispose"], run.Calls);
    }

    [Fact]
    public async Task StopAsync_DisposedAbortRaceStillDisposesWithoutError()
    {
        var run = new ControlledHostedRun("orders")
        {
            DrainResult = new(
                PipelineDrainStatus.TimedOutStillRunning,
                PipelineRunState.Running,
                TimeSpan.Zero),
            AbortError = new ObjectDisposedException("run"),
        };

        await new HostedPipelineController().StopAsync(
            run,
            CreateDescriptor("orders"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["drain", "abort", "dispose"], run.Calls);
    }

    [Fact]
    public async Task StopAsync_AggregatesDrainAbortDisposeErrorsInActionOrder()
    {
        var drainError = new InvalidOperationException("drain");
        var abortError = new InvalidOperationException("abort");
        var disposeError = new InvalidOperationException("dispose");
        var run = new ControlledHostedRun("orders")
        {
            DrainError = drainError,
            AbortError = abortError,
            DisposeError = disposeError,
        };

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            new HostedPipelineController().StopAsync(
                run,
                CreateDescriptor("orders"),
                TestContext.Current.CancellationToken));

        Assert.Equal([drainError, abortError, disposeError], aggregate.InnerExceptions);
        Assert.Equal(["drain", "abort", "dispose"], run.Calls);
    }

    private static HostedPipelineDescriptor CreateDescriptor(
        string key,
        TimeSpan? drainTimeout = null) =>
        new()
        {
            Key = new PipelineKey(key),
            InputType = typeof(int),
            OutputType = typeof(int),
            Order = 0,
            RegistrationOrder = 0,
            DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30),
            FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication,
            CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive,
        };
}
