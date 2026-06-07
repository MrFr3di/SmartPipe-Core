using FluentAssertions;
using SmartPipe.Core;
using System.Threading.Channels;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelReaderTests
{
    [Fact]
    public async Task AsChannelReader_AfterRunAsync_ShouldReturnNull()
    {
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(new SimpleSource<int>(1, 2, 3));
        channel.AddTransformer(new PassthroughTransformer<int>());
        channel.AddSink(new CollectionSink<int>());

        // Before RunAsync — reader is null
        channel.AsChannelReader().Should().BeNull();

        await channel.RunAsync();

        // After RunAsync — internal output reader remains owned by the pipeline.
        channel.AsChannelReader().Should().BeNull();
    }
}
