using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineKeyTests
{
    [Fact]
    public void Constructor_Null_ThrowsArgumentNullException()
    {
        var act = () => new PipelineKey(null!);

        act.Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Empty_ThrowsArgumentException()
    {
        var act = () => new PipelineKey(string.Empty);

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Constructor_Whitespace_ThrowsArgumentException()
    {
        var act = () => new PipelineKey(" \t\r\n");

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Constructor_ValueWithOuterWhitespace_PreservesExactValue()
    {
        const string value = " Orders ";

        var key = new PipelineKey(value);

        key.IsEmpty.Should().BeFalse();
        key.Value.Should().Be(value);
        key.ToString().Should().Be(value);
    }

    [Fact]
    public void Equality_DifferentCase_IsFalse()
    {
        var upper = new PipelineKey("Orders");
        var lower = new PipelineKey("orders");

        upper.Should().NotBe(lower);
    }

    [Fact]
    public void Equality_SameOrdinalValue_IsTrue()
    {
        var first = new PipelineKey("Orders");
        var second = new PipelineKey("Orders");

        first.Should().Be(second);
    }

    [Fact]
    public void Default_IsEmptyAndValueThrows()
    {
        var key = default(PipelineKey);

        key.IsEmpty.Should().BeTrue();
        var act = () => key.Value;

        act.Should().ThrowExactly<InvalidOperationException>();
    }

    [Fact]
    public void ToString_Default_ReturnsEmptyString()
    {
        default(PipelineKey).ToString().Should().BeEmpty();
    }

    [Fact]
    public void DictionaryLookup_UsesOrdinalCaseSensitiveEquality()
    {
        var values = new Dictionary<PipelineKey, string>
        {
            [new PipelineKey("Orders")] = "value",
        };

        values.Should().ContainKey(new PipelineKey("Orders"));
        values.Should().NotContainKey(new PipelineKey("orders"));
    }
}
