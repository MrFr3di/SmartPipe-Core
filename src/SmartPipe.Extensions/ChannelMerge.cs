using System.Threading.Channels;

namespace SmartPipe.Extensions;

/// <summary>
/// Provides methods for merging multiple <see cref="ChannelReader{T}"/> streams into a single reader.
/// Items from all readers are interleaved as they arrive.
/// </summary>
public static class ChannelMerge
{
    /// <summary>
    /// Merges two <see cref="ChannelReader{T}"/> instances into a single unbounded channel reader.
    /// Both readers are pumped concurrently, and items are written to the output as they arrive.
    /// </summary>
    /// <typeparam name="T">The type of items in the channels.</typeparam>
    /// <param name="first">The first channel reader.</param>
    /// <param name="second">The second channel reader.</param>
    /// <returns>A <see cref="ChannelReader{T}"/> that receives items from both input readers.</returns>
    public static ChannelReader<T> Merge<T>(ChannelReader<T> first, ChannelReader<T> second)
    {
        var output = Channel.CreateUnbounded<T>();

        _ = Task.Run(async () =>
        {
            try
            {
                var readers = new[] { first, second };
                var tasks = readers.Select(r => PumpAsync(r, output.Writer)).ToArray();
                await Task.WhenAll(tasks);
            }
            finally
            {
                output.Writer.TryComplete();
            }
        });

        return output.Reader;
    }

    /// <summary>
    /// Pumps items from a source <see cref="ChannelReader{T}"/> to a target <see cref="ChannelWriter{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of items.</typeparam>
    /// <param name="reader">The source channel reader.</param>
    /// <param name="writer">The target channel writer.</param>
    private static async Task PumpAsync<T>(ChannelReader<T> reader, ChannelWriter<T> writer)
    {
        await foreach (var item in reader.ReadAllAsync())
            await writer.WriteAsync(item);
    }
}
