#nullable enable

using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace SmartPipe.Core;

internal interface IPipelineObserverDispatcher : IAsyncDisposable
{
    ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct);

    ValueTask FlushAsync(CancellationToken ct);

    ValueTask CompleteAsync(CancellationToken ct);
}

internal enum ObserverDispatchMessageKind
{
    Event,
    Flush,
}

internal readonly record struct ObserverDispatchMessage(
    ObserverDispatchMessageKind Kind,
    PipelineEvent? Event,
    TaskCompletionSource? Completion)
{
    public static ObserverDispatchMessage FromEvent(PipelineEvent pipelineEvent)
    {
        ArgumentNullException.ThrowIfNull(pipelineEvent);
        return new ObserverDispatchMessage(ObserverDispatchMessageKind.Event, pipelineEvent, null);
    }

    public static ObserverDispatchMessage Flush(TaskCompletionSource completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return new ObserverDispatchMessage(ObserverDispatchMessageKind.Flush, null, completion);
    }
}

internal static class PipelineObserverDispatcher
{
    public static IPipelineObserverDispatcher Create(
        IReadOnlyList<PipelineObserverRegistration> observers,
        ObserverDispatchOptions options,
        IPipelineClock clock,
        Action<PipelineEvent>? onObserverEventDropped = null
    )
    {
        options.Validate();
        ArgumentNullException.ThrowIfNull(clock);
        return options.Mode == ObserverDispatchMode.Inline
            ? new InlinePipelineObserverDispatcher(observers, options, clock)
            : new BufferedPipelineObserverDispatcher(observers, options, clock, onObserverEventDropped);
    }
}

internal sealed class InlinePipelineObserverDispatcher : IPipelineObserverDispatcher
{
    private readonly ActiveObserverRegistration[] _observers;
    private readonly ObserverDispatchOptions _options;
    private readonly IPipelineClock _clock;

    public InlinePipelineObserverDispatcher(
        IReadOnlyList<PipelineObserverRegistration> observers,
        ObserverDispatchOptions options,
        IPipelineClock clock
    )
    {
        _observers = ObserverRegistrationState.CreateActiveObservers(observers);
        _options = options;
        _clock = clock;
    }

    public async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        foreach (var registration in _observers)
        {
            if (registration.IsRemoved)
                continue;

            try
            {
                await registration.Registration.Observer.OnEventAsync(pipelineEvent, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
                    ExceptionDispatchInfo.Capture(
                        ObserverRegistrationState.NormalizeObserverFailure(ex)).Throw();

                if (registration.Registration.FailurePolicy == ObserverFailurePolicy.RemoveObserver)
                    registration.Remove();

                await EmitObserverFailureAsync(pipelineEvent, registration, ex, ct).ConfigureAwait(false);
            }
        }
    }

    public ValueTask CompleteAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask EmitObserverFailureAsync(
        PipelineEvent sourceEvent,
        ActiveObserverRegistration failedRegistration,
        Exception exception,
        CancellationToken ct
    )
    {
        var failureEvent = new ObserverFailedEvent(
            sourceEvent.PipelineId,
            sourceEvent.RunId,
            failedRegistration.Registration.Observer.GetType().Name,
            _clock.GetUtcNow(),
            exception
        );

        foreach (var registration in _observers)
        {
            if (registration.IsRemoved)
                continue;

            if (ReferenceEquals(registration.Registration.Observer, failedRegistration.Registration.Observer))
                continue;

            try
            {
                await registration.Registration.Observer.OnEventAsync(failureEvent, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
                    throw;

                // Best-effort observer failure notifications must not recurse indefinitely.
            }
        }
    }

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
}

internal sealed class BufferedPipelineObserverDispatcher : IPipelineObserverDispatcher
{
    private readonly ActiveObserverRegistration[] _observers;
    private readonly ObserverDispatchOptions _options;
    private readonly IPipelineClock _clock;
    private readonly Action<PipelineEvent>? _onObserverEventDropped;
    private readonly Channel<ObserverDispatchMessage> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private Exception? _pipelineFault;
    private int _completed;
    private int _disposed;
    private int _emittingDroppedEvent;

    public BufferedPipelineObserverDispatcher(
        IReadOnlyList<PipelineObserverRegistration> observers,
        ObserverDispatchOptions options,
        IPipelineClock clock,
        Action<PipelineEvent>? onObserverEventDropped
    )
    {
        _observers = ObserverRegistrationState.CreateActiveObservers(observers);
        _options = options;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _onObserverEventDropped = onObserverEventDropped;
        _events = PipelineChannelFactory.CreateObserverBuffer<ObserverDispatchMessage>(
            options.Capacity,
            options.FullMode,
            RecordDroppedMessage);
        _worker = Task.Run(ProcessAsync, CancellationToken.None);
    }

    public async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        ThrowPipelineFaultIfRecorded();

        if (_options.Mode == ObserverDispatchMode.BufferedBestEffort)
        {
            if (_events.Writer.TryWrite(ObserverDispatchMessage.FromEvent(pipelineEvent)))
                return;

            if (_options.FullMode == BoundedChannelFullMode.Wait)
            {
                using var timeoutCts = new CancellationTokenSource(_options.BestEffortWriteTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await _events.Writer.WriteAsync(
                            ObserverDispatchMessage.FromEvent(pipelineEvent),
                            linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    RecordDroppedEvent(pipelineEvent);
                }
            }
            else
            {
                RecordDroppedEvent(pipelineEvent);
            }
            return;
        }

        try
        {
            await _events.Writer.WriteAsync(
                    ObserverDispatchMessage.FromEvent(pipelineEvent),
                    ct)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ThrowPipelineFaultIfRecorded();
            throw;
        }
    }

    public async ValueTask CompleteAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _events.Writer.TryComplete();
        if (_options.FlushOnCompletion)
            await _worker.WaitAsync(ct).ConfigureAwait(false);

        ThrowPipelineFaultIfRecorded();
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            ThrowPipelineFaultIfRecorded();
            return;
        }

        ThrowPipelineFaultIfRecorded();

        if (_options.Mode == ObserverDispatchMode.BufferedBestEffort && !_options.FlushOnCompletion)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _events.Writer.WriteAsync(ObserverDispatchMessage.Flush(completion), ct)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ThrowPipelineFaultIfRecorded();
            throw;
        }

        var completed = await Task.WhenAny(completion.Task, _worker)
            .WaitAsync(ct)
            .ConfigureAwait(false);
        if (completed == completion.Task)
            await completion.Task.ConfigureAwait(false);
        else
            await _worker.ConfigureAwait(false);

        ThrowPipelineFaultIfRecorded();
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
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected during disposal; cancellation is the disposal signal.
        }
        catch (Exception) when (GetPipelineFault() is not null)
        {
            // Recorded observer failure is surfaced through EmitAsync/CompleteAsync.
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private Exception? GetPipelineFault() => Volatile.Read(ref _pipelineFault);

    private Exception RecordPipelineFault(Exception exception)
    {
        Interlocked.CompareExchange(ref _pipelineFault, exception, null);
        return Volatile.Read(ref _pipelineFault)!;
    }

    private void ThrowPipelineFaultIfRecorded()
    {
        var pipelineFault = GetPipelineFault();
        if (pipelineFault is not null)
            ExceptionDispatchInfo.Capture(pipelineFault).Throw();
    }

    private async Task ProcessAsync()
    {
        await foreach (var message in _events.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            if (message.Kind == ObserverDispatchMessageKind.Flush)
            {
                message.Completion!.TrySetResult();
                continue;
            }

            if (await DispatchEventAsync(message.Event!).ConfigureAwait(false))
                return;
        }
    }

    private async ValueTask<bool> DispatchEventAsync(PipelineEvent pipelineEvent)
    {
        foreach (var registration in _observers)
        {
            if (registration.IsRemoved)
                continue;

            if (await DispatchObserverAsync(pipelineEvent, registration).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private async ValueTask<bool> DispatchObserverAsync(
        PipelineEvent pipelineEvent,
        ActiveObserverRegistration registration)
    {
        try
        {
            await registration.Registration.Observer.OnEventAsync(pipelineEvent, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            return await HandleObserverFailureAsync(pipelineEvent, registration, ex)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async ValueTask<bool> HandleObserverFailureAsync(
        PipelineEvent sourceEvent,
        ActiveObserverRegistration registration,
        Exception exception)
    {
        if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
        {
            var pipelineFault = RecordPipelineFault(
                ObserverRegistrationState.NormalizeObserverFailure(exception));
            _events.Writer.TryComplete(pipelineFault);
            return true;
        }

        if (registration.Registration.FailurePolicy == ObserverFailurePolicy.RemoveObserver)
            registration.Remove();

        if (sourceEvent is not ObserverFailedEvent)
            await EmitObserverFailureAsync(sourceEvent, registration, exception).ConfigureAwait(false);

        return false;
    }

    private async ValueTask EmitObserverFailureAsync(
        PipelineEvent sourceEvent,
        ActiveObserverRegistration failedRegistration,
        Exception exception)
    {
        var failureEvent = new ObserverFailedEvent(
            sourceEvent.PipelineId,
            sourceEvent.RunId,
            failedRegistration.Registration.Observer.GetType().Name,
            _clock.GetUtcNow(),
            exception);

        foreach (var registration in _observers)
        {
            if (registration.IsRemoved)
                continue;

            if (ReferenceEquals(registration.Registration.Observer, failedRegistration.Registration.Observer))
                continue;

            try
            {
                await registration.Registration.Observer.OnEventAsync(failureEvent, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
                    return;
            }
        }
    }

    private void RecordDroppedMessage(ObserverDispatchMessage message)
    {
        if (message.Kind == ObserverDispatchMessageKind.Event && message.Event is not null)
            RecordDroppedEvent(message.Event);
    }

    private void RecordDroppedEvent(PipelineEvent droppedEvent)
    {
        _onObserverEventDropped?.Invoke(droppedEvent);

        if (!_options.EmitDroppedObserverEvents || droppedEvent is ObserverEventDroppedEvent)
            return;

        if (Interlocked.Exchange(ref _emittingDroppedEvent, 1) != 0)
            return;

        try
        {
            _events.Writer.TryWrite(ObserverDispatchMessage.FromEvent(
                new ObserverEventDroppedEvent(
                    droppedEvent.PipelineId,
                    droppedEvent.RunId,
                    _clock.GetUtcNow(),
                    droppedEvent.GetType().Name)));
        }
        finally
        {
            Volatile.Write(ref _emittingDroppedEvent, 0);
        }
    }

}

internal sealed class ActiveObserverRegistration(PipelineObserverRegistration registration)
{
    private int _isRemoved;

    public PipelineObserverRegistration Registration { get; } = registration;

    public bool IsRemoved => Volatile.Read(ref _isRemoved) != 0;

    public void Remove() => Interlocked.Exchange(ref _isRemoved, 1);
}

internal static class ObserverRegistrationState
{
    public static ActiveObserverRegistration[] CreateActiveObservers(
        IReadOnlyList<PipelineObserverRegistration> observers)
    {
        var active = new ActiveObserverRegistration[observers.Count];
        for (var i = 0; i < observers.Count; i++)
            active[i] = new ActiveObserverRegistration(observers[i]);
        return active;
    }

    public static bool ShouldFaultPipeline(
        ObserverFailureMode failureMode,
        PipelineObserverRegistration registration)
    {
        return failureMode switch
        {
            ObserverFailureMode.FaultPipeline => true,
            ObserverFailureMode.Ignore => false,
            ObserverFailureMode.UseRegistrationPolicy =>
                registration.FailurePolicy == ObserverFailurePolicy.FaultPipeline
                || registration.Reliability == ObserverReliability.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(failureMode), failureMode, null),
        };
    }

    public static Exception NormalizeObserverFailure(Exception exception)
    {
        return exception is OperationCanceledException
            ? new InvalidOperationException("Observer dispatch failed.", exception)
            : exception;
    }
}
