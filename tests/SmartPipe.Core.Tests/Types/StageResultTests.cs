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
    public void Failure_ShouldCreateValidFailure()
    {
        var error = new SmartPipeError("failed", ErrorType.Permanent, "Test");
        var result = StageResult<string>.Failure(error);

        result.IsValid.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(StageResultKind.Failure);
        result.Error.Should().Be(error);
    }
}
