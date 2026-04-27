using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class PipelineToolTests
{
    [Fact]
    public void Constructor_ShouldSetNameAndDescription()
    {
        var channel = new SmartPipeChannel<string, string>();
        var tool = new PipelineTool<string, string>("test", "Test tool", channel);

        tool.Name.Should().Be("test");
        tool.Description.Should().Be("Test tool");
    }

    [Fact]
    public async Task ExecuteAsync_WithTransformer_ShouldReturnResult()
    {
        var channel = new SmartPipeChannel<string, string>();
        channel.AddTransformer(new PassthroughTransformer<string>());
        var tool = new PipelineTool<string, string>("summarize", "Summarizer", channel);

        var result = await tool.ExecuteAsync("hello");
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
    }

    [Fact]
    public async Task ExecuteAsync_NoTransformer_ShouldFail()
    {
        var channel = new SmartPipeChannel<string, string>();
        var tool = new PipelineTool<string, string>("test", "Test", channel);

        var result = await tool.ExecuteAsync("hello");
        result.IsSuccess.Should().BeFalse();
    }
}
