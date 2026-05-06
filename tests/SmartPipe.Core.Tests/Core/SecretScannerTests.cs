#nullable enable
using Xunit;
using SmartPipe.Core;
using System.Reflection;

namespace SmartPipe.Core.Tests.Core;

public class SecretScannerTests
{
    private static string? InvokeTryDecodeBase64(string content)
    {
        var method = typeof(SecretScanner).GetMethod(
            "TryDecodeBase64",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        return (string?)method?.Invoke(null, new object[] { content });
    }

    private static string? InvokeTryDecodeUrl(string content)
    {
        var method = typeof(SecretScanner).GetMethod(
            "TryDecodeUrl",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        return (string?)method?.Invoke(null, new object[] { content });
    }

    [Fact]
    public void TryDecodeBase64_ValidBase64_ReturnsDecodedString()
    {
        var original = "test";
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(original));
        
        var result = InvokeTryDecodeBase64(base64);
        
        Assert.Equal(original, result);
    }

    [Fact]
    public void TryDecodeBase64_InvalidBase64_ReturnsNull()
    {
        var input = "invalid!base64";
        
        var result = InvokeTryDecodeBase64(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeBase64_NullInput_ReturnsNull()
    {
        string? input = null;
        
        var result = InvokeTryDecodeBase64(input!);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeBase64_EmptyInput_ReturnsNull()
    {
        var input = "";
        
        var result = InvokeTryDecodeBase64(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeBase64_WhitespaceInput_ReturnsNull()
    {
        var input = "   ";
        
        var result = InvokeTryDecodeBase64(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeUrl_ValidEncodedUrl_ReturnsDecodedUrl()
    {
        var encoded = Uri.EscapeDataString("test value");
        
        var result = InvokeTryDecodeUrl(encoded);
        
        Assert.Equal("test value", result);
    }

    [Fact]
    public void TryDecodeUrl_InvalidEncodedUrl_ReturnsNull()
    {
        var input = "not%encoded";
        
        var result = InvokeTryDecodeUrl(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void Redact_StringWithSecrets_ReturnsRedactedString()
    {
        var input = "my api_key: 'secret123' and password: 'pass456'";
        
        var result = SecretScanner.Redact(input);
        
        Assert.Contains("***REDACTED***", result);
        Assert.DoesNotContain("secret123", result);
    }

    [Fact]
    public void Redact_StringWithNoSecrets_ReturnsOriginal()
    {
        var input = "this is a clean string without secrets";
        
        var result = SecretScanner.Redact(input);
        
        Assert.Equal(input, result);
    }

    [Fact]
    public void TryDecodeBase64_RawAwsKey_ReturnsNull()
    {
        var awsKey = "AKIA1234567890ABCDEF";
        
        var result = InvokeTryDecodeBase64(awsKey);
        
        Assert.Null(result);
    }

    [Theory]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=")]
    [InlineData("dGVzdA==")]
    public void ValidateBase64Characters_ValidBase64_ReturnsTrue(string input)
    {
        var result = SecretScanner.ValidateBase64Characters(input);
        Assert.True(result);
    }

    [Theory]
    [InlineData("invalid!base64")]
    [InlineData("test string")]
    [InlineData("abc@def")]
    public void ValidateBase64Characters_InvalidChars_ReturnsFalse(string input)
    {
        var result = SecretScanner.ValidateBase64Characters(input);
        Assert.False(result);
    }

    [Fact]
    public void DecodeBase64WithPadding_ValidBase64_ReturnsDecodedString()
    {
        var original = "test";
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(original));
        
        var result = SecretScanner.DecodeBase64WithPadding(base64);
        
        Assert.Equal(original, result);
    }

    [Fact]
    public void DecodeBase64WithPadding_ValidBase64WithoutPadding_ReturnsDecodedString()
    {
        var original = "test";
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(original));
        // Remove padding if present
        var base64NoPadding = base64.TrimEnd('=');
        
        var result = SecretScanner.DecodeBase64WithPadding(base64NoPadding);
        
        Assert.Equal(original, result);
    }

    [Fact]
    public void DecodeBase64WithPadding_InvalidBase64_ReturnsNull()
    {
        var input = "invalid!base64";
        
        var result = SecretScanner.DecodeBase64WithPadding(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void DecodeBase64WithPadding_NullInput_ReturnsNull()
    {
        string? input = null;
        
        var result = SecretScanner.DecodeBase64WithPadding(input!);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeBase64_LengthNotMultipleOf4_ReturnsNull()
    {
        // "abc" has length 3, which is not a multiple of 4
        var input = "abc";
        
        var result = InvokeTryDecodeBase64(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeUrl_NullInput_ReturnsNull()
    {
        string? input = null;
        
        var result = InvokeTryDecodeUrl(input!);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeUrl_EmptyInput_ReturnsNull()
    {
        var input = "";
        
        var result = InvokeTryDecodeUrl(input);
        
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeUrl_DecodedEqualsOriginal_ReturnsNull()
    {
        // String without URL encoding - decoding returns the same string
        var input = "noencoding";
        
        var result = InvokeTryDecodeUrl(input);
        
        Assert.Null(result);
    }
}
