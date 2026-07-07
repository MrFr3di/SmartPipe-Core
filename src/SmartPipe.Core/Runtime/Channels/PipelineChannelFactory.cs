#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal static class PipelineChannelFactory
{
    public static Channel<ProcessingEnvelope<T>> CreateInput<T>(
        int capacity,
        BoundedChannelFullMode fullMode,
        Action<ProcessingEnvelope<T>>? itemDropped = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Input capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Input full mode is invalid.");

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = fullMode,
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        };

        return itemDropped is null
            ? Channel.CreateBounded<ProcessingEnvelope<T>>(options)
            : Channel.CreateBounded(options, itemDropped);
    }

    public static Channel<PipelineOutput<T>> CreateOutput<T>(
        int capacity,
        BoundedChannelFullMode fullMode,
        Action<PipelineOutput<T>>? itemDropped = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Output capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Output full mode is invalid.");

        var options = CreateOutputOptions(capacity, fullMode);

        return itemDropped is null
            ? Channel.CreateBounded<PipelineOutput<T>>(options)
            : Channel.CreateBounded(options, itemDropped);
    }

    internal static BoundedChannelOptions CreateOutputOptions(
        int capacity,
        BoundedChannelFullMode fullMode)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Output capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Output full mode is invalid.");

        return new BoundedChannelOptions(capacity)
        {
            FullMode = fullMode,
            SingleWriter = false,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        };
    }

    public static Channel<PipelineEvent> CreateObserverBuffer(
        int capacity,
        BoundedChannelFullMode fullMode,
        Action<PipelineEvent>? itemDropped = null)
    {
        return CreateObserverBuffer<PipelineEvent>(capacity, fullMode, itemDropped);
    }

    public static Channel<T> CreateObserverBuffer<T>(
        int capacity,
        BoundedChannelFullMode fullMode,
        Action<T>? itemDropped = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Observer buffer capacity must be greater than zero.");

        if (!Enum.IsDefined(fullMode))
            throw new ArgumentOutOfRangeException(nameof(fullMode), fullMode, "Observer full mode is invalid.");

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = fullMode,
            SingleWriter = false,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        };

        return itemDropped is null
            ? Channel.CreateBounded<T>(options)
            : Channel.CreateBounded(options, itemDropped);
    }
}
