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
        new(@"-----BEGIN\s(?:RSA|OPENSSH|DSA|EC)\sPRIVATE KEY-----", RegexOptions.Singleline | RegexOptions.Compiled),
        new(@"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled),
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),
        new(@"ghp_[A-Za-z0-9]{36}", RegexOptions.Compiled),
        new(@"ya29\.[A-Za-z0-9_-]+", RegexOptions.Compiled),
    };

    public static bool HasSecrets(string content)
    {
        foreach (var p in Patterns)
            if (p.IsMatch(content)) return true;
        return false;
    }

    public static string Redact(string content)
    {
        foreach (var p in Patterns)
            content = p.Replace(content, "***REDACTED***");
        return content;
    }
}
