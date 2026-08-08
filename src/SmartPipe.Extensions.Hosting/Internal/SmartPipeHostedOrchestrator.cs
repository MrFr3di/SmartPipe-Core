using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartPipe.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;

namespace SmartPipe.Extensions.Hosting;

internal sealed class SmartPipeHostedOrchestrator : BackgroundService
{
    private readonly object _gate = new();
    private readonly IHostedPipelineRegistration[] _registrations;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SmartPipeHostedOrchestrator> _logger;
    private readonly HostedPipelineController _controller;
    private readonly List<(HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run)> _started = [];
    private HostedOrchestratorState _state = HostedOrchestratorState.NotStarted;
    private Task? _startupTask;
    private Task? _stopTask;
    private CancellationTokenSource? _startupStopSource;
    private StartupFailureKind _startupFailureKind;
    private int _stopApplicationRequested;

    public SmartPipeHostedOrchestrator(
        IEnumerable<IHostedPipelineRegistration> registrations,
        ISmartPipeRegistry registry,
        IHostApplicationLifetime lifetime,
        ILogger<SmartPipeHostedOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(registry);
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _controller = new HostedPipelineController(logger);
        _registrations = registrations
            .Select(registration => Materialize(registration, registry))
            .OrderBy(static item => item.Registration.Descriptor.Order)
            .ThenBy(static item => item.Canonical.RegistrationOrder)
            .ThenBy(static item => item.Registration.Descriptor.Key.Value, StringComparer.Ordinal)
            .Select(static item => item.Registration)
            .ToArray();
    }

    private static (
        IHostedPipelineRegistration Registration,
        SmartPipeRegistrationDescriptor Canonical) Materialize(
            IHostedPipelineRegistration registration,
            ISmartPipeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var hosted = registration.Descriptor;
        if (!registry.TryGetRegistration(hosted.Key, out var canonical))
        {
            throw new InvalidOperationException(
                $"Hosted pipeline '{hosted.Key.Value}' has no canonical DI registration.");
        }

        if (canonical.Key != hosted.Key
            || canonical.InputType != hosted.InputType
            || canonical.OutputType != hosted.OutputType)
        {
            throw new InvalidOperationException(
                $"Hosted pipeline '{hosted.Key.Value}' metadata does not match its canonical DI registration.");
        }

        return (registration, canonical);
    }

    internal HostedOrchestratorState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource launch;
        Task startupTask;
        lock (_gate)
        {
            if (_startupTask is not null)
            {
                if (_state is HostedOrchestratorState.Starting or HostedOrchestratorState.Running)
                    return _startupTask;

                throw new InvalidOperationException("The SmartPipe hosted orchestrator cannot be restarted.");
            }

            if (_state != HostedOrchestratorState.NotStarted)
                throw new InvalidOperationException("The SmartPipe hosted orchestrator cannot be restarted.");

            _state = HostedOrchestratorState.Starting;
            var stopSource = new CancellationTokenSource();
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                stopSource.Token);
            launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _startupStopSource = stopSource;
            startupTask = StartCoordinatorAsync(
                launch.Task,
                cancellationToken,
                stopSource,
                linkedSource);
            _startupTask = startupTask;
        }

        launch.SetResult();
        return startupTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource launch;
        TaskCompletionSource<Exception?> cancellationCompleted;
        CancellationTokenSource? stopSource;
        Task stopTask;
        lock (_gate)
        {
            if (_stopTask is not null)
                return _stopTask;

            _state = HostedOrchestratorState.Stopping;
            stopSource = _startupStopSource;
            _startupStopSource = null;
            launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = StopCoordinatorAsync(
                launch.Task,
                cancellationCompleted.Task,
                stopSource,
                _startupTask,
                cancellationToken);
            _stopTask = stopTask;
        }

        Exception? cancellationError = null;
        try
        {
            stopSource?.Cancel();
        }
        catch (Exception error)
        {
            cancellationError = error;
        }

        cancellationCompleted.SetResult(cancellationError);
        launch.SetResult();
        return stopTask;
    }

    public override void Dispose() => base.Dispose();

    private async Task StartCoordinatorAsync(
        Task launch,
        CancellationToken callerToken,
        CancellationTokenSource stopSource,
        CancellationTokenSource linkedSource)
    {
        await launch.ConfigureAwait(false);
        try
        {
            await StartCoreAsync(
                callerToken,
                stopSource.Token,
                linkedSource.Token).ConfigureAwait(false);
        }
        finally
        {
            var ownsStopSource = false;
            lock (_gate)
            {
                if (ReferenceEquals(_startupStopSource, stopSource))
                {
                    _startupStopSource = null;
                    ownsStopSource = true;
                }
            }

            linkedSource.Dispose();
            if (ownsStopSource)
                stopSource.Dispose();
        }
    }

    private async Task StartCoreAsync(
        CancellationToken callerToken,
        CancellationToken stopToken,
        CancellationToken effectiveToken)
    {
        try
        {
            foreach (var registration in _registrations)
            {
                var run = await _controller.StartAsync(
                    registration,
                    effectiveToken).ConfigureAwait(false);
                var stopping = false;
                lock (_gate)
                {
                    _started.Add((registration.Descriptor, run));
                    stopping = _state == HostedOrchestratorState.Stopping;
                }

                ThrowIfStartupCannotContinue(
                    callerToken,
                    stopToken,
                    effectiveToken,
                    stopping);
            }

            ThrowIfStartupCannotContinue(
                callerToken,
                stopToken,
                effectiveToken,
                IsStopping());
            await base.StartAsync(effectiveToken).ConfigureAwait(false);
            ThrowIfStartupCannotContinue(
                callerToken,
                stopToken,
                effectiveToken,
                IsStopping());
            lock (_gate)
            {
                if (_state == HostedOrchestratorState.Starting)
                {
                    _state = HostedOrchestratorState.Running;
                    _startupFailureKind = StartupFailureKind.None;
                    return;
                }
            }

            throw new StopRequestedOperationCanceledException(stopToken);
        }
        catch (Exception primary)
        {
            var failureKind = ClassifyStartupFailure(primary, callerToken, stopToken);
            var cleanupErrors = new List<Exception>();
            (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run)[] started;
            lock (_gate)
                started = _started.ToArray();

            for (var index = started.Length - 1; index >= 0; index--)
            {
                try
                {
                    await _controller.RollbackAsync(
                        started[index].Run,
                        started[index].Descriptor).ConfigureAwait(false);
                }
                catch (AggregateException aggregate)
                {
                    cleanupErrors.AddRange(aggregate.InnerExceptions);
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
            }

            lock (_gate)
            {
                _started.Clear();
                _startupFailureKind = failureKind;
                if (failureKind != StartupFailureKind.StopCancellation
                    || cleanupErrors.Count > 0
                    || _state != HostedOrchestratorState.Stopping)
                {
                    _state = HostedOrchestratorState.Faulted;
                }
            }

            if (cleanupErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(primary).Throw();
                throw;
            }

            throw new AggregateException([primary, .. cleanupErrors]);
        }
    }

    private async Task StopCoordinatorAsync(
        Task launch,
        Task<Exception?> cancellationCompleted,
        CancellationTokenSource? stopSource,
        Task? startupTask,
        CancellationToken cancellationToken)
    {
        await launch.ConfigureAwait(false);
        var cancellationError = await cancellationCompleted.ConfigureAwait(false);
        try
        {
            await StopCoreAsync(
                startupTask,
                cancellationToken,
                cancellationError).ConfigureAwait(false);
        }
        finally
        {
            stopSource?.Dispose();
        }
    }

    private async Task StopCoreAsync(
        Task? startupTask,
        CancellationToken cancellationToken,
        Exception? cancellationError)
    {
        var cleanupErrors = new List<Exception>();
        var monitorErrors = new List<Exception>();

        if (startupTask is not null)
        {
            try
            {
                await startupTask.ConfigureAwait(false);
            }
            catch (AggregateException aggregate)
            {
                CaptureStartupErrors(aggregate, monitorErrors);
            }
            catch (Exception error)
            {
                CaptureStartupErrors(error, monitorErrors);
            }
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            monitorErrors.Add(error);
        }

        if (cancellationToken.IsCancellationRequested
            && !monitorErrors.Any(static error => error is OperationCanceledException))
        {
            monitorErrors.Add(new OperationCanceledException(cancellationToken));
        }

        (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run)[] started;
        lock (_gate)
            started = _started.ToArray();

        for (var index = started.Length - 1; index >= 0; index--)
        {
            try
            {
                await _controller.StopAsync(
                    started[index].Run,
                    started[index].Descriptor,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AggregateException aggregate)
            {
                cleanupErrors.AddRange(aggregate.InnerExceptions);
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }
        }

        if (ExecuteTask?.IsFaulted == true)
        {
            foreach (var error in ExecuteTask.Exception!.InnerExceptions)
            {
                if (!monitorErrors.Any(existing => ReferenceEquals(existing, error)))
                    monitorErrors.Add(error);
            }
        }

        var errors = cleanupErrors
            .Concat(monitorErrors)
            .Concat(Flatten(cancellationError))
            .ToArray();
        lock (_gate)
        {
            _started.Clear();
            _state = errors.Length == 0
                ? HostedOrchestratorState.Stopped
                : HostedOrchestratorState.Faulted;
        }

        if (errors.Length == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
            throw new InvalidOperationException("Unreachable.");
        }

        if (errors.Length > 1)
            throw new AggregateException(errors);
    }

    private bool IsStopping()
    {
        lock (_gate)
            return _state == HostedOrchestratorState.Stopping;
    }

    private static void ThrowIfStartupCannotContinue(
        CancellationToken callerToken,
        CancellationToken stopToken,
        CancellationToken effectiveToken,
        bool stopping)
    {
        if (callerToken.IsCancellationRequested)
            throw new OperationCanceledException(callerToken);

        if (stopToken.IsCancellationRequested || stopping)
            throw new StopRequestedOperationCanceledException(stopToken);

        effectiveToken.ThrowIfCancellationRequested();
    }

    private static StartupFailureKind ClassifyStartupFailure(
        Exception error,
        CancellationToken callerToken,
        CancellationToken stopToken)
    {
        if (error is not OperationCanceledException)
            return StartupFailureKind.Fault;

        if (callerToken.IsCancellationRequested)
            return StartupFailureKind.CallerCancellation;

        return error is StopRequestedOperationCanceledException
            || stopToken.IsCancellationRequested
                ? StartupFailureKind.StopCancellation
                : StartupFailureKind.Fault;
    }

    private void CaptureStartupErrors(Exception error, List<Exception> errors)
    {
        StartupFailureKind failureKind;
        lock (_gate)
            failureKind = _startupFailureKind;

        if (failureKind != StartupFailureKind.StopCancellation)
        {
            errors.AddRange(Flatten(error));
            return;
        }

        var startupErrors = Flatten(error).ToArray();
        var first = startupErrors.Length > 0 ? startupErrors[0] : null;
        if (first is not OperationCanceledException)
        {
            errors.AddRange(startupErrors);
            return;
        }

        errors.AddRange(startupErrors.Skip(1));
    }

    private static IEnumerable<Exception> Flatten(Exception? error)
    {
        if (error is null)
            yield break;

        if (error is not AggregateException aggregate)
        {
            yield return error;
            yield break;
        }

        foreach (var inner in aggregate.InnerExceptions)
        {
            foreach (var leaf in Flatten(inner))
                yield return leaf;
        }
    }

    private enum StartupFailureKind
    {
        None,
        StopCancellation,
        CallerCancellation,
        Fault,
    }

    private sealed class StopRequestedOperationCanceledException(CancellationToken token)
        : OperationCanceledException("Hosted pipeline startup was stopped.", token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run)[] pending;
        lock (_gate)
            pending = _started.ToArray();

        var remaining = pending.ToList();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stoppingToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            stopped);

        while (remaining.Count > 0)
        {
            var completed = remaining.Where(static item => item.Run.Completion.IsCompleted).ToArray();
            if (completed.Length == 0)
            {
                var observed = await Task.WhenAny(
                    remaining.Select(static item => item.Run.Completion).Append(stopped.Task))
                    .ConfigureAwait(false);
                if (ReferenceEquals(observed, stopped.Task))
                {
                    ObserveRemainingCompletions(remaining);
                    return;
                }

                completed = remaining.Where(static item => item.Run.Completion.IsCompleted).ToArray();
            }

            foreach (var item in completed)
            {
                remaining.Remove(item);
                try
                {
                    await item.Run.Completion.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (stoppingToken.IsCancellationRequested)
                        continue;

                    HandleFault(item, error, remaining);
                    continue;
                }

                if (stoppingToken.IsCancellationRequested)
                    continue;

                HandleCompletion(item);
            }
        }
    }

    private void HandleFault(
        (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run) item,
        Exception error,
        IEnumerable<(HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run)> remaining)
    {
        SmartPipeHostedPipelineFailureBehavior behavior;
        lock (_gate)
        {
            if (_state is HostedOrchestratorState.Stopping
                or HostedOrchestratorState.Stopped
                or HostedOrchestratorState.Faulted)
                return;

            behavior = item.Descriptor.FailureBehavior;
        }

        LogFault(item, error);
        switch (behavior)
        {
            case SmartPipeHostedPipelineFailureBehavior.StopApplication:
                RequestStopApplication();
                break;
            case SmartPipeHostedPipelineFailureBehavior.Rethrow:
                ObserveRemainingCompletions(remaining);
                ExceptionDispatchInfo.Capture(error).Throw();
                break;
            case SmartPipeHostedPipelineFailureBehavior.MarkUnhealthyAndKeepHostAlive:
            case SmartPipeHostedPipelineFailureBehavior.Ignore:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(item.Descriptor.FailureBehavior),
                    item.Descriptor.FailureBehavior,
                    "Hosted pipeline failure behavior is invalid.");
        }
    }

    private void HandleCompletion(
        (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run) item)
    {
        var shouldStopApplication = false;
        lock (_gate)
        {
            if (_state is HostedOrchestratorState.Stopping
                or HostedOrchestratorState.Stopped
                or HostedOrchestratorState.Faulted)
                return;

            shouldStopApplication = item.Descriptor.CompletionBehavior
                == SmartPipeHostedCompletionBehavior.StopApplication;
        }

        _logger.LogInformation(
            "Hosted pipeline operation {Operation} for {PipelineKey} run {RunId}, order {Order}, failure behavior {FailureBehavior}, completion behavior {CompletionBehavior}, drain timeout {DrainTimeout}, state {PipelineState}.",
            "Monitor",
            item.Descriptor.Key.Value,
            item.Run.RunId,
            item.Descriptor.Order,
            item.Descriptor.FailureBehavior,
            item.Descriptor.CompletionBehavior,
            item.Descriptor.DrainTimeout,
            item.Run.State);
        if (shouldStopApplication)
            RequestStopApplication();
    }

    private void RequestStopApplication()
    {
        lock (_gate)
        {
            if (_state is HostedOrchestratorState.Stopping
                or HostedOrchestratorState.Stopped
                or HostedOrchestratorState.Faulted
                || Interlocked.CompareExchange(ref _stopApplicationRequested, 1, 0) != 0)
            {
                return;
            }
        }

        _lifetime.StopApplication();
    }

    private void LogFault(
        (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run) item,
        Exception error)
    {
        if (item.Descriptor.FailureBehavior == SmartPipeHostedPipelineFailureBehavior.Ignore)
        {
            _logger.LogWarning(
                error,
                "Hosted pipeline operation {Operation} for {PipelineKey} run {RunId}, order {Order}, failure behavior {FailureBehavior}, drain timeout {DrainTimeout}, state {PipelineState}.",
                "Monitor",
                item.Descriptor.Key.Value,
                item.Run.RunId,
                item.Descriptor.Order,
                item.Descriptor.FailureBehavior,
                item.Descriptor.DrainTimeout,
                item.Run.State);
            return;
        }

        _logger.LogError(
            error,
            "Hosted pipeline operation {Operation} for {PipelineKey} run {RunId}, order {Order}, failure behavior {FailureBehavior}, drain timeout {DrainTimeout}, state {PipelineState}.",
            "Monitor",
            item.Descriptor.Key.Value,
            item.Run.RunId,
            item.Descriptor.Order,
            item.Descriptor.FailureBehavior,
            item.Descriptor.DrainTimeout,
            item.Run.State);
    }

    private static void ObserveRemainingCompletions(
        IEnumerable<(HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run)> remaining)
    {
        foreach (var item in remaining)
        {
            if (item.Run.Completion.IsFaulted)
            {
                _ = item.Run.Completion.Exception;
                continue;
            }

            _ = item.Run.Completion.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
