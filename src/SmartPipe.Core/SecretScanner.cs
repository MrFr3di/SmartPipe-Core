#nullable enable
using System.Text;
using System.Text.RegularExpressions;

namespace SmartPipe.Core;

/// <summary>Result of scanning content for secrets.</summary>
public enum SecretScanResult
{
    /// <summary>No secret was detected and scanning completed.</summary>
    Clean,

    /// <summary>A secret pattern was detected.</summary>
    SecretFound,

    /// <summary>Scanning could not complete safely, so callers should fail closed.</summary>
    Indeterminate,
}

/// <summary>Detects secrets (API keys, passwords, private keys) in data.
/// Based on OWASP security best practices.</summary>
public static partial class SecretScanner
{
    // Max recursion depth of 3 allows detection of:
    // - Plain text secrets (depth 0)
    // - Single-encoded secrets: Base64 or URL-encoded (depth 1)
    // - Double-encoded secrets: Base64 within Base64, or URL then Base64 (depth 2)
    // Depth 3 provides safety margin for edge cases with multiple nested encodings.
    internal const int MaxRecursionDepth = 3;
    internal const int MaxInputLength = 16 * 1024 * 1024;
    internal const int MaxDecodedBytes = 4 * 1024 * 1024;
    internal const string IndeterminateRedaction = "***REDACTION_INDETERMINATE***";

    private const int RegexTimeoutMilliseconds = 250;

    private static readonly Regex[] Patterns =
    [
        ApiKeyRegex(),
        PasswordRegex(),
        OpenAiKeyRegex(),
        PrivateKeyRegex(),
        JwtRegex(),
        AwsAccessKeyRegex(),
        GitHubPatRegex(),
        GoogleOAuthRegex(),
    ];

    private static readonly Regex UrlEncodedPattern = UrlEncodedOctetRegex();

    /// <summary>Scan content for secrets with fail-closed indeterminate results.</summary>
    /// <param name="content">String to scan.</param>
    /// <returns>The scan result.</returns>
    public static SecretScanResult Scan(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ScanInternal(content, depth: 0, new ScanBudget(), Patterns);
    }

    /// <summary>Check if content contains any secrets (API keys, passwords, private keys).</summary>
    /// <param name="content">String to scan.</param>
    /// <returns>True if a secret is detected or scanning is indeterminate.</returns>
    public static bool HasSecrets(string content)
    {
        return Scan(content) != SecretScanResult.Clean;
    }

    /// <summary>Redact all detected secrets in content with ***REDACTED***.</summary>
    /// <param name="content">String to redact.</param>
    /// <returns>Content with secrets replaced, or an indeterminate marker when redaction cannot complete safely.</returns>
    public static string Redact(string content)
    {
        var scanResult = Scan(content);
        return scanResult switch
        {
            SecretScanResult.Clean => content,
            SecretScanResult.Indeterminate => IndeterminateRedaction,
            _ => RedactInternal(content, depth: 0, new ScanBudget()).Value,
        };
    }

    internal static SecretScanResult ScanWithPatternsForTesting(string content, IReadOnlyList<Regex> patterns)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(patterns);
        return ScanInternal(content, depth: 0, new ScanBudget(), patterns);
    }

    private static SecretScanResult ScanInternal(
        string content,
        int depth,
        ScanBudget budget,
        IReadOnlyList<Regex> patterns)
    {
        if (content.Length > MaxInputLength)
            return SecretScanResult.Indeterminate;

        var patternResult = CheckPatterns(content, patterns);
        if (patternResult != SecretScanResult.Clean)
            return patternResult;

        if (depth >= MaxRecursionDepth)
            return HasPotentialEncodedContent(content) ? SecretScanResult.Indeterminate : SecretScanResult.Clean;

        var base64Result = TryDecodeBase64ForScan(content, budget);
        if (base64Result.Status == DecodeStatus.Indeterminate)
            return SecretScanResult.Indeterminate;
        if (base64Result.Status == DecodeStatus.Decoded)
        {
            var nestedResult = ScanInternal(base64Result.Value!, depth + 1, budget, patterns);
            if (nestedResult != SecretScanResult.Clean)
                return nestedResult;
        }

        var urlResult = TryDecodeUrlForScan(content, budget);
        if (urlResult.Status == DecodeStatus.Indeterminate)
            return SecretScanResult.Indeterminate;
        if (urlResult.Status == DecodeStatus.Decoded)
        {
            var nestedResult = ScanInternal(urlResult.Value!, depth + 1, budget, patterns);
            if (nestedResult != SecretScanResult.Clean)
                return nestedResult;
        }

        return SecretScanResult.Clean;
    }

    private static SecretScanResult CheckPatterns(string content, IReadOnlyList<Regex> patterns)
    {
        foreach (var p in patterns)
        {
            try
            {
                if (p.IsMatch(content))
                    return SecretScanResult.SecretFound;
            }
            catch (RegexMatchTimeoutException)
            {
                return SecretScanResult.Indeterminate;
            }
        }
        return SecretScanResult.Clean;
    }

    private static bool HasPotentialEncodedContent(string content)
    {
        if (LooksLikeBase64(content))
            return true;

        try
        {
            return UrlEncodedPattern.IsMatch(content);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static RedactionResult RedactInternal(string content, int depth, ScanBudget budget)
    {
        if (content.Length > MaxInputLength)
            return RedactionResult.Indeterminate();

        var redaction = ApplyPatternRedaction(content);
        if (redaction.IsIndeterminate)
            return redaction;

        content = redaction.Value;

        if (depth >= MaxRecursionDepth)
            return HasPotentialEncodedContent(content)
                ? RedactionResult.Indeterminate()
                : RedactionResult.Complete(content);

        var base64Result = TryDecodeBase64ForScan(content, budget);
        if (base64Result.Status == DecodeStatus.Indeterminate)
            return RedactionResult.Indeterminate();
        if (base64Result.Status == DecodeStatus.Decoded)
        {
            var nested = RedactInternal(base64Result.Value!, depth + 1, budget);
            if (nested.IsIndeterminate)
                return nested;
            if (nested.Value != base64Result.Value)
                return RedactionResult.Complete(Convert.ToBase64String(Encoding.UTF8.GetBytes(nested.Value)));
        }

        var urlResult = TryDecodeUrlForScan(content, budget);
        if (urlResult.Status == DecodeStatus.Indeterminate)
            return RedactionResult.Indeterminate();
        if (urlResult.Status == DecodeStatus.Decoded)
        {
            var nested = RedactInternal(urlResult.Value!, depth + 1, budget);
            if (nested.IsIndeterminate)
                return nested;
            if (nested.Value != urlResult.Value)
                return RedactionResult.Complete(Uri.EscapeDataString(nested.Value));
        }

        return RedactionResult.Complete(content);
    }

    private static RedactionResult ApplyPatternRedaction(string content)
    {
        foreach (var p in Patterns)
        {
            try
            {
                content = p.Replace(content, "***REDACTED***");
            }
            catch (RegexMatchTimeoutException)
            {
                return RedactionResult.Indeterminate();
            }
        }
        return RedactionResult.Complete(content);
    }

    internal static bool ValidateBase64Characters(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;
        return content.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
    }

    private static bool IsRawAwsAccessKey(string content)
    {
        // Raw AWS access keys (AKIA followed by 16 alphanumeric chars) are not Base64.
        return content.StartsWith("AKIA") && content.Length == 20;
    }

    internal static string? DecodeBase64WithPadding(string content)
    {
        if (!ValidateBase64Characters(content))
            return null;
        var padded = EnsurePadding(content);
        return TryDecodeWithBuffer(padded, budget: null).Value;
    }

    private static string EnsurePadding(string content)
    {
        var paddingNeeded = (4 - (content.Length % 4)) % 4;
        return paddingNeeded > 0 ? content + new string('=', paddingNeeded) : content;
    }

    private static DecodeResult TryDecodeWithBuffer(string content, ScanBudget? budget)
    {
        var estimatedDecodedBytes = GetBase64DecodedByteEstimate(content);
        if (budget is not null && !budget.TryReserve(estimatedDecodedBytes))
            return DecodeResult.Indeterminate();

        var buffer = new byte[content.Length];
        if (Convert.TryFromBase64String(content, buffer, out var bytesWritten))
        {
            var decoded = Encoding.UTF8.GetString(buffer, 0, bytesWritten);
            if (decoded.Length > MaxInputLength)
                return DecodeResult.Indeterminate();
            if (!string.IsNullOrEmpty(decoded) && decoded != content)
                return DecodeResult.Decoded(decoded);
        }
        return DecodeResult.NotDecoded();
    }

    private static int GetBase64DecodedByteEstimate(string content)
    {
        var padding = 0;
        if (content.EndsWith("==", StringComparison.Ordinal))
            padding = 2;
        else if (content.EndsWith("=", StringComparison.Ordinal))
            padding = 1;

        return checked((content.Length / 4 * 3) - padding);
    }

    private static string? TryDecodeBase64(string content)
    {
        var result = TryDecodeBase64ForScan(content, new ScanBudget());
        return result.Status == DecodeStatus.Decoded ? result.Value : null;
    }

    private static DecodeResult TryDecodeBase64ForScan(string content, ScanBudget budget)
    {
        if (!LooksLikeBase64(content))
            return DecodeResult.NotDecoded();

        return DecodeBase64WithPaddingForScan(content, budget);
    }

    private static bool LooksLikeBase64(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        // Guard 1: Base64 strings must have length multiple of 4 for recursive scanning.
        if (content.Length % 4 != 0)
            return false;

        // Guard 2: All characters must be valid Base64 characters.
        if (!ValidateBase64Characters(content))
            return false;

        // Additional guard: raw AWS access keys.
        return !IsRawAwsAccessKey(content);
    }

    private static DecodeResult DecodeBase64WithPaddingForScan(string content, ScanBudget budget)
    {
        var padded = EnsurePadding(content);
        return TryDecodeWithBuffer(padded, budget);
    }

    private static string? TryDecodeUrl(string content)
    {
        var result = TryDecodeUrlForScan(content, new ScanBudget());
        return result.Status == DecodeStatus.Decoded ? result.Value : null;
    }

    private static DecodeResult TryDecodeUrlForScan(string content, ScanBudget budget)
    {
        if (string.IsNullOrEmpty(content))
            return DecodeResult.NotDecoded();

        try
        {
            if (!UrlEncodedPattern.IsMatch(content))
                return DecodeResult.NotDecoded();
        }
        catch (RegexMatchTimeoutException)
        {
            return DecodeResult.Indeterminate();
        }

        if (!budget.HasCapacityFor(content.Length))
            return DecodeResult.Indeterminate();

        try
        {
            var decoded = Uri.UnescapeDataString(content);
            var decodedBytes = Encoding.UTF8.GetByteCount(decoded);
            if (!budget.TryReserve(decodedBytes))
                return DecodeResult.Indeterminate();
            if (decoded.Length > MaxInputLength)
                return DecodeResult.Indeterminate();
            if (!string.IsNullOrEmpty(decoded) && decoded != content)
                return DecodeResult.Decoded(decoded);
        }
        catch
        {
            // Invalid URL encoding.
        }

        return DecodeResult.NotDecoded();
    }

    [GeneratedRegex(
        @"api[_-]?key\s*[:=]\s*['""].+?['""]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(
        @"password\s*[:=]\s*['""].+?['""]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(
        @"sk-[a-zA-Z0-9]{32,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(
        @"-----BEGIN\s(?:RSA|OPENSSH|DSA|EC)\sPRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"AKIA[0-9A-Z]{16}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(
        @"ghp_[A-Za-z0-9]{36}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex GitHubPatRegex();

    [GeneratedRegex(
        @"ya29\.[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex GoogleOAuthRegex();

    [GeneratedRegex(
        @"%[0-9A-Fa-f]{2}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex UrlEncodedOctetRegex();

    private enum DecodeStatus
    {
        NotDecoded,
        Decoded,
        Indeterminate,
    }

    private readonly record struct DecodeResult(DecodeStatus Status, string? Value)
    {
        public static DecodeResult NotDecoded() => new(DecodeStatus.NotDecoded, null);

        public static DecodeResult Decoded(string value) => new(DecodeStatus.Decoded, value);

        public static DecodeResult Indeterminate() => new(DecodeStatus.Indeterminate, null);
    }

    private readonly record struct RedactionResult(string Value, bool IsIndeterminate)
    {
        public static RedactionResult Complete(string value) => new(value, IsIndeterminate: false);

        public static RedactionResult Indeterminate() => new(IndeterminateRedaction, IsIndeterminate: true);
    }

    private sealed class ScanBudget
    {
        private int _decodedBytes;

        public bool HasCapacityFor(int decodedBytes) => decodedBytes <= MaxDecodedBytes - _decodedBytes;

        public bool TryReserve(int decodedBytes)
        {
            if (decodedBytes < 0 || decodedBytes > MaxDecodedBytes - _decodedBytes)
                return false;

            _decodedBytes += decodedBytes;
            return true;
        }
    }
}
