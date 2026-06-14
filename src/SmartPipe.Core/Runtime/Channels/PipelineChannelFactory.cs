#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal static class PipelineChannelFactory
{
    public static Channel<ProcessingEnvelope<T>> CreateInput<T>(
        int capacity,
        BoundedChannelFullMode fullMode)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Input capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Input full mode is invalid.");

        return Channel.CreateBounded<ProcessingEnvelope<T>>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = fullMode,
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            });
    }

    public static Channel<PipelineOutput<T>> CreateOutput<T>(
        int capacity,
        BoundedChannelFullMode fullMode)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Output capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Output full mode is invalid.");

        return Channel.CreateBounded<PipelineOutput<T>>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = fullMode,
                SingleWriter = false,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            });
    }

    public static Channel<PipelineEvent> CreateObserverBuffer(
        int capacity,
        BoundedChannelFullMode fullMode)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Observer buffer capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Observer full mode is invalid.");

        return Channel.CreateBounded<PipelineEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = fullMode,
                SingleWriter = false,
                SingleReader = true,
                AllowSynchronousContinuations = false,
            });
    }
}
