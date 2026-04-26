using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class SecretScannerTests
{
    [Fact]
    public void HasSecrets_ApiKey_ShouldDetect()
    {
        SecretScanner.HasSecrets("api_key: 'sk-1234567890abcdef'").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_Password_ShouldDetect()
    {
        SecretScanner.HasSecrets("password: 'supersecret'").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_PrivateKey_ShouldDetect()
    {
        SecretScanner.HasSecrets("-----BEGIN RSA PRIVATE KEY-----").Should().BeTrue();
    }

    [Fact]
    public void HasSecrets_CleanData_ShouldBeFalse()
    {
        SecretScanner.HasSecrets("Hello, world!").Should().BeFalse();
    }

    [Fact]
    public void Redact_ShouldReplaceSecrets()
    {
        var result = SecretScanner.Redact("api_key: 'sk-secret'");
        result.Should().Be("***REDACTED***");
    }

    [Fact]
    public void Redact_CleanData_ShouldStaySame()
    {
        var result = SecretScanner.Redact("Normal text");
        result.Should().Be("Normal text");
    }
}
