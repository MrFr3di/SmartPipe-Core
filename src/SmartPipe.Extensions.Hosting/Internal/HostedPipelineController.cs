using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting;

internal sealed class HostedPipelineController
{
    private readonly ILogger _logger;

    internal HostedPipelineController(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    internal async Task<IHostedPipelineRun> StartAsync(
        IHostedPipelineRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        try
        {
            var run = await registration.StartAsync(cancellationToken).ConfigureAwait(false);
            LogOperation(LogLevel.Information, "Start", registration.Descriptor, run);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogOperation(LogLevel.Information, "Start", registration.Descriptor, null);
            throw;
        }
        catch (Exception error)
        {
            LogOperation(LogLevel.Error, "Start", registration.Descriptor, null, error);
            throw;
        }
    }

    internal async Task RollbackAsync(
        IHostedPipelineRun run,
        HostedPipelineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(descriptor);
        var errors = new HostedExceptionCollector();

        LogOperation(LogLevel.Warning, "Abort", descriptor, run);
        try
        {
            await run.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogOperation(LogLevel.Error, "Abort", descriptor, run, error);
            errors.Capture(error);
        }

        try
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogOperation(LogLevel.Error, "Dispose", descriptor, run, error);
            errors.Capture(error);
        }

        errors.ThrowIfAny();
    }

    /// <returns>
    /// <see langword="true"/> when the stopping token fired before the run finished disposing and the
    /// remaining disposal was left to complete in the background.
    /// </returns>
    internal async Task<bool> StopAsync(
        IHostedPipelineRun run,
        HostedPipelineDescriptor descriptor,
        CancellationToken hostStoppingToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(descriptor);
        var errors = new HostedExceptionCollector();
        var isTerminal = run.Completion.IsCompleted
            || run.State is PipelineRunState.Completed
            or PipelineRunState.Cancelled
            or PipelineRunState.Aborted
            or PipelineRunState.Faulted;
        var mustAbort = !isTerminal && hostStoppingToken.IsCancellationRequested;

        if (isTerminal)
        {
            LogOperation(LogLevel.Debug, "Drain", descriptor, run);
        }
        else if (!mustAbort)
        {
            try
            {
                var drain = await run.TryDrainAsync(
                    descriptor.DrainTimeout,
                    hostStoppingToken).ConfigureAwait(false);
                mustAbort = drain.Status is not (
                    PipelineDrainStatus.Completed or PipelineDrainStatus.AlreadyCompleted);
                LogOperation(
                    drain.Status switch
                    {
                        PipelineDrainStatus.Completed => LogLevel.Information,
                        PipelineDrainStatus.AlreadyCompleted => LogLevel.Debug,
                        PipelineDrainStatus.TimedOutStillRunning => LogLevel.Warning,
                        PipelineDrainStatus.CancelledByCaller => LogLevel.Warning,
                        PipelineDrainStatus.Faulted => LogLevel.Error,
                        _ => LogLevel.Error,
                    },
                    "Drain",
                    descriptor,
                    run,
                    drain.Exception);
                if (drain.Status == PipelineDrainStatus.Faulted)
                {
                    errors.Capture(drain.Exception ?? new InvalidOperationException(
                        $"Hosted pipeline '{descriptor.Key.Value}' faulted while draining."));
                }
            }
            catch (ObjectDisposedException)
            {
                LogOperation(LogLevel.Debug, "Drain", descriptor, run);
                mustAbort = false;
            }
            catch (Exception error)
            {
                LogOperation(LogLevel.Error, "Drain", descriptor, run, error);
                errors.Capture(error);
                mustAbort = true;
            }
        }

        if (mustAbort)
        {
            LogOperation(LogLevel.Warning, "Abort", descriptor, run);
            try
            {
                await run.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                LogOperation(LogLevel.Debug, "Abort", descriptor, run);
            }
            catch (Exception error)
            {
                LogOperation(LogLevel.Error, "Abort", descriptor, run, error);
                errors.Capture(error);
            }
        }

        // A stage that ignores cancellation can keep the run's disposal pending indefinitely; the host
        // stopping token bounds how long shutdown waits for it, and disposal continues in the background.
        Task? dispose = null;
        var disposeAbandoned = false;
        try
        {
            dispose = run.DisposeAsync().AsTask();
            await dispose.WaitAsync(hostStoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            dispose is { IsCompleted: false } && hostStoppingToken.IsCancellationRequested)
        {
            LogOperation(LogLevel.Warning, "DisposeAbandoned", descriptor, run);
            ObserveAbandonedDispose(dispose, descriptor, run);
            disposeAbandoned = true;
        }
        catch (Exception error)
        {
            LogOperation(LogLevel.Error, "Dispose", descriptor, run, error);
            errors.Capture(error);
        }

        if (!errors.HasErrors && !disposeAbandoned)
            LogOperation(LogLevel.Information, "Stop", descriptor, run);

        errors.ThrowIfAny();
        return disposeAbandoned;
    }

    private void ObserveAbandonedDispose(
        Task dispose,
        HostedPipelineDescriptor descriptor,
        IHostedPipelineRun run) =>
        _ = dispose.ContinueWith(
            task => LogOperation(LogLevel.Error, "Dispose", descriptor, run, task.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void LogOperation(
        LogLevel level,
        string operation,
        HostedPipelineDescriptor descriptor,
        IHostedPipelineRun? run,
        Exception? error = null)
    {
        if (run is null)
        {
            _logger.Log(
                level,
                error,
                "Hosted pipeline operation {Operation} for {PipelineKey}, order {Order}, failure behavior {FailureBehavior}, drain timeout {DrainTimeout}.",
                operation,
                descriptor.Key.Value,
                descriptor.Order,
                descriptor.FailureBehavior,
                descriptor.DrainTimeout);
            return;
        }

        _logger.Log(
            level,
            error,
            "Hosted pipeline operation {Operation} for {PipelineKey} run {RunId}, order {Order}, failure behavior {FailureBehavior}, drain timeout {DrainTimeout}, state {PipelineState}.",
            operation,
            descriptor.Key.Value,
            run.RunId,
            descriptor.Order,
            descriptor.FailureBehavior,
            descriptor.DrainTimeout,
            run.State);
    }
}
