using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class MetadataBagTests
{
    [Fact]
    public void Empty_ShouldBeReusableSingleton()
    {
        MetadataBag.Empty.AsReadOnlyDictionary().Should().BeEmpty();
    }

    [Fact]
    public void Set_ShouldReturnNewBagWithoutMutatingOriginal()
    {
        var original = MetadataBag.Empty;

        var updated = original.Set("trace", "abc");

        original.Contains("trace").Should().BeFalse();
        updated.GetString("trace").Should().Be("abc");
    }

    [Fact]
    public void From_ShouldCopyInputDictionary()
    {
        var source = new Dictionary<string, string> { ["a"] = "1" };

        var bag = MetadataBag.From(source);
        source["a"] = "2";

        bag.GetString("a").Should().Be("1");
    }
}
