using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting;

internal sealed class HostedPipelineRun<TOutput>(PipelineRun<TOutput> run) : IHostedPipelineRun
{
    private readonly PipelineRun<TOutput> _run =
        run ?? throw new ArgumentNullException(nameof(run));

    public PipelineKey Key => _run.PipelineKey;

    public Guid RunId => _run.RunId;

    public Task Completion => _run.Completion;

    public PipelineRunState State => _run.State;

    public ValueTask<PipelineDrainResult> TryDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _run.TryDrainAsync(timeout, cancellationToken);

    public ValueTask AbortAsync(CancellationToken cancellationToken) =>
        _run.AbortAsync(cancellationToken);

    public ValueTask DisposeAsync() => _run.DisposeAsync();
}
