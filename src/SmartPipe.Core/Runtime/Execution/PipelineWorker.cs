#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class PipelineWorker<TInput>
{
    private readonly Func<ProcessingEnvelope<TInput>, CancellationToken, ValueTask<FailureAction?>>
        _processEnvelope;
    private readonly Action _requestStopAccepting;

    public PipelineWorker(
        Func<ProcessingEnvelope<TInput>, CancellationToken, ValueTask<FailureAction?>> processEnvelope,
        Action requestStopAccepting)
    {
        _processEnvelope = processEnvelope ?? throw new ArgumentNullException(nameof(processEnvelope));
        _requestStopAccepting = requestStopAccepting
            ?? throw new ArgumentNullException(nameof(requestStopAccepting));
    }

    public async Task RunAsync(
        ChannelReader<ProcessingEnvelope<TInput>> reader,
        ChannelWriter<ProcessingEnvelope<TInput>> writer,
        Action<Exception> recordFailure,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(recordFailure);

        try
        {
            await foreach (var envelope in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var action = await _processEnvelope(envelope, ct).ConfigureAwait(false);
                if (action == FailureAction.StopPipeline)
                    _requestStopAccepting();
            }
        }
        catch (Exception ex)
        {
            recordFailure(ex);
            writer.TryComplete(ex);
            throw;
        }
    }
}
