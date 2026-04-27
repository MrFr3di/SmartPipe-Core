using System.Threading.Channels;

namespace SmartPipe.Extensions;

/// <summary>
/// Merges two ChannelReader<T> streams into one.
/// Items from both readers are interleaved as they arrive.
/// </summary>
public static class ChannelMerge
{
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

    private static async Task PumpAsync<T>(ChannelReader<T> reader, ChannelWriter<T> writer)
    {
        await foreach (var item in reader.ReadAllAsync())
            await writer.WriteAsync(item);
    }
}
