using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class SmartPipeErrorTests
{
    [Fact]
    public void Constructor_ShouldSetAllProperties()
    {
        var error = new SmartPipeError("Test error", ErrorType.Transient, "Network");

        error.Message.Should().Be("Test error");
        error.Type.Should().Be(ErrorType.Transient);
        error.Category.Should().Be("Network");
        error.InnerException.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithException_ShouldStoreException()
    {
        var ex = new InvalidOperationException("Inner");
        var error = new SmartPipeError("Outer", ErrorType.Permanent, null, ex);

        error.InnerException.Should().Be(ex);
    }

    [Fact]
    public void ToString_ShouldContainAllFields()
    {
        var error = new SmartPipeError("Test", ErrorType.Transient, "IO");

        var str = error.ToString();
        str.Should().Contain("Transient")
           .And.Contain("IO")
           .And.Contain("Test");
    }

    [Fact]
    public void ToString_WithNullCategory_ShouldShowNA()
    {
        var error = new SmartPipeError("Test", ErrorType.Permanent);

        error.ToString().Should().Contain("N/A");
    }

    [Fact]
    public void IsValueType_ShouldBeRecordStruct()
    {
        var error1 = new SmartPipeError("A", ErrorType.Transient);
        var error2 = error1;
        error2.Should().Be(error1); // Value equality
    }
}
