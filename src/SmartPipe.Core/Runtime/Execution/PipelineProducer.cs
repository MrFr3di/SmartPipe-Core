#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class PipelineProducer<TInput>
{
    private readonly IPipelineSource<TInput> _source;
    private readonly Func<bool> _shouldStopAccepting;
    private readonly Action _recordAccepted;

    public PipelineProducer(
        IPipelineSource<TInput> source,
        Func<bool> shouldStopAccepting,
        Action recordAccepted)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _shouldStopAccepting = shouldStopAccepting
            ?? throw new ArgumentNullException(nameof(shouldStopAccepting));
        _recordAccepted = recordAccepted ?? throw new ArgumentNullException(nameof(recordAccepted));
    }

    public async Task ProduceAsync(
        ChannelWriter<ProcessingEnvelope<TInput>> writer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var enumerator = _source
            .ReadEnvelopesAsync(ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (!_shouldStopAccepting() && await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                await writer.WriteAsync(enumerator.Current, ct).ConfigureAwait(false);
                _recordAccepted();
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
