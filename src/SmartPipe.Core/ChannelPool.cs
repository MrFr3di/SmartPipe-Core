using System.Threading.Channels;

namespace SmartPipe.Core;

/// <summary>Channel pool for reuse between pipeline runs. Reduces allocations on repeated executions.</summary>
public static class ChannelPool
{
    /// <summary>Rent an unbounded channel with optimized options.</summary>
    /// <typeparam name="T">Channel element type.</typeparam>
    /// <returns>Configured unbounded channel.</returns>
    public static Channel<T> RentUnbounded<T>() =>
        Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        });

    /// <summary>Rent a bounded channel with capacity and full mode.</summary>
    /// <typeparam name="T">Channel element type.</typeparam>
    /// <param name="capacity">Maximum capacity.</param>
    /// <param name="mode">Behavior when channel is full.</param>
    /// <returns>Configured bounded channel.</returns>
    public static Channel<T> RentBounded<T>(int capacity, BoundedChannelFullMode mode) =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = mode,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

    /// <summary>Return a channel to the pool by completing its writer.</summary>
    /// <typeparam name="T">Channel element type.</typeparam>
    /// <param name="channel">Channel to return.</param>
    public static void Return<T>(Channel<T> channel) => channel.Writer.TryComplete();
}
