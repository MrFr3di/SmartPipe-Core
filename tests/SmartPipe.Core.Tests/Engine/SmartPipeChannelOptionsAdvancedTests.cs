using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelOptionsAdvancedTests
{
    [Fact]
    public void OnProgress_ShouldBeSettable()
    {
        var options = new SmartPipeChannelOptions();
        options.OnProgress = (current, total, elapsed, eta) => { };
        options.OnProgress.Should().NotBeNull();
    }

    [Fact]
    public void DeadLetterSink_ShouldBeSettable()
    {
        var options = new SmartPipeChannelOptions();
        options.DeadLetterSink = null;
        options.DeadLetterSink.Should().BeNull();
    }

    [Fact]
    public void FullMode_Default_ShouldBeWait()
    {
        new SmartPipeChannelOptions().FullMode.Should().Be(System.Threading.Channels.BoundedChannelFullMode.Wait);
    }

    [Fact]
    public async Task RunAsync_WithOnProgress_ShouldCallDelegate()
    {
        var calls = new List<int>();
        var options = new SmartPipeChannelOptions { OnProgress = (current, _, _, _) => { lock (calls) calls.Add(current); } };
        var pipe = new SmartPipeChannel<int, int>(options);
        pipe.AddSource(new SimpleSource<int>(1, 2, 3));
        pipe.AddTransformer(new PassthroughTransformer<int>());
        pipe.AddSink(new CollectionSink<int>());
        await pipe.RunAsync();
        calls.Should().NotBeEmpty();
    }
}
