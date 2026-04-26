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
        ctx.Metadata.Should().NotBeSameAs(metadata); // Copied, not referenced
    }

    [Fact]
    public void EnterPipelineTicks_ShouldBeSetToCurrentTime()
    {
        var before = Environment.TickCount64;
        var ctx = new ProcessingContext<string>("test");
        var after = Environment.TickCount64;

        ctx.EnterPipelineTicks.Should().BeInRange(before, after);
    }

    [Fact]
    public void AddMetadata_ShouldReturnNewInstance()
    {
        var ctx1 = new ProcessingContext<string>("test");
        var ctx2 = ctx1.AddMetadata("key", "value");

        ctx2.Should().NotBeSameAs(ctx1);
        ctx2.Metadata.Should().ContainKey("key");
        ctx1.Metadata.Should().BeEmpty(); // Original unchanged
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
        Parallel.For(0, 1000, i =>
        {
            ids.Add(new ProcessingContext<int>(i).TraceId);
        });

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(1000);
    }
}
