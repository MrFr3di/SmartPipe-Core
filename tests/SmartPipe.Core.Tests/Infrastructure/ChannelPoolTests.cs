using FluentAssertions;
using SmartPipe.Core;
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
    public void Return_ShouldCompleteWriter()
    {
        var channel = ChannelPool.RentUnbounded<int>();
        ChannelPool.Return(channel);

        // Writer should be completed, reader should eventually complete
        channel.Reader.Completion.IsCompleted.Should().BeTrue();
    }
}
