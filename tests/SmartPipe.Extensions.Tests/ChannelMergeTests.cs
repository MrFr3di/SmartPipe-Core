using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public class ChannelMergeTests
{
    [Fact]
    public async Task Merge_TwoChannels_ShouldCombine()
    {
        var ch1 = Channel.CreateUnbounded<int>();
        var ch2 = Channel.CreateUnbounded<int>();

        await ch1.Writer.WriteAsync(1);
        await ch1.Writer.WriteAsync(2);
        ch1.Writer.Complete();

        await ch2.Writer.WriteAsync(3);
        await ch2.Writer.WriteAsync(4);
        ch2.Writer.Complete();

        var merged = ChannelMerge.Merge(ch1.Reader, ch2.Reader);
        var results = new List<int>();
        
        await foreach (var item in merged.ReadAllAsync())
            results.Add(item);

        results.Should().BeEquivalentTo([1, 2, 3, 4]);
    }
}
