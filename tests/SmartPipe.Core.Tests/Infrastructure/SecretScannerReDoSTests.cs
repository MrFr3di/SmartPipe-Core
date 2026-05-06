#nullable enable
using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace SmartPipe.Core.Tests.Infrastructure;

public class SecretScannerReDoSTests
{
    public static IEnumerable<object[]> ReDoSTestCases
    {
        get
        {
            // Pattern 1: API Key pattern - api[_-]?key\s*[:=]\s*['\"].+?['\"]
            // Malicious: long prefix match + extremely long value without closing quote
            // Forces the lazy .+? to scan entire string before failing
            yield return new object[] 
            { 
                "APIKey", 
                "api-key = '" + new string('x', 10000) 
            };

            // Pattern 2: Password pattern - password\s*[:=]\s*['\"].+?['\"]
            // Malicious: long prefix match + extremely long value without closing quote
            yield return new object[] 
            { 
                "Password", 
                "password = \"" + new string('p', 10000) 
            };

            // Pattern 3: OpenAI key - sk-[a-zA-Z0-9]{32,}
            // Malicious: prefix match with 31 chars (just below threshold) + non-matching suffix
            // Forces backtracking as engine tries different positions
            yield return new object[] 
            { 
                "OpenAIKey", 
                "sk-" + new string('a', 31) + "!" 
            };

            // Pattern 4: Private Key PEM - -----BEGIN\s(?:RSA|OPENSSH|DSA|EC)\sPRIVATE KEY-----
            // Malicious: long sequence of dashes and BEGIN without completing the pattern
            // The Singleline option doesn't create backtracking risk here, but we test anyway
            yield return new object[] 
            { 
                "PrivateKeyPEM", 
                "-----BEGIN" + new string('-', 1000) 
            };

            // Pattern 5: JWT - eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+
            // Malicious: valid first part, second part with many chars, no third part
            // Forces backtracking on the second and third dot-separated parts
            yield return new object[] 
            { 
                "JWT", 
                "eyJ" + new string('x', 1000) + ".eyJ" + new string('y', 1000) 
            };

            // Pattern 6: AWS Access Key - AKIA[0-9A-Z]{16}
            // Malicious: AKIA prefix + 15 alphanumeric chars (just below threshold) + non-matching
            yield return new object[] 
            { 
                "AWSAccessKey", 
                "AKIA" + new string('A', 15) + "!" 
            };

            // Pattern 7: GitHub PAT - ghp_[A-Za-z0-9]{36}
            // Malicious: ghp_ prefix + 35 alphanumeric chars (just below threshold) + non-matching
            yield return new object[] 
            { 
                "GitHubPAT", 
                "ghp_" + new string('g', 35) + "!" 
            };

            // Pattern 8: Google OAuth - ya29\.[A-Za-z0-9_-]+
            // Malicious: ya29. prefix + many valid chars, then non-matching to force scanning
            yield return new object[] 
            { 
                "GoogleOAuth", 
                "ya29." + new string('z', 10000) + "!" 
            };
        }
    }

    [Theory]
    [MemberData(nameof(ReDoSTestCases))]
    public void RegexPattern_ShouldNotExhibitCatastrophicBacktracking(string patternDescription, string maliciousInput)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100, 
            $"Pattern '{patternDescription}' should not exhibit ReDoS - took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void APIKeyPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "api-key = '" + new string('x', 5000);
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void PasswordPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "password = \"" + new string('p', 5000);
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void OpenAIKeyPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "sk-" + new string('a', 100) + "!";
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void PrivateKeyPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "-----BEGIN" + new string('-', 5000);
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void JWTPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "eyJ" + new string('x', 1000) + ".eyJ" + new string('y', 1000);
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void AWSAccessKeyPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "AKIA" + new string('A', 50) + "!";
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void GitHubPATPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "ghp_" + new string('g', 100) + "!";
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void GoogleOAuthPattern_ShouldNotBacktrack()
    {
        var maliciousInput = "ya29." + new string('z', 5000) + "!";
        var stopwatch = Stopwatch.StartNew();
        SecretScanner.HasSecrets(maliciousInput);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void ValidSecrets_ShouldStillBeDetected()
    {
        // API Key pattern
        SecretScanner.HasSecrets("api-key = 'abc123'").Should().BeTrue();
        SecretScanner.HasSecrets("api_key=\"xyz789\"").Should().BeTrue();

        // Password pattern
        SecretScanner.HasSecrets("password = 'secret123'").Should().BeTrue();
        SecretScanner.HasSecrets("password=\"mypass\"").Should().BeTrue();

        // OpenAI key
        SecretScanner.HasSecrets("sk-1234567890abcdef1234567890abcdef").Should().BeTrue();

        // Private Key PEM
        SecretScanner.HasSecrets("-----BEGIN RSA PRIVATE KEY-----").Should().BeTrue();
        SecretScanner.HasSecrets("-----BEGIN OPENSSH PRIVATE KEY-----").Should().BeTrue();

        // JWT
        SecretScanner.HasSecrets("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c").Should().BeTrue();

        // AWS Access Key
        SecretScanner.HasSecrets("AKIA1234567890ABCDEF").Should().BeTrue();

        // GitHub PAT
        SecretScanner.HasSecrets("ghp_123456789012345678901234567890123456").Should().BeTrue();

        // Google OAuth
        SecretScanner.HasSecrets("ya29.a0AfH6SMB").Should().BeTrue();
    }

    [Fact]
    public void NoSecrets_ShouldReturnFalse()
    {
        SecretScanner.HasSecrets("").Should().BeFalse();
        SecretScanner.HasSecrets("hello world").Should().BeFalse();
        SecretScanner.HasSecrets("no secrets here").Should().BeFalse();
    }
}
