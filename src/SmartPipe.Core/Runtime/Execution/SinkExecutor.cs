#nullable enable

namespace SmartPipe.Core;

internal sealed class SinkExecutor<TOutput> : IDisposable
{
    private readonly IPipelineSink<TOutput>? _sink;
    private readonly string _pipelineId;
    private readonly string _runId;
    private readonly IPipelineClock _clock;
    private readonly Func<PipelineEvent, CancellationToken, ValueTask> _emitAsync;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SinkExecutor(
        IPipelineSink<TOutput>? sink,
        string pipelineId,
        string runId,
        IPipelineClock clock,
        Func<PipelineEvent, CancellationToken, ValueTask> emitAsync)
    {
        _sink = sink;
        _pipelineId = pipelineId ?? throw new ArgumentNullException(nameof(pipelineId));
        _runId = runId ?? throw new ArgumentNullException(nameof(runId));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _emitAsync = emitAsync ?? throw new ArgumentNullException(nameof(emitAsync));
    }

    public bool HasSink => _sink is not null;

    public async ValueTask WriteAsync(
        ProcessingEnvelope<TOutput> outputEnvelope,
        CancellationToken ct)
    {
        if (_sink is null)
            return;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _emitAsync(
                    new SinkWriteStartedEvent(
                        _pipelineId,
                        _runId,
                        outputEnvelope.TraceId,
                        outputEnvelope.Attempt,
                        _clock.GetUtcNow()
                    ),
                    ct
                )
                .ConfigureAwait(false);

            try
            {
                await _sink.WriteAsync(outputEnvelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _emitAsync(
                        new SinkWriteFailedEvent(
                            _pipelineId,
                            _runId,
                            outputEnvelope.TraceId,
                            outputEnvelope.Attempt,
                            _clock.GetUtcNow(),
                            ex
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        _writeGate.Dispose();
    }
}
