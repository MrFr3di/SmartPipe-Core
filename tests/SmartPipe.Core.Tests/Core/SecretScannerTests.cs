#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using SmartPipe.Core;
using System.Reflection;

namespace SmartPipe.Core.Tests.Core;

public class SecretScannerTests
{
    private const string EncodedSecretPayload = "api_key='secret123'";

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
    public void Scan_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SecretScanner.Scan(null!));
    }

    [Fact]
    public void Scan_StringWithSecret_ReturnsSecretFound()
    {
        var result = SecretScanner.Scan("api_key: 'secret123'");

        Assert.Equal(SecretScanResult.SecretFound, result);
    }

    [Fact]
    public void Scan_StringWithNoSecrets_ReturnsClean()
    {
        var result = SecretScanner.Scan("this is a clean string without secrets");

        Assert.Equal(SecretScanResult.Clean, result);
    }

    [Fact]
    public void Scan_CanonicalPaddedBase64Secret_ReturnsSecretFound()
    {
        var input = Convert.ToBase64String(Encoding.UTF8.GetBytes(EncodedSecretPayload));

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.SecretFound, result);
    }

    [Fact]
    public void Scan_CanonicalUnpaddedBase64Secret_ReturnsSecretFound()
    {
        var input = Convert.ToBase64String(Encoding.UTF8.GetBytes(EncodedSecretPayload)).TrimEnd('=');

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.SecretFound, result);
    }

    [Fact]
    public void Scan_Base64UrlPaddedSecret_ReturnsSecretFound()
    {
        var input = ToBase64Url(EncodedSecretPayload, padded: true);

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.SecretFound, result);
    }

    [Fact]
    public void Scan_Base64UrlUnpaddedSecret_ReturnsSecretFound()
    {
        var input = ToBase64Url(EncodedSecretPayload, padded: false);

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.SecretFound, result);
    }

    [Fact]
    public void Scan_CleanBase64Url_ReturnsClean()
    {
        var input = ToBase64Url("clean payload", padded: false);

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.Clean, result);
    }

    [Fact]
    public void Scan_Base64LengthModuloOne_DoesNotDecode()
    {
        var result = SecretScanner.Scan("abcde");

        Assert.Equal(SecretScanResult.Clean, result);
    }

    [Fact]
    public void Scan_Base64WithInvalidPadding_DoesNotDecode()
    {
        var result = SecretScanner.Scan("YWJj=ZA=");

        Assert.Equal(SecretScanResult.Clean, result);
    }

    [Fact]
    public void Scan_Base64WithMixedAlphabet_DoesNotDecode()
    {
        var result = SecretScanner.Scan("YWJj-ZA/");

        Assert.Equal(SecretScanResult.Clean, result);
    }

    [Fact]
    public void Scan_RegexTimeout_ReturnsIndeterminate()
    {
        var timeoutPattern = new Regex(
            "^(a+)+$",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(1));
        var input = new string('a', 100_000) + "!";

        var result = SecretScanner.ScanWithPatternsForTesting(input, [timeoutPattern]);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void Scan_InputLengthLimit_ReturnsIndeterminateBeforeScanning()
    {
        var input = new string('a', SecretScanner.MaxInputLength + 1);

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void Scan_DecodedLengthLimit_ReturnsIndeterminate()
    {
        var decoded = new string('a', SecretScanner.MaxDecodedBytes + 1);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded));

        var result = SecretScanner.Scan(encoded);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void Scan_RecursionLimitWithMoreEncodedContent_ReturnsIndeterminate()
    {
        var encoded = "clean";
        for (var i = 0; i <= SecretScanner.MaxRecursionDepth; i++)
            encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(encoded));

        var result = SecretScanner.Scan(encoded);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void Scan_NestedBase64UrlBeyondRecursionLimit_ReturnsIndeterminate()
    {
        var encoded = "clean";
        for (var i = 0; i <= SecretScanner.MaxRecursionDepth; i++)
            encoded = ToBase64Url(encoded, padded: false);

        var result = SecretScanner.Scan(encoded);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void Scan_Base64UrlDecodedPayloadBeyondBudget_ReturnsIndeterminate()
    {
        var decoded = new string('a', SecretScanner.MaxDecodedBytes + 1);
        var encoded = ToBase64Url(decoded, padded: false);

        var result = SecretScanner.Scan(encoded);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void HasSecrets_IndeterminateScan_ReturnsTrue()
    {
        var input = new string('a', SecretScanner.MaxInputLength + 1);

        var result = SecretScanner.HasSecrets(input);

        Assert.True(result);
    }

    [Fact]
    public void Redact_IndeterminateScan_ReturnsIndeterminateMarker()
    {
        var input = new string('a', SecretScanner.MaxInputLength + 1);

        var result = SecretScanner.Redact(input);

        Assert.Equal(SecretScanner.IndeterminateRedaction, result);
    }

    [Fact]
    public void Scan_FixedSeedFuzzInputs_DoesNotThrow()
    {
        var random = new Random(20260702);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_%:/?='\"-.";

        for (var i = 0; i < 200; i++)
        {
            var length = random.Next(0, 4096);
            var builder = new StringBuilder(length);
            for (var j = 0; j < length; j++)
                builder.Append(alphabet[random.Next(alphabet.Length)]);

            var result = SecretScanner.Scan(builder.ToString());

            Assert.True(Enum.IsDefined(result));
        }
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
    public void Scan_MalformedUrlEncodingAfterValidEscape_ReturnsIndeterminate()
    {
        var input = "prefix%20suffix%";

        var result = SecretScanner.Scan(input);

        Assert.Equal(SecretScanResult.Indeterminate, result);
    }

    [Fact]
    public void Redact_MalformedUrlEncodingAfterValidEscape_ReturnsIndeterminateMarker()
    {
        var input = "prefix%20suffix%";

        var result = SecretScanner.Redact(input);

        Assert.Equal(SecretScanner.IndeterminateRedaction, result);
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
    public void Redact_CanonicalUnpaddedSecret_PreservesUnpaddedRepresentation()
    {
        var input = Convert.ToBase64String(Encoding.UTF8.GetBytes(EncodedSecretPayload)).TrimEnd('=');

        var result = SecretScanner.Redact(input);
        var decoded = SecretScanner.DecodeBase64WithPadding(result);

        Assert.NotNull(decoded);
        Assert.Contains("***REDACTED***", decoded);
        Assert.DoesNotContain("secret123", decoded);
        Assert.False(result.EndsWith("=", StringComparison.Ordinal));
    }

    [Fact]
    public void Redact_Base64UrlSecret_PreservesUrlAlphabet()
    {
        var input = ToBase64Url(EncodedSecretPayload, padded: true);

        var result = SecretScanner.Redact(input);
        var decoded = SecretScanner.DecodeBase64WithPadding(result);

        Assert.NotNull(decoded);
        Assert.Contains("***REDACTED***", decoded);
        Assert.DoesNotContain("secret123", decoded);
        Assert.DoesNotContain("+", result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void Redact_Base64UrlUnpaddedSecret_PreservesUrlAlphabetAndPaddingStyle()
    {
        var input = ToBase64Url(EncodedSecretPayload, padded: false);

        var result = SecretScanner.Redact(input);
        var decoded = SecretScanner.DecodeBase64WithPadding(result);

        Assert.NotNull(decoded);
        Assert.Contains("***REDACTED***", decoded);
        Assert.DoesNotContain("secret123", decoded);
        Assert.DoesNotContain("+", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("=", result);
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
        var input = "abcde";

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

    private static string ToBase64Url(string value, bool padded)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .Replace('+', '-')
            .Replace('/', '_');
        return padded ? encoded : encoded.TrimEnd('=');
    }
}
