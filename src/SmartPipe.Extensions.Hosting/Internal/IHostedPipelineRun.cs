using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting;

internal interface IHostedPipelineRun : IAsyncDisposable
{
    PipelineKey Key { get; }

    Guid RunId { get; }

    Task Completion { get; }

    PipelineRunState State { get; }

    ValueTask<PipelineDrainResult> TryDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask AbortAsync(CancellationToken cancellationToken);
}
