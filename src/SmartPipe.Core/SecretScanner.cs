using System.Text.RegularExpressions;

namespace SmartPipe.Core;

/// <summary>Detects secrets (API keys, passwords, private keys) in data.
/// Based on OWASP security best practices.</summary>
public static class SecretScanner
{
    private static readonly Regex[] Patterns =
    {
        new(@"api[_-]?key\s*[:=]\s*['""].+?['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"password\s*[:=]\s*['""].+?['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"sk-[a-zA-Z0-9]{32,}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"-----BEGIN\s(?:RSA|OPENSSH|DSA|EC)\sPRIVATE KEY-----", RegexOptions.Singleline | RegexOptions.Compiled)
    };

    /// <summary>Check if content contains secrets.</summary>
    /// <param name="content">Content to scan.</param>
    /// <returns>True if any secret pattern is detected.</returns>
    public static bool HasSecrets(string content)
    {
        foreach (var p in Patterns)
            if (p.IsMatch(content)) return true;
        return false;
    }

    /// <summary>Redact secrets from content.</summary>
    /// <param name="content">Content to redact.</param>
    /// <returns>Content with secrets replaced by ***REDACTED***.</returns>
    public static string Redact(string content)
    {
        foreach (var p in Patterns)
            content = p.Replace(content, "***REDACTED***");
        return content;
    }
}
