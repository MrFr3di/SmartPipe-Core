using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting.Tests.Fakes;

internal sealed class ControlledHostedRun : IHostedPipelineRun
{
    internal ControlledHostedRun(string key)
    {
        Key = new PipelineKey(key);
        RunId = Guid.NewGuid();
    }

    internal List<string> Calls { get; } = [];

    internal Action<string>? CallObserver { get; set; }

    internal PipelineDrainResult DrainResult { get; set; } = new(
        PipelineDrainStatus.Completed,
        PipelineRunState.Completed,
        TimeSpan.Zero);

    internal Exception? DrainError { get; set; }

    internal Exception? AbortError { get; set; }

    internal Exception? DisposeError { get; set; }

    internal CancellationToken DrainToken { get; private set; }

    internal CancellationToken AbortToken { get; private set; }

    internal TimeSpan DrainTimeout { get; private set; }

    public PipelineKey Key { get; }

    public Guid RunId { get; }

    public Task Completion { get; set; } = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously).Task;

    public PipelineRunState State { get; set; } = PipelineRunState.Running;

    public ValueTask<PipelineDrainResult> TryDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Calls.Add("drain");
        CallObserver?.Invoke($"{Key.Value}:drain");
        DrainTimeout = timeout;
        DrainToken = cancellationToken;
        return DrainError is null
            ? ValueTask.FromResult(DrainResult)
            : ValueTask.FromException<PipelineDrainResult>(DrainError);
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        Calls.Add("abort");
        CallObserver?.Invoke($"{Key.Value}:abort");
        AbortToken = cancellationToken;
        return AbortError is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(AbortError);
    }

    public ValueTask DisposeAsync()
    {
        Calls.Add("dispose");
        CallObserver?.Invoke($"{Key.Value}:dispose");
        return DisposeError is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeError);
    }
}
