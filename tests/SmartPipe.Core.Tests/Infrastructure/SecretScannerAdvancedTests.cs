using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class SecretScannerAdvancedTests
{
    [Fact]
    public void HasSecrets_JwtToken_ShouldDetect()
    {
        SecretScanner.HasSecrets("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_AwsKey_ShouldDetect()
    {
        SecretScanner.HasSecrets("AKIA1234567890ABCDEF").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_GitHubToken_ShouldDetect()
    {
        SecretScanner.HasSecrets("ghp_1234567890abcdef1234567890abcdef12345678").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_OAuthToken_ShouldDetect()
    {
        SecretScanner.HasSecrets("ya29.a0AfH6SMB-1234567890abcdef").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_CleanData_ShouldBeFalse()
    {
        SecretScanner.HasSecrets("Hello, world!").Should().BeFalse();
    }

    [Fact]
    public void Redact_Jwt_ShouldReplace()
    {
        var result = SecretScanner.Redact("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U");
        result.Should().Contain("***REDACTED***");
    }
}
