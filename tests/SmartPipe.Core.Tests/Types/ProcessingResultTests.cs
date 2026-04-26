using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class ProcessingResultTests
{
    [Fact]
    public void Success_ShouldSetIsSuccessAndValue()
    {
        var result = ProcessingResult<string>.Success("data", 123UL);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("data");
        result.Error.Should().BeNull();
        result.TraceId.Should().Be(123UL);
    }

    [Fact]
    public void Failure_ShouldSetErrorAndNotValue()
    {
        var error = new SmartPipeError("Failed", ErrorType.Permanent);
        var result = ProcessingResult<string>.Failure(error, 456UL);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        result.Value.Should().BeNull();
        result.TraceId.Should().Be(456UL);
    }

    [Fact]
    public void ImplicitBool_Success_ShouldBeTrue()
    {
        var result = ProcessingResult<int>.Success(42, 1UL);
        bool isSuccess = result;
        isSuccess.Should().BeTrue();
    }

    [Fact]
    public void ImplicitBool_Failure_ShouldBeFalse()
    {
        var result = ProcessingResult<int>.Failure(new("err", ErrorType.Permanent), 1UL);
        bool isSuccess = result;
        isSuccess.Should().BeFalse();
    }

    [Fact]
    public void IsValueType_ShouldBeRecordStruct()
    {
        var r1 = ProcessingResult<int>.Success(1, 1UL);
        var r2 = r1;
        r2.Should().Be(r1);
    }
}
