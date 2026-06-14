#nullable enable

namespace SmartPipe.Core;

internal sealed class PipelineLifecycleController
{
    private int _state = (int)PipelineRunState.NotStarted;

    public PipelineRunState State => (PipelineRunState)Volatile.Read(ref _state);

    public void MarkRunning()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Running);
    }

    public void MarkCompleted()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Completed);
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
        if (State == PipelineRunState.Draining)
            Volatile.Write(ref _state, (int)PipelineRunState.Completed);
    }

    public void MarkCancelled()
    {
        Volatile.Write(ref _state, (int)PipelineRunState.Cancelled);
    }

    public void MarkCancelledUnlessAborted()
    {
        if (State != PipelineRunState.Aborted)
            Volatile.Write(ref _state, (int)PipelineRunState.Cancelled);
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
