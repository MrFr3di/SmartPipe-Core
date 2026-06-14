#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class PipelineOutputEmitter<TOutput>
{
    private readonly ChannelWriter<PipelineOutput<TOutput>> _writer;
    private readonly PipelineRuntimeOptions _options;
    private readonly bool _hasSink;

    public PipelineOutputEmitter(
        ChannelWriter<PipelineOutput<TOutput>> writer,
        PipelineRuntimeOptions options,
        bool hasSink)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hasSink = hasSink;
    }

    public async ValueTask WriteAsync(
        PipelineOutput<TOutput> output,
        CancellationToken ct)
    {
        if (ShouldEmit(output.Result))
            await _writer.WriteAsync(output, ct).ConfigureAwait(false);
    }

    public bool ShouldEmit(PipelineResult<TOutput> result)
    {
        if (_options.OutputPolicy != PipelineOutputPolicy.EmitAll)
        {
            return _options.OutputPolicy switch
            {
                PipelineOutputPolicy.EmitFailuresOnly => !result.IsSuccess,
                PipelineOutputPolicy.SuppressSuccessWhenSinkAttached => !_hasSink || !result.IsSuccess,
                PipelineOutputPolicy.SuppressAllWhenSinkAttached => !_hasSink,
                _ => throw new InvalidOperationException(
                    $"Unsupported output policy '{_options.OutputPolicy}'."),
            };
        }

        return _options.OutputMode switch
        {
            PipelineOutputMode.EmitAll => true,
            PipelineOutputMode.FailuresOnlyWhenSinkAttached => !_hasSink || !result.IsSuccess,
            PipelineOutputMode.SuppressWhenSinkAttached => !_hasSink,
            PipelineOutputMode.SuppressAll => false,
            _ => throw new InvalidOperationException(
                $"Unsupported output mode '{_options.OutputMode}'."),
        };
    }
}
