using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private int _stopApplicationRequested;

    public SmartPipeHostedOrchestrator(
        IEnumerable<IHostedPipelineRegistration> registrations,
        IHostApplicationLifetime lifetime,
        ILogger<SmartPipeHostedOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _controller = new HostedPipelineController(logger);
        _registrations = registrations
            .OrderBy(static registration => registration.Descriptor.Order)
            .ThenBy(static registration => registration.Descriptor.RegistrationOrder)
            .ThenBy(static registration => registration.Descriptor.Key.Value, StringComparer.Ordinal)
            .ToArray();
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
            _startupTask = StartCoreAsync(cancellationToken);
            return _startupTask;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopTask is not null)
                return _stopTask;

            _state = HostedOrchestratorState.Stopping;
            _stopTask = StopCoreAsync(_startupTask, cancellationToken);
            return _stopTask;
        }
    }

    public override void Dispose() => base.Dispose();

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var registration in _registrations)
            {
                var run = await _controller.StartAsync(
                    registration,
                    cancellationToken).ConfigureAwait(false);
                lock (_gate)
                    _started.Add((registration.Descriptor, run));
            }

            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_state == HostedOrchestratorState.Starting)
                    _state = HostedOrchestratorState.Running;
            }
        }
        catch (Exception primary)
        {
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
                _state = HostedOrchestratorState.Faulted;
            }

            if (cleanupErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(primary).Throw();
                throw;
            }

            throw new AggregateException([primary, .. cleanupErrors]);
        }
    }

    private async Task StopCoreAsync(Task? startupTask, CancellationToken cancellationToken)
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
                monitorErrors.AddRange(aggregate.InnerExceptions);
            }
            catch (Exception error)
            {
                monitorErrors.Add(error);
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

        var errors = cleanupErrors.Concat(monitorErrors).ToArray();
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
        lock (_gate)
        {
            if (_state is HostedOrchestratorState.Stopping
                or HostedOrchestratorState.Stopped
                or HostedOrchestratorState.Faulted)
                return;

            LogFault(item, error);
            switch (item.Descriptor.FailureBehavior)
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
    }

    private void HandleCompletion(
        (HostedPipelineDescriptor Descriptor, IHostedPipelineRun Run) item)
    {
        lock (_gate)
        {
            if (_state is HostedOrchestratorState.Stopping
                or HostedOrchestratorState.Stopped
                or HostedOrchestratorState.Faulted)
                return;

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
            if (item.Descriptor.CompletionBehavior == SmartPipeHostedCompletionBehavior.StopApplication)
                RequestStopApplication();
        }
    }

    private void RequestStopApplication()
    {
        if (Interlocked.CompareExchange(ref _stopApplicationRequested, 1, 0) == 0)
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
