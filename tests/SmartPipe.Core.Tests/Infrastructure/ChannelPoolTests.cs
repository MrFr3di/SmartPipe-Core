using FluentAssertions;
using SmartPipe.Core;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SmartPipe.Core.Tests.Infrastructure;

public class ChannelPoolTests
{
    [Fact]
    public void RentUnbounded_ShouldReturnChannel()
    {
        var channel = ChannelPool.RentUnbounded<int>();
        channel.Should().NotBeNull();
    }

    [Fact]
    public async Task RentBounded_ShouldEnforceCapacity()
    {
        var channel = ChannelPool.RentBounded<int>(1, BoundedChannelFullMode.Wait);

        await channel.Writer.WriteAsync(42);
        channel.Reader.Count.Should().Be(1);
    }

    [Fact]
    public void CloseChannel_ShouldCompleteWriter()
    {
        var channel = ChannelPool.RentUnbounded<int>();
        ChannelPool.CloseChannel(channel);

        // Writer should be completed, reader should eventually complete
        channel.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task CreateBoundedMultiReaderMultiWriter_ShouldSupportConcurrentReadersWithoutLoss_WhenFullModeWait()
    {
        var channel = ChannelPool.CreateBoundedMultiReaderMultiWriter<int>(
            capacity: 8,
            mode: BoundedChannelFullMode.Wait);

        var seen = new ConcurrentDictionary<int, byte>();
        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync())
                    seen.TryAdd(item, 0);
            }))
            .ToArray();

        for (int i = 0; i < 100; i++)
            await channel.Writer.WriteAsync(i);

        channel.Writer.Complete();
        await Task.WhenAll(readers);

        seen.Keys.Should().BeEquivalentTo(Enumerable.Range(0, 100));
    }

    [Fact]
    public async Task CreateBoundedSingleReaderMultiWriter_ShouldSupportConcurrentWriters_WhenFullModeWait()
    {
        var channel = ChannelPool.CreateBoundedSingleReaderMultiWriter<int>(
            capacity: 8,
            mode: BoundedChannelFullMode.Wait);

        var items = new List<int>();
        var reader = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
                items.Add(item);
        });

        var writers = Enumerable.Range(0, 100)
            .Select(i => Task.Run(async () => await channel.Writer.WriteAsync(i)))
            .ToArray();

        await Task.WhenAll(writers);
        channel.Writer.Complete();
        await reader;

        items.Should().BeEquivalentTo(Enumerable.Range(0, 100));
    }
}
