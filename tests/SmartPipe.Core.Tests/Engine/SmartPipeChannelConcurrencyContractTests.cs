#nullable enable

using FluentAssertions;
using SmartPipe.Core.Tests;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SmartPipe.Core.Tests.Engine;

public sealed class SmartPipeChannelConcurrencyContractTests
{
    [Fact]
    public async Task RunAsync_WithMultipleWorkersAndBackpressure_ShouldEmitEachAcceptedItemExactlyOnce()
    {
        var input = Enumerable.Range(0, 100).ToArray();
        var source = new AcceptedTrackingSource<int>(input);
        var sink = new CollectingSink<int>();
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 8,
            MaxDegreeOfParallelism = 4,
            FullMode = BoundedChannelFullMode.Wait,
            UseRendezvous = false,
        };
        var channel = new SmartPipeChannel<int, int>(options);

        channel.AddSource(source);
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(sink);

        await channel.RunAsync();

        source.AcceptedCount.Should().Be(input.Length);
        sink.Items.Should().HaveCount(input.Length);
        sink.Items.Should().BeEquivalentTo(input);

        var counts = new ConcurrentDictionary<int, int>();
        foreach (var item in sink.Items)
            counts.AddOrUpdate(item, 1, (_, count) => count + 1);

        counts.Should().OnlyContain(pair => pair.Value == 1);
    }
}
