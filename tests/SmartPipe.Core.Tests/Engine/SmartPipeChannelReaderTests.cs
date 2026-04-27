using FluentAssertions;
using SmartPipe.Core;
using System.Threading.Channels;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelReaderTests
{
    [Fact]
    public async Task AsChannelReader_AfterRun_ShouldReturnNull()
    {
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(new SimpleSource<int>(1, 2, 3));
        channel.AddTransformer(new PassthroughTransformer<int>());
        channel.AddSink(new CollectionSink<int>());

        // Before RunAsync — reader is null
        channel.AsChannelReader().Should().BeNull();

        await channel.RunAsync();

        // After RunAsync — reader is completed (channel disposed)
        var reader = channel.AsChannelReader();
        reader.Should().NotBeNull();
    }
}
