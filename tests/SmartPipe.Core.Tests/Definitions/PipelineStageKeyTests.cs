using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineStageKeyTests
{
    [Fact]
    public void Constructor_Null_ThrowsArgumentNullException()
    {
        var act = () => new PipelineStageKey(null!);

        act.Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Empty_ThrowsArgumentException()
    {
        var act = () => new PipelineStageKey(string.Empty);

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Constructor_Whitespace_ThrowsArgumentException()
    {
        var act = () => new PipelineStageKey(" \t\r\n");

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Constructor_ValueWithOuterWhitespace_PreservesExactValue()
    {
        const string value = " Normalize ";

        var key = new PipelineStageKey(value);

        key.IsEmpty.Should().BeFalse();
        key.Value.Should().Be(value);
        key.ToString().Should().Be(value);
    }

    [Fact]
    public void Equality_DifferentCase_IsFalse()
    {
        var upper = new PipelineStageKey("Normalize");
        var lower = new PipelineStageKey("normalize");

        upper.Should().NotBe(lower);
    }

    [Fact]
    public void Equality_SameOrdinalValue_IsTrue()
    {
        var first = new PipelineStageKey("Normalize");
        var second = new PipelineStageKey("Normalize");

        first.Should().Be(second);
    }

    [Fact]
    public void Default_IsEmptyAndValueThrows()
    {
        var key = default(PipelineStageKey);

        key.IsEmpty.Should().BeTrue();
        var act = () => key.Value;

        act.Should().ThrowExactly<InvalidOperationException>();
    }

    [Fact]
    public void ToString_Default_ReturnsEmptyString()
    {
        default(PipelineStageKey).ToString().Should().BeEmpty();
    }

    [Fact]
    public void DictionaryLookup_UsesOrdinalCaseSensitiveEquality()
    {
        var values = new Dictionary<PipelineStageKey, string>
        {
            [new PipelineStageKey("Normalize")] = "value",
        };

        values.Should().ContainKey(new PipelineStageKey("Normalize"));
        values.Should().NotContainKey(new PipelineStageKey("normalize"));
    }
}
