#nullable enable

namespace SmartPipe.Core;

internal sealed class PipelineLifecycleController
{
    private int _state = (int)PipelineRunState.NotStarted;

    public PipelineRunState State => (PipelineRunState)Volatile.Read(ref _state);

    public void MarkRunning()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Running,
            (int)PipelineRunState.NotStarted);
    }

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

    public void MarkDrainingIfRunning()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Draining,
            (int)PipelineRunState.Running);
    }

    public void MarkCompletedIfDraining()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)PipelineRunState.Completed,
            (int)PipelineRunState.Draining);
    }

    public void MarkCancelled()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Cancelled);
    }

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

    public void MarkAborted()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Aborted);
    }

    public void MarkFaulted()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Faulted);
    }
}
