using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class StageResultTests
{
    [Fact]
    public void Default_ShouldBeInvalid()
    {
        StageResult<string> result = default;

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Success_ShouldCreateValidSuccess()
    {
        var result = StageResult<string>.Success("ok");

        result.IsValid.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Kind.Should().Be(StageResultKind.Success);
        result.Value.Should().Be("ok");
    }

    [Fact]
    public void Failure_ShouldConvertToProcessingResult()
    {
        var error = new SmartPipeError("failed", ErrorType.Permanent, "Test");
        var result = StageResult<string>.Failure(error);

        var legacy = result.ToProcessingResult(42);

        legacy.IsSuccess.Should().BeFalse();
        legacy.TraceId.Should().Be(42);
        legacy.Error.Should().Be(error);
    }
}
