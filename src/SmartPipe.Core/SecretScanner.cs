#nullable enable
using System.Text;
using System.Text.RegularExpressions;

namespace SmartPipe.Core;

/// <summary>Detects secrets (API keys, passwords, private keys) in data.
/// Based on OWASP security best practices.</summary>
public static class SecretScanner
{
    // Max recursion depth of 3 allows detection of:
    // - Plain text secrets (depth 0)
    // - Single-encoded secrets: Base64 or URL-encoded (depth 1)
    // - Double-encoded secrets: Base64 within Base64, or URL then Base64 (depth 2)
    // Depth 3 provides safety margin for edge cases with multiple nested encodings.
    private const int MaxRecursionDepth = 3;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex[] Patterns =
    {
        CreateRegex(@"api[_-]?key\s*[:=]\s*['""].+?['""]", RegexOptions.IgnoreCase),
        CreateRegex(@"password\s*[:=]\s*['""].+?['""]", RegexOptions.IgnoreCase),
        CreateRegex(@"sk-[a-zA-Z0-9]{32,}", RegexOptions.IgnoreCase),
        CreateRegex(
            @"-----BEGIN\s(?:RSA|OPENSSH|DSA|EC)\sPRIVATE KEY-----",
            RegexOptions.Singleline
        ),
        CreateRegex(@"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"),
        CreateRegex(@"AKIA[0-9A-Z]{16}"),
        CreateRegex(@"ghp_[A-Za-z0-9]{36}"),
        CreateRegex(@"ya29\.[A-Za-z0-9_-]+"),
    };

    private static readonly Regex UrlEncodedPattern = CreateRegex(@"%[0-9A-Fa-f]{2}");

    private static Regex CreateRegex(string pattern, RegexOptions options = RegexOptions.None)
    {
        return new Regex(
            pattern,
            options | RegexOptions.Compiled | RegexOptions.CultureInvariant,
            RegexTimeout
        );
    }

    /// <summary>Check if content contains any secrets (API keys, passwords, private keys).</summary>
    /// <param name="content">String to scan.</param>
    /// <returns>True if a secret pattern is detected.</returns>
    public static bool HasSecrets(string content)
    {
        return HasSecretsInternal(content, 0);
    }

    private static bool HasSecretsInternal(string content, int depth)
    {
        if (CheckPatterns(content))
            return true;

        if (depth < MaxRecursionDepth && TryDecodeAndCheck(content, depth))
            return true;

        return false;
    }

    private static bool CheckPatterns(string content)
    {
        foreach (var p in Patterns)
        {
            try
            {
                if (p.IsMatch(content))
                    return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryDecodeAndCheck(string content, int depth)
    {
        if (TryDecodeBase64(content) is { } base64Decoded)
            if (HasSecretsInternal(base64Decoded, depth + 1))
                return true;

        if (TryDecodeUrl(content) is { } urlDecoded)
            if (HasSecretsInternal(urlDecoded, depth + 1))
                return true;

        return false;
    }

    /// <summary>Redact all detected secrets in content with ***REDACTED***.</summary>
    /// <param name="content">String to redact.</param>
    /// <returns>Content with secrets replaced.</returns>
    public static string Redact(string content)
    {
        return RedactInternal(content, 0);
    }

    private static string RedactInternal(string content, int depth)
    {
        content = ApplyPatternRedaction(content);

        if (depth < MaxRecursionDepth)
        {
            content = TryDecodeAndRedactBase64(content, depth);
            content = TryDecodeAndRedactUrl(content, depth);
        }

        return content;
    }

    private static string ApplyPatternRedaction(string content)
    {
        foreach (var p in Patterns)
        {
            try
            {
                content = p.Replace(content, "***REDACTED***");
            }
            catch (RegexMatchTimeoutException)
            {
                return content;
            }
        }
        return content;
    }

    private static string TryDecodeAndRedactBase64(string content, int depth)
    {
        if (TryDecodeBase64(content) is { } base64Decoded)
        {
            var redacted = RedactInternal(base64Decoded, depth + 1);
            if (redacted != base64Decoded)
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(redacted));
        }
        return content;
    }

    private static string TryDecodeAndRedactUrl(string content, int depth)
    {
        if (TryDecodeUrl(content) is { } urlDecoded)
        {
            var redacted = RedactInternal(urlDecoded, depth + 1);
            if (redacted != urlDecoded)
                return Uri.EscapeDataString(redacted);
        }
        return content;
    }

    internal static bool ValidateBase64Characters(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;
        return content.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
    }

    private static bool IsRawAwsAccessKey(string content)
    {
        // Raw AWS access keys (AKIA followed by 16 alphanumeric chars) are not Base64
        // They would pass the above checks but should not be decoded
        return content.StartsWith("AKIA") && content.Length == 20;
    }

    internal static string? DecodeBase64WithPadding(string content)
    {
        if (!ValidateBase64Characters(content))
            return null;
        var padded = EnsurePadding(content);
        return TryDecodeWithBuffer(padded);
    }

    private static string EnsurePadding(string content)
    {
        var paddingNeeded = (4 - (content.Length % 4)) % 4;
        return paddingNeeded > 0 ? content + new string('=', paddingNeeded) : content;
    }

    private static string? TryDecodeWithBuffer(string content)
    {
        var buffer = new byte[content.Length];
        if (Convert.TryFromBase64String(content, buffer, out var bytesWritten))
        {
            var decoded = Encoding.UTF8.GetString(buffer, 0, bytesWritten);
            if (!string.IsNullOrEmpty(decoded) && decoded != content)
                return decoded;
        }
        return null;
    }

    private static string? TryDecodeBase64(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        // Guard 1: Base64 strings must have length multiple of 4
        if (content.Length % 4 != 0)
            return null;

        // Guard 2: All characters must be valid Base64 characters
        if (!ValidateBase64Characters(content))
            return null;

        // Additional guard: raw AWS access keys
        if (IsRawAwsAccessKey(content))
            return null;

        return DecodeBase64WithPadding(content);
    }

    private static string? TryDecodeUrl(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        try
        {
            if (!UrlEncodedPattern.IsMatch(content))
                return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(content);
            if (!string.IsNullOrEmpty(decoded) && decoded != content)
                return decoded;
        }
        catch
        {
            // Invalid URL encoding
        }

        return null;
    }
}
