#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal interface IPipelineObserverDispatcher : IAsyncDisposable
{
    ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct);

    ValueTask CompleteAsync(CancellationToken ct);
}

internal static class PipelineObserverDispatcher
{
    public static IPipelineObserverDispatcher Create(
        IReadOnlyList<PipelineObserverRegistration> observers,
        ObserverDispatchOptions options
    )
    {
        options.Validate();
        return options.Mode == ObserverDispatchMode.Inline
            ? new InlinePipelineObserverDispatcher(observers)
            : new BufferedPipelineObserverDispatcher(observers, options);
    }
}

internal sealed class InlinePipelineObserverDispatcher : IPipelineObserverDispatcher
{
    private readonly IReadOnlyList<PipelineObserverRegistration> _observers;

    public InlinePipelineObserverDispatcher(IReadOnlyList<PipelineObserverRegistration> observers)
    {
        _observers = observers;
    }

    public async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        foreach (var registration in _observers)
        {
            try
            {
                await registration.Observer.OnEventAsync(pipelineEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (registration.FailurePolicy != ObserverFailurePolicy.FaultPipeline
                    && registration.Reliability != ObserverReliability.Critical)
            {
                await EmitObserverFailureAsync(pipelineEvent, registration, ex, ct).ConfigureAwait(false);
            }
        }
    }

    public ValueTask CompleteAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask EmitObserverFailureAsync(
        PipelineEvent sourceEvent,
        PipelineObserverRegistration failedRegistration,
        Exception exception,
        CancellationToken ct
    )
    {
        var failureEvent = new ObserverFailedEvent(
            sourceEvent.PipelineId,
            sourceEvent.RunId,
            failedRegistration.Observer.GetType().Name,
            DateTimeOffset.UtcNow,
            exception
        );

        foreach (var registration in _observers)
        {
            if (ReferenceEquals(registration.Observer, failedRegistration.Observer))
                continue;

            try
            {
                await registration.Observer.OnEventAsync(failureEvent, ct).ConfigureAwait(false);
            }
            catch (Exception)
                when (registration.FailurePolicy != ObserverFailurePolicy.FaultPipeline
                    && registration.Reliability != ObserverReliability.Critical)
            {
                // Best-effort observer failure notifications must not recurse indefinitely.
            }
        }
    }
}

internal sealed class BufferedPipelineObserverDispatcher : IPipelineObserverDispatcher
{
    private readonly IReadOnlyList<PipelineObserverRegistration> _observers;
    private readonly ObserverDispatchOptions _options;
    private readonly Channel<PipelineEvent> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private Exception? _failure;
    private int _completed;
    private int _disposed;

    public BufferedPipelineObserverDispatcher(
        IReadOnlyList<PipelineObserverRegistration> observers,
        ObserverDispatchOptions options
    )
    {
        _observers = observers;
        _options = options;
        _events = Channel.CreateBounded<PipelineEvent>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = options.FullMode,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _worker = Task.Run(ProcessAsync, CancellationToken.None);
    }

    public async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        if (_options.Mode == ObserverDispatchMode.BufferedBestEffort)
        {
            if (_events.Writer.TryWrite(pipelineEvent))
                return;

            if (_options.FullMode == BoundedChannelFullMode.Wait)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await _events.Writer.WriteAsync(pipelineEvent, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) { }
            }
            return;
        }

        await _events.Writer.WriteAsync(pipelineEvent, ct).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _events.Writer.TryComplete();
        if (_options.FlushOnCompletion)
            await _worker.WaitAsync(ct).ConfigureAwait(false);

        if (_failure is not null && _options.FailureMode == ObserverFailureMode.FaultPipeline)
            throw _failure;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _events.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch
        {
            // Failure is surfaced through CompleteAsync when configured to fault.
        }
        _cts.Dispose();
    }

    private async Task ProcessAsync()
    {
        await foreach (var pipelineEvent in _events.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            foreach (var registration in _observers)
            {
                try
                {
                    await registration.Observer.OnEventAsync(pipelineEvent, _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _failure ??= ex;
                    if (
                        _options.FailureMode == ObserverFailureMode.FaultPipeline
                        || registration.FailurePolicy == ObserverFailurePolicy.FaultPipeline
                        || registration.Reliability == ObserverReliability.Critical
                    )
                    {
                        _events.Writer.TryComplete(ex);
                        return;
                    }
                }
            }
        }
    }
}
