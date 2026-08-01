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

    internal SmartPipeRunFactory(
        PipelineDefinition<TInput, TOutput> definition,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ISmartPipeMutableRunRegistry runRegistry)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _runRegistry = runRegistry ?? throw new ArgumentNullException(nameof(runRegistry));
    }

    public async Task<PipelineRun<TOutput>> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runId = Guid.NewGuid();
        var startedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var scope = _scopeFactory.CreateAsyncScope();
        PipelineRun<TOutput>? inner = null;
        IDisposable? registration = null;
        try
        {
            var context = new PipelineActivationContext(
                _definition.Key,
                runId,
                scope.ServiceProvider,
                _timeProvider);
            inner = await _definition
                .StartAsync(context, cancellationToken)
                .ConfigureAwait(false);
            registration = _runRegistry.Register<TInput, TOutput>(inner, startedAtUtc);
            var lifetime = new ScopedPipelineRunLifetime<TInput, TOutput>(inner, registration, scope);
            return inner.WithLifetime(lifetime.Completion, lifetime.DisposeAsync);
        }
        catch (Exception error)
        {
            var cleanupErrors = new List<Exception>(3);
            try
            {
                registration?.Dispose();
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            if (inner is not null)
            {
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
            }

            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            if (cleanupErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            throw new AggregateException([error, .. cleanupErrors]);
        }
    }
}

internal sealed class ScopedPipelineRunLifetime<TInput, TOutput>
{
    private readonly PipelineRun<TOutput> _inner;
    private readonly IDisposable _registration;
    private readonly AsyncServiceScope _scope;
    private readonly object _gate = new();
    private Task? _cleanupTask;

    internal ScopedPipelineRunLifetime(
        PipelineRun<TOutput> inner,
        IDisposable registration,
        AsyncServiceScope scope)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _scope = scope;
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
        var errors = new List<Exception>(2);
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
