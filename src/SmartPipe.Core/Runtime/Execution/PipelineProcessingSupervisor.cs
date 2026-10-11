#nullable enable

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace SmartPipe.Core;

/// <summary>Owns the ordinary worker group; cancellation never substitutes for joining.</summary>
internal sealed class PipelineProcessingSupervisor : IDisposable
{
    private readonly CancellationTokenSource _workerStop;
    private readonly Action _requestSourceStop;
    private readonly ConcurrentQueue<Exception> _stopErrors = new();
    private readonly object _failureGate = new();
    private Exception? _workerFailure;

    public PipelineProcessingSupervisor(CancellationToken processingToken, Action requestSourceStop)
    {
        _requestSourceStop = requestSourceStop ?? throw new ArgumentNullException(nameof(requestSourceStop));
        _workerStop = CancellationTokenSource.CreateLinkedTokenSource(processingToken);
    }

    public CancellationToken WorkerToken => _workerStop.Token;

    public bool HasWorkerFailure
    {
        get { lock (_failureGate) return _workerFailure is not null; }
    }

    public void ReportWorkerFailure(Exception error)
    {
        lock (_failureGate)
            _workerFailure ??= error;

        try { _requestSourceStop(); }
        catch (Exception ex) { _stopErrors.Enqueue(ex); }
        RequestStop();
    }

    public void RequestStop()
    {
        try { _workerStop.Cancel(); }
        catch (Exception ex) { _stopErrors.Enqueue(ex); }
    }

    public async ValueTask<ProcessingJoinResult> JoinAsync(
        IReadOnlyList<Task> workers, ExceptionDispatchInfo? producerError)
    {
        var primary = producerError;
        var allWorkers = Task.WhenAll(workers);
        try
        {
            await allWorkers.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_failureGate)
                primary ??= ExceptionDispatchInfo.Capture(_workerFailure ?? ex);
        }

        var errors = _stopErrors.ToList();
        if (allWorkers.Exception is { } workerErrors)
        {
            foreach (var error in workerErrors.InnerExceptions)
            {
                if (ReferenceEquals(error, primary?.SourceException))
                    continue;
                if (error is OperationCanceledException cancellation
                    && _workerStop.IsCancellationRequested
                    && cancellation.CancellationToken == WorkerToken)
                    continue;
                errors.Add(error);
            }
        }

        return new ProcessingJoinResult(primary, errors.ToArray());
    }

    public void Dispose() => _workerStop.Dispose();
}

internal readonly record struct ProcessingJoinResult(
    ExceptionDispatchInfo? PrimaryError, Exception[] SecondaryErrors);
