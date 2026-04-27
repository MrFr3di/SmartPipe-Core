using System.Collections.Concurrent;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class ProcessingContextTests
{
    [Fact]
    public void Constructor_ShouldGenerateUniqueTraceId()
    {
        var ctx1 = new ProcessingContext<string>("payload1");
        var ctx2 = new ProcessingContext<string>("payload2");
        ctx1.TraceId.Should().NotBe(ctx2.TraceId);
    }

    [Fact]
    public void Constructor_ShouldSetPayload()
    {
        var ctx = new ProcessingContext<int>(42);
        ctx.Payload.Should().Be(42);
    }

    [Fact]
    public void Constructor_ShouldInitializeEmptyMetadata()
    {
        var ctx = new ProcessingContext<string>("test");
        ctx.Metadata.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Constructor_WithMetadata_ShouldCopyDictionary()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var ctx = new ProcessingContext<string>("test", metadata);
        ctx.Metadata.Should().ContainKey("key").WhoseValue.Should().Be("value");
        ctx.Metadata.Should().NotBeSameAs(metadata);
    }

    [Fact]
    public void EnterPipelineTicks_ShouldBeSet()
    {
        var ctx = new ProcessingContext<string>("test");
        ctx.EnterPipelineTicks.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Metadata_ShouldBeMutable()
    {
        var ctx = new ProcessingContext<string>("test");
        ctx.Metadata["newKey"] = "newValue";
        ctx.Metadata.Should().ContainKey("newKey");
    }

    [Fact]
    public void TraceId_ShouldBeMonotonic()
    {
        var ids = new List<ulong>();
        for (int i = 0; i < 100; i++)
            ids.Add(new ProcessingContext<int>(i).TraceId);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TraceId_ShouldBeThreadSafe()
    {
        var ids = new ConcurrentBag<ulong>();
        Parallel.For(0, 1000, i => ids.Add(new ProcessingContext<int>(i).TraceId));
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(1000);
    }

    [Fact]
    public void Reset_ShouldGenerateNewTraceId()
    {
        var ctx = new ProcessingContext<string>("test");
        var oldId = ctx.TraceId;
        ctx.Reset();
        ctx.TraceId.Should().NotBe(oldId);
        ctx.Payload.Should().BeNull();
        ctx.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstructor_ShouldCreateValidContext()
    {
        var ctx = new ProcessingContext<int>();
        ctx.TraceId.Should().BeGreaterThan(0UL);
        ctx.Payload.Should().Be(0); // default(int)
        ctx.Metadata.Should().NotBeNull();
    }
}
