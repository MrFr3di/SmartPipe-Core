#nullable enable

namespace SmartPipe.Core;

internal sealed class PipelineLifecycleController
{
    private int _state = (int)PipelineRunState.NotStarted;

    public PipelineRunState State => (PipelineRunState)Volatile.Read(ref _state);

    /// <summary>
    /// Marks the pipeline as running.
    /// </summary>
    /// <remarks>
    /// The state changes from <see cref="PipelineRunState.NotStarted"/> only if it has not
    /// already transitioned to another state.
    /// </remarks>
    public void MarkRunning()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Running,
            (int)PipelineRunState.NotStarted);
    }

    /// <summary>
    /// Marks the pipeline run as completed.
    /// </summary>
    /// <remarks>
    /// Sets the state to <see cref="PipelineRunState.Completed"/> when the current state is
    /// <see cref="PipelineRunState.Running"/> or <see cref="PipelineRunState.Draining"/>.
    /// </remarks>
    public void MarkCompleted()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current is not ((int)PipelineRunState.Running or (int)PipelineRunState.Draining))
                return;

            var previous = Interlocked.CompareExchange(
                ref _state,
                (int)PipelineRunState.Completed,
                current);

            if (previous == current)
                return;
        }
    }

    /// <summary>
    /// Marks the pipeline run as draining when it is running.
    /// </summary>
    public void MarkDrainingIfRunning()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Draining,
            (int)PipelineRunState.Running);
    }

    /// <summary>
    /// Marks the pipeline run as completed when it is draining.
    /// </summary>
    public void MarkCompletedIfDraining()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Completed,
            (int)PipelineRunState.Draining);
    }

    /// <summary>
    /// Marks the pipeline run as cancelled.
    /// </summary>
    public void MarkCancelled()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Cancelled);
    }

    /// <summary>
    /// Marks the pipeline run as cancelled unless it has already reached a terminal state.
    /// </summary>
    public void MarkCancelledUnlessAborted()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current is (int)PipelineRunState.Completed
                or (int)PipelineRunState.Cancelled
                or (int)PipelineRunState.Aborted
                or (int)PipelineRunState.Faulted)
            {
                return;
            }

            var previous = Interlocked.CompareExchange(
                ref _state,
                (int)PipelineRunState.Cancelled,
                current);

            if (previous == current)
                return;
        }
    }

    /// <summary>
    /// Marks the pipeline run as aborted.
    /// </summary>
    public void MarkAborted()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Aborted);
    }

    public void MarkFaulted()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Faulted);
    }
}
