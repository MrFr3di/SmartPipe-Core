using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class ErrorTypeTests
{
    [Fact]
    public void ErrorType_ShouldHaveTransientAndPermanent()
    {
        Enum.GetValues<ErrorType>().Should().Contain([ErrorType.Transient, ErrorType.Permanent]);
    }

    [Fact]
    public void Transient_ShouldNotEqualPermanent()
    {
        ErrorType.Transient.Should().NotBe(ErrorType.Permanent);
    }
}
