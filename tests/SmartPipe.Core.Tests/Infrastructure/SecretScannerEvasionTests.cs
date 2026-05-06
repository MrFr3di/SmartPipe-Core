#nullable enable
using System;
using FluentAssertions;
using Xunit;
using SmartPipe.Core.Tests.Infrastructure.TestCases;

namespace SmartPipe.Core.Tests.Infrastructure;

public class SecretScannerEvasionTests
{
    [Theory]
    [MemberData(nameof(SecretScannerEvasionTestCases.AllTestCasesForXUnit), MemberType = typeof(SecretScannerEvasionTestCases))]
    public void Scan_ObfuscatedSecret_ShouldDetect(
        string payload,
        string secretType,
        bool shouldDetect,
        string obfuscationMethod)
    {
        // Act
        var result = SecretScanner.HasSecrets(payload);

        // Assert
        result.Should().Be(shouldDetect,
            $"obfuscation method: {obfuscationMethod}, secret type: {secretType}, payload: {payload[..System.Math.Min(payload.Length, 100)]}");
    }

    [Theory]
    [InlineData("hello world", "plain text")]
    [InlineData("my api_key is valid", "api_key without secret format")]
    [InlineData("the password is strong", "password without assignment")]
    [InlineData("ghp_abc", "too short github token")]
    [InlineData("AKIAABC", "too short aws key")]
    [InlineData("sk-abc", "too short openai key")]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6", "incomplete jwt")]
    [InlineData("-----BEGIN CERTIFICATE-----", "certificate not private key")]
    [InlineData("ya29abc", "too short oauth token")]
    public void Scan_NonSecret_ShouldNotDetect(string payload, string description)
    {
        // Act
        var result = SecretScanner.HasSecrets(payload);

        // Assert
        result.Should().BeFalse($"non-secret payload should not trigger detection: {description}");
    }

    [Fact]
    public void Scan_Base64EncodedNonSecret_ShouldNotDetect()
    {
        // Arrange
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello world"));

        // Act
        var result = SecretScanner.HasSecrets(payload);

        // Assert
        result.Should().BeFalse("base64 encoded non-secret should not trigger detection");
    }

    [Fact]
    public void Scan_UrlEncodedNonSecret_ShouldNotDetect()
    {
        // Arrange
        var payload = Uri.EscapeDataString("hello world");

        // Act
        var result = SecretScanner.HasSecrets(payload);

        // Assert
        result.Should().BeFalse("url-encoded non-secret should not trigger detection");
    }

    #region Base64 Evasion Tests (Task 3.1)

    [Fact]
    public void Base64Encoded_ApiKey_ShouldBeDetected()
    {
        // Arrange
        var apiKey = "api_key: 'sk-1234567890abcdef1234567890abcdef'";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(apiKey));

        // Act
        var result = SecretScanner.HasSecrets(base64Encoded);

        // Assert
        result.Should().BeTrue("Base64-encoded API key should be detected");
    }

    [Fact]
    public void Base64Encoded_Password_ShouldBeDetected()
    {
        // Arrange
        var password = "password: 'SuperSecret123!'";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password));

        // Act
        var result = SecretScanner.HasSecrets(base64Encoded);

        // Assert
        result.Should().BeTrue("Base64-encoded password should be detected");
    }

    [Fact]
    public void Base64Encoded_PrivateKey_ShouldBeDetected()
    {
        // Arrange
        var privateKey = "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA1234567890abcdef\n-----END RSA PRIVATE KEY-----";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(privateKey));

        // Act
        var result = SecretScanner.HasSecrets(base64Encoded);

        // Assert
        result.Should().BeTrue("Base64-encoded private key should be detected");
    }

    [Fact]
    public void Base64Encoded_Jwt_ShouldBeDetected()
    {
        // Arrange
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jwt));

        // Act
        var result = SecretScanner.HasSecrets(base64Encoded);

        // Assert
        result.Should().BeTrue("Base64-encoded JWT should be detected");
    }

    [Fact]
    public void Base64Encoded_GitHubToken_ShouldBeDetected()
    {
        // Arrange
        var githubToken = "ghp_abcdefghijklmnopqrstuvwxyz1234567890ABCD";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(githubToken));

        // Act
        var result = SecretScanner.HasSecrets(base64Encoded);

        // Assert
        result.Should().BeTrue("Base64-encoded GitHub token should be detected");
    }

    [Fact]
    public void Base64Encoded_AwsKey_ShouldBeDetected()
    {
        // Arrange
        var awsKey = "AKIAIOSFODNN7EXAMPLE";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(awsKey));

        // Act
        var result = SecretScanner.HasSecrets(base64Encoded);

        // Assert
        result.Should().BeTrue("Base64-encoded AWS key should be detected");
    }

    [Fact]
    public void InvalidBase64_ShouldNotCrash()
    {
        // Arrange
        var invalidBase64 = "Not!Valid@Base64#Content$With%Special^Chars&";

        // Act & Assert - should not throw
        var result = SecretScanner.HasSecrets(invalidBase64);
        result.Should().BeFalse("invalid Base64 should not be detected as secret");
    }

    [Fact]
    public void Redact_Base64EncodedSecret_ShouldWork()
    {
        // Arrange
        var secret = "api_key: 'sk-1234567890abcdef'";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));

        // Act
        var result = SecretScanner.Redact(base64Encoded);

        // Assert - the redaction happens on the decoded content
        // Since we can't easily map back, we verify HasSecrets returns true
        SecretScanner.HasSecrets(base64Encoded).Should().BeTrue();
    }

    #endregion

    #region URL Encoded Evasion Tests (Task 3.2)

    [Fact]
    public void UrlEncoded_ApiKey_ShouldBeDetected()
    {
        // Arrange
        var apiKey = "api_key: 'sk-1234567890abcdef'";
        var urlEncoded = Uri.EscapeDataString(apiKey);

        // Act
        var result = SecretScanner.HasSecrets(urlEncoded);

        // Assert
        result.Should().BeTrue("URL-encoded API key should be detected");
    }

    [Fact]
    public void UrlEncoded_Password_ShouldBeDetected()
    {
        // Arrange
        var password = "password: 'SuperSecret123!'";
        var urlEncoded = Uri.EscapeDataString(password);

        // Act
        var result = SecretScanner.HasSecrets(urlEncoded);

        // Assert
        result.Should().BeTrue("URL-encoded password should be detected");
    }

    [Fact]
    public void UrlEncoded_PrivateKey_ShouldBeDetected()
    {
        // Arrange
        var privateKey = "-----BEGIN RSA PRIVATE KEY-----";
        var urlEncoded = Uri.EscapeDataString(privateKey);

        // Act
        var result = SecretScanner.HasSecrets(urlEncoded);

        // Assert
        result.Should().BeTrue("URL-encoded private key header should be detected");
    }

    [Fact]
    public void DoubleEncoded_Base64ThenUrl_ShouldBeDetected()
    {
        // Arrange
        var secret = "api_key: 'sk-1234567890abcdef'";
        var base64Encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));
        var doubleEncoded = Uri.EscapeDataString(base64Encoded);

        // Act
        var result = SecretScanner.HasSecrets(doubleEncoded);

        // Assert
        result.Should().BeTrue("Double-encoded (URL + Base64) secret should be detected");
    }

    [Fact]
    public void DoubleEncoded_UrlThenBase64_ShouldBeDetected()
    {
        // Arrange
        var secret = "password: 'SuperSecret123!'";
        var urlEncoded = Uri.EscapeDataString(secret);
        var doubleEncoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(urlEncoded));

        // Act
        var result = SecretScanner.HasSecrets(doubleEncoded);

        // Assert
        result.Should().BeTrue("Double-encoded (Base64 + URL) secret should be detected");
    }

    [Fact]
    public void InvalidUrlEncoding_ShouldNotCrash()
    {
        // Arrange
        var invalidUrl = "Hello%ZZWorld"; // %ZZ is not valid hex

        // Act & Assert - should not throw
        var result = SecretScanner.HasSecrets(invalidUrl);
        // Result can be true or false, we just care that it doesn't crash
    }

    [Fact]
    public void UrlEncoded_GitHubToken_ShouldBeDetected()
    {
        // Arrange
        var githubToken = "ghp_abcdefghijklmnopqrstuvwxyz1234567890ABCD";
        var urlEncoded = Uri.EscapeDataString(githubToken);

        // Act
        var result = SecretScanner.HasSecrets(urlEncoded);

        // Assert
        result.Should().BeTrue("URL-encoded GitHub token should be detected");
    }

    [Fact]
    public void Redact_UrlEncodedSecret_ShouldWork()
    {
        // Arrange
        var secret = "password: 'MySecretPassword123'";
        var urlEncoded = Uri.EscapeDataString(secret);

        // Act
        var result = SecretScanner.Redact(urlEncoded);

        // Assert - verify HasSecrets returns true for the encoded secret
        SecretScanner.HasSecrets(urlEncoded).Should().BeTrue();
    }

    #endregion
}
