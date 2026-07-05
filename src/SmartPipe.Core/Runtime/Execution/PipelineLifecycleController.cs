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

    public void MarkTerminal(PipelineRunState state)
    {
        EnsureTerminalState(state);
        Volatile.Write(ref _state, (int)state);
    }

    public void MarkCancelled()
    {
        MarkTerminalUnlessTerminal(PipelineRunState.Cancelled);
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
        MarkTerminalUnlessTerminal(PipelineRunState.Aborted);
    }

    public void MarkFaulted()
    {
        MarkTerminalUnlessTerminal(PipelineRunState.Faulted);
    }

    private void MarkTerminalUnlessTerminal(PipelineRunState state)
    {
        EnsureTerminalState(state);
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

            var previous = Interlocked.CompareExchange(ref _state, (int)state, current);
            if (previous == current)
                return;
        }
    }

    private static void EnsureTerminalState(PipelineRunState state)
    {
        if (state is not (
            PipelineRunState.Completed
            or PipelineRunState.Cancelled
            or PipelineRunState.Aborted
            or PipelineRunState.Faulted))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }
}
