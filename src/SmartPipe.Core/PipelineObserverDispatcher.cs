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
        ObserverDispatchOptions options,
        IPipelineClock clock
    )
    {
        options.Validate();
        ArgumentNullException.ThrowIfNull(clock);
        return options.Mode == ObserverDispatchMode.Inline
            ? new InlinePipelineObserverDispatcher(observers, options, clock)
            : new BufferedPipelineObserverDispatcher(observers, options);
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
    private readonly Channel<PipelineEvent> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private Exception? _failure;
    private Exception? _pipelineFault;
    private int _completed;
    private int _disposed;

    public BufferedPipelineObserverDispatcher(
        IReadOnlyList<PipelineObserverRegistration> observers,
        ObserverDispatchOptions options
    )
    {
        _observers = ObserverRegistrationState.CreateActiveObservers(observers);
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

        if (_pipelineFault is not null)
            throw _pipelineFault;
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
                if (registration.IsRemoved)
                    continue;

                try
                {
                    await registration.Registration.Observer.OnEventAsync(pipelineEvent, _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _failure ??= ex;
                    if (ObserverRegistrationState.ShouldFaultPipeline(_options.FailureMode, registration.Registration))
                    {
                        _pipelineFault ??= ex;
                        _events.Writer.TryComplete(ex);
                        return;
                    }

                    if (registration.Registration.FailurePolicy == ObserverFailurePolicy.RemoveObserver)
                        registration.Remove();
                }
            }
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
        return failureMode == ObserverFailureMode.FaultPipeline
            || registration.FailurePolicy == ObserverFailurePolicy.FaultPipeline
            || registration.Reliability == ObserverReliability.Critical;
    }
}
