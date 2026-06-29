#nullable enable

using System.Runtime.ExceptionServices;
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
            catch (Exception ex)
            {
                if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
                    throw;

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
            catch (Exception)
            {
                if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
                    throw;

                // Best-effort observer failure notifications must not recurse indefinitely.
            }
        }
    }
}

internal sealed class BufferedPipelineObserverDispatcher : IPipelineObserverDispatcher
{
    private readonly ActiveObserverRegistration[] _observers;
    private readonly ObserverDispatchOptions _options;
    private readonly IPipelineClock _clock;
    private readonly Action<PipelineEvent>? _onObserverEventDropped;
    private readonly Channel<PipelineEvent> _events;
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
        _events = PipelineChannelFactory.CreateObserverBuffer(
            options.Capacity,
            options.FullMode,
            RecordDroppedEvent);
        _worker = Task.Run(ProcessAsync, CancellationToken.None);
    }

    /// <summary>
    /// Emits an event to registered observers.
    /// </summary>
    /// <param name="pipelineEvent">The event to deliver.</param>
    /// <param name="ct">A token that cancels the emit operation.</param>
    /// <returns>A task that completes when the event has been accepted for dispatch.</returns>
    public async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        ThrowPipelineFaultIfRecorded();

        if (_options.Mode == ObserverDispatchMode.BufferedBestEffort)
        {
            if (_events.Writer.TryWrite(pipelineEvent))
                return;

            if (_options.FullMode == BoundedChannelFullMode.Wait)
            {
                using var timeoutCts = new CancellationTokenSource(_options.BestEffortWriteTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await _events.Writer.WriteAsync(pipelineEvent, linked.Token).ConfigureAwait(false);
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
            await _events.Writer.WriteAsync(pipelineEvent, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ThrowPipelineFaultIfRecorded();
            throw;
        }
    }

    /// <summary>
    /// Completes event dispatch and optionally waits for buffered work to finish.
    /// </summary>
    /// <param name="ct">A token that cancels waiting for buffered work to finish.</param>
    public async ValueTask CompleteAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _events.Writer.TryComplete();
        if (_options.FlushOnCompletion)
            await _worker.WaitAsync(ct).ConfigureAwait(false);

        ThrowPipelineFaultIfRecorded();
    }

    /// <summary>
    /// Stops the dispatcher and releases its internal resources.
    /// </summary>
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
        _cts.Dispose();
    }

    /// <summary>
/// Gets the recorded pipeline fault.
/// </summary>
/// <returns>The first exception recorded as a pipeline fault, or null if no fault has been recorded.</returns>
private Exception? GetPipelineFault() => Volatile.Read(ref _pipelineFault);

    /// <summary>
    /// Records the first pipeline fault exception.
    /// </summary>
    /// <param name="exception">The exception to store if no fault has been recorded yet.</param>
    /// <returns>The recorded pipeline fault exception.</returns>
    private Exception RecordPipelineFault(Exception exception)
    {
        Interlocked.CompareExchange(ref _pipelineFault, exception, null);
        return Volatile.Read(ref _pipelineFault)!;
    }

    /// <summary>
    /// Rethrows the recorded pipeline fault.
    /// </summary>
    /// <exception cref="Exception">The recorded pipeline fault, when one has been captured.</exception>
    private void ThrowPipelineFaultIfRecorded()
    {
        var pipelineFault = GetPipelineFault();
        if (pipelineFault is not null)
            ExceptionDispatchInfo.Capture(pipelineFault).Throw();
    }

    /// <summary>
    /// Processes buffered pipeline events on the worker task.
    /// </summary>
    private async Task ProcessAsync()
    {
        await foreach (var pipelineEvent in _events.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            if (await DispatchEventAsync(pipelineEvent).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>
    /// Dispatches a pipeline event to each active observer.
    /// </summary>
    /// <param name="pipelineEvent">The event to deliver.</param>
    /// <returns><c>true</c> if dispatch should stop because an observer faulted the pipeline, <c>false</c> otherwise.</returns>
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

    /// <summary>
    /// Dispatches an event to a single observer.
    /// </summary>
    /// <param name="pipelineEvent">The event to deliver.</param>
    /// <param name="registration">The observer registration to invoke.</param>
    /// <returns><c>true</c> if observer failure should stop buffered processing, <c>false</c> otherwise.</returns>
    private async ValueTask<bool> DispatchObserverAsync(
        PipelineEvent pipelineEvent,
        ActiveObserverRegistration registration)
    {
        try
        {
            await registration.Registration.Observer.OnEventAsync(pipelineEvent, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleObserverFailure(registration, ex);
        }

        return false;
    }

    /// <summary>
    /// Handles a failure reported by an observer.
    /// </summary>
    /// <param name="registration">The observer registration that failed.</param>
    /// <param name="exception">The exception raised by the observer.</param>
    /// <returns><c>true</c> if the failure should stop buffered processing, <c>false</c> otherwise.</returns>
    private bool HandleObserverFailure(ActiveObserverRegistration registration, Exception exception)
    {
        if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
        {
            var pipelineFault = RecordPipelineFault(exception);
            _events.Writer.TryComplete(pipelineFault);
            return true;
        }

        if (registration.Registration.FailurePolicy == ObserverFailurePolicy.RemoveObserver)
            registration.Remove();

        return false;
    }

    /// <summary>
    /// Records a dropped observer event and optionally emits a dropped-event notification.
    /// </summary>
    /// <param name="droppedEvent">The event that was dropped.</param>
    private void RecordDroppedEvent(PipelineEvent droppedEvent)
    {
        _onObserverEventDropped?.Invoke(droppedEvent);

        if (!_options.EmitDroppedObserverEvents || droppedEvent is ObserverEventDroppedEvent)
            return;

        if (Interlocked.Exchange(ref _emittingDroppedEvent, 1) != 0)
            return;

        try
        {
            _events.Writer.TryWrite(new ObserverEventDroppedEvent(
                droppedEvent.PipelineId,
                droppedEvent.RunId,
                _clock.GetUtcNow(),
                droppedEvent.GetType().Name));
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
}
