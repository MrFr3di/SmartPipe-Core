using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using System.Runtime.ExceptionServices;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed class SmartPipeRunFactory<TInput, TOutput> : ISmartPipeRunFactory<TInput, TOutput>
{
    private readonly PipelineDefinition<TInput, TOutput> _definition;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ISmartPipeMutableRunRegistry _runRegistry;
    private readonly ISmartPipeMutableRunObservationStore _observationStore;

    internal SmartPipeRunFactory(
        PipelineDefinition<TInput, TOutput> definition,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ISmartPipeMutableRunRegistry runRegistry,
        ISmartPipeMutableRunObservationStore observationStore)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _runRegistry = runRegistry ?? throw new ArgumentNullException(nameof(runRegistry));
        _observationStore = observationStore ?? throw new ArgumentNullException(nameof(observationStore));
    }

    public async Task<PipelineRun<TOutput>> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runId = Guid.NewGuid();
        var startedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        AsyncServiceScope? scope = null;
        PipelineRun<TOutput>? inner = null;
        IDisposable? registration = null;
        try
        {
            scope = _scopeFactory.CreateAsyncScope();
            var context = new PipelineActivationContext(
                _definition.Key,
                runId,
                scope.Value.ServiceProvider,
                _timeProvider);
            inner = await _definition
                .StartAsync(context, cancellationToken)
                .ConfigureAwait(false);
            registration = _runRegistry.Register<TInput, TOutput>(inner, startedAtUtc);
            var lifetime = new ScopedPipelineRunLifetime<TInput, TOutput>(
                inner,
                registration,
                scope.Value,
                startedAtUtc,
                _timeProvider,
                _observationStore);
            return inner.WithLifetime(lifetime.Completion, lifetime.DisposeAsync);
        }
        catch (Exception error)
        {
            var subsequentErrors = new List<Exception>(4);
            try
            {
                registration?.Dispose();
            }
            catch (Exception cleanupError)
            {
                subsequentErrors.Add(cleanupError);
            }

            if (inner is not null)
            {
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    subsequentErrors.Add(cleanupError);
                }
            }

            if (scope is { } createdScope)
            {
                try
                {
                    await createdScope.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    subsequentErrors.Add(cleanupError);
                }
            }

            if (error is not OperationCanceledException cancellation
                || !IsCallerCancellation(cancellation, cancellationToken))
            {
                try
                {
                    _observationStore.RecordTerminal(new SmartPipeTerminalRunCandidate(
                        new SmartPipeRunIdentity { PipelineKey = _definition.Key, RunId = runId },
                        typeof(TInput),
                        typeof(TOutput),
                        SmartPipeRunObservationOutcome.ActivationFailed,
                        startedAtUtc,
                        _timeProvider.GetUtcNow().ToUniversalTime(),
                        SmartPipeMetricsSnapshot.Empty,
                        _definition.RuntimeOptions.InputCapacity,
                        TypedPipelineExecutor<TInput, TOutput>.GetEffectiveOutputCapacity(
                            _definition.RuntimeOptions)));
                }
                catch (Exception publicationError)
                {
                    subsequentErrors.Add(publicationError);
                }
            }

            if (subsequentErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            throw new AggregateException([error, .. subsequentErrors]);
        }
    }

    private static bool IsCallerCancellation(
        OperationCanceledException error,
        CancellationToken requestedToken) =>
        requestedToken.IsCancellationRequested
        && error.CancellationToken == requestedToken;
}

internal sealed class ScopedPipelineRunLifetime<TInput, TOutput>
{
    private readonly PipelineRun<TOutput> _inner;
    private readonly IDisposable _registration;
    private readonly AsyncServiceScope _scope;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly TimeProvider _timeProvider;
    private readonly ISmartPipeMutableRunObservationStore _observationStore;
    private readonly object _gate = new();
    private Task? _cleanupTask;

    internal ScopedPipelineRunLifetime(
        PipelineRun<TOutput> inner,
        IDisposable registration,
        AsyncServiceScope scope,
        DateTimeOffset startedAtUtc,
        TimeProvider timeProvider,
        ISmartPipeMutableRunObservationStore observationStore)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _scope = scope;
        _startedAtUtc = startedAtUtc;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _observationStore = observationStore ?? throw new ArgumentNullException(nameof(observationStore));
        Completion = CompleteNaturallyAsync();
    }

    internal Task Completion { get; }

    internal ValueTask DisposeAsync() => new(GetOrStartCleanup());

    private async Task CompleteNaturallyAsync()
    {
        Exception? completionError = null;
        try
        {
            await _inner.Completion.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            completionError = error;
        }

        Exception? cleanupError = null;
        try
        {
            await GetOrStartCleanup().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            cleanupError = error;
        }

        if (completionError is not null && cleanupError is not null)
        {
            throw cleanupError is AggregateException aggregate
                ? new AggregateException([completionError, .. aggregate.InnerExceptions])
                : new AggregateException(completionError, cleanupError);
        }

        if (completionError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(completionError).Throw();
        }

        if (cleanupError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private Task GetOrStartCleanup()
    {
        TaskCompletionSource? starter = null;
        Task task;
        lock (_gate)
        {
            if (_cleanupTask is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _cleanupTask = starter.Task;
            }

            task = _cleanupTask;
        }

        if (starter is not null)
        {
            _ = CompleteCleanupAsync(starter);
        }

        return task;
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>(4);
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            var state = _inner.State;
            var outcome = state switch
            {
                PipelineRunState.Completed => SmartPipeRunObservationOutcome.Completed,
                PipelineRunState.Cancelled => SmartPipeRunObservationOutcome.Cancelled,
                PipelineRunState.Aborted => SmartPipeRunObservationOutcome.Aborted,
                PipelineRunState.Faulted => SmartPipeRunObservationOutcome.Faulted,
                _ => throw new InvalidOperationException(
                    $"Run '{_inner.RunId}' reached cleanup with non-terminal state '{state}'."),
            };
            _observationStore.RecordTerminal(new SmartPipeTerminalRunCandidate(
                new SmartPipeRunIdentity
                {
                    PipelineKey = _inner.PipelineKey,
                    RunId = _inner.RunId,
                },
                typeof(TInput),
                typeof(TOutput),
                outcome,
                _startedAtUtc,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                _inner.Metrics,
                _inner.InputCapacity,
                _inner.OutputCapacity));
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            _registration.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (errors.Count == 0)
        {
            completion.SetResult();
        }
        else if (errors.Count == 1)
        {
            completion.SetException(errors[0]);
        }
        else
        {
            completion.SetException(new AggregateException(errors));
        }
    }
}
