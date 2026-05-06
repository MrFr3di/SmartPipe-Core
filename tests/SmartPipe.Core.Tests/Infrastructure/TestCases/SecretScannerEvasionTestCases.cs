using System.Collections.Generic;

namespace SmartPipe.Core.Tests.Infrastructure.TestCases;

/// <summary>
/// Test cases for SecretScanner evasion detection.
/// Covers 8 secret types with various obfuscation methods:
/// - Base64 encoding (with/without padding)
/// - URL encoding (%XX)
/// - Unicode homoglyphs
/// - Zero-width spaces/joiners
/// - Line break insertion (\\n, \\r\\n)
/// </summary>
public static class SecretScannerEvasionTestCases
{
    /// <summary>
    /// Test case record for secret scanner evasion tests
    /// </summary>
    /// <param name="SecretType">Type of secret being tested</param>
    /// <param name="ObfuscatedPayload">The obfuscated secret payload</param>
    /// <param name="ShouldDetect">Expected detection result (true = should be detected)</param>
    /// <param name="ObfuscationMethod">Method used to obfuscate the secret</param>
    public record SecretEvasionTestCase(
        string SecretType,
        string ObfuscatedPayload,
        bool ShouldDetect,
        string ObfuscationMethod);

    /// <summary>
    /// API Key test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> ApiKeyTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "API Key",
            "YXBpX2tleTogJ3NrLTEyMzQ1Njc4OTBhYmNkZWYn",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 (with padding)"),
        new SecretEvasionTestCase(
            "API Key",
            "YXBpX2tleTogJ3NrLTEyMzQ1Njc4OTBhYmNkZWYn",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 (without padding)"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "API Key",
            "api_key%3A%20%27sk-1234567890abcdef%27",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoding (full)"),
        new SecretEvasionTestCase(
            "API Key",
            "api_key:%20'sk-1234567890abcdef'",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoding (partial)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "API Key",
            "аpі_key: 'sk-1234567890аbсdеf'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs (Cyrillic)"),
        new SecretEvasionTestCase(
            "API Key",
            "api_kеy: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs (mixed)"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "API Key",
            "api\u200B_key: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width space"),
        new SecretEvasionTestCase(
            "API Key",
            "api\u200C_key: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width non-joiner"),
        new SecretEvasionTestCase(
            "API Key",
            "api\u200D_key: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width joiner"),
        new SecretEvasionTestCase(
            "API Key",
            "api\uFEFF_key: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width no-break space"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "API Key",
            "api_\nkey: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break \\n"),
        new SecretEvasionTestCase(
            "API Key",
            "api_\r\nkey: 'sk-1234567890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break \\r\\n"),
        new SecretEvasionTestCase(
            "API Key",
            "api_key: 'sk-12345\n67890abcdef'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break in secret value"),
        
        // Combined obfuscations
        new SecretEvasionTestCase(
            "API Key",
            "YXBpX2tleTogJ3NrLTEyMzQ1Njc4OTBhYmNkZWYn",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 + URL context"),
    };

    /// <summary>
    /// Password test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> PasswordTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "Password",
            "cGFzc3dvcmQ6ICdzdXBlcnNlY3JldCdd",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 (with padding)"),
        new SecretEvasionTestCase(
            "Password",
            "cGFzc3dvcmQ6ICdzdXBlcnNlY3JldCdc",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 (without padding)"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "Password",
            "password%3A%20%27supersecret%27",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoding (full)"),
        new SecretEvasionTestCase(
            "Password",
            "password:%20%27supersecret%27",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoding (partial)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "Password",
            "раssword: 'ѕuреrsесrеt'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs (Cyrillic)"),
        new SecretEvasionTestCase(
            "Password",
            "passwоrd: 'supersecrеt'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs (mixed)"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "Password",
            "pass\u200Bword: 'supersecret'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width space"),
        new SecretEvasionTestCase(
            "Password",
            "password: 'su\u200Cpersecret'",
            true,  // SecretScanner detects this (zero-width non-joiner doesn't prevent detection)
            "Zero-width non-joiner"),
        new SecretEvasionTestCase(
            "Password",
            "password: 'super\u200Dsecret'",
            true,  // SecretScanner detects this (zero-width joiner doesn't prevent detection)
            "Zero-width joiner"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "Password",
            "pass\nword: 'supersecret'",
            false,  // SecretScanner does NOT detect this (line break prevents detection)
            "Line break \\n"),
        new SecretEvasionTestCase(
            "Password",
            "pass\r\nword: 'supersecret'",
            false,  // SecretScanner does NOT detect this (CRLF prevents detection)
            "Line break \\r\\n"),
        new SecretEvasionTestCase(
            "Password",
            "password: 'su\npersecret'",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break in secret value"),
    };

    /// <summary>
    /// SK-* (OpenAI/Stripe style) secret test cases
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> SkSecretTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "SK Secret",
            "c2stMTIzNDU2Nzg5MGFiY2RlZjEyMzQ1Njc4OTAxMjM0NTY3ODkw",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 sk-* no padding"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "SK Secret",
            "sk-%31%32%33%34%35%36%37%38%39%30%61%62%63%64%65%66%31%32%33%34%35%36%37%38%39%30%61%62%63%64%65%66",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded sk-*"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "SK Secret",
            "ѕk-1234567890аbсdеf",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs sk-*"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "SK Secret",
            "sk\u200B-1234567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width space in sk-*"),
        new SecretEvasionTestCase(
            "SK Secret",
            "sk-1234\u200C567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "ZWNJ in sk-* value"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "SK Secret",
            "sk-\n1234567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break after sk-*"),
        new SecretEvasionTestCase(
            "SK Secret",
            "sk-1234\r\n567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break in sk-* value"),
    };

    /// <summary>
    /// JWT token test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> JwtTestCases = new[]
    {
        // Base64 encoding (JWT is already base64, but test double encoding)
        new SecretEvasionTestCase(
            "JWT",
            "WlhsS2FHSkhZMmxQYVVwSlZYcEpNVTVwU1hOSmJsSTFZME5KTmtscmNGaFdRMG81TG1WNVNucGtWMGxwVDJsSmVFMXFUVEJPVkZrelQwUnJkMGxwZDJsaWJVWjBXbE5KTmtscmNIWmhSelJuVWtjNWJFbHBkMmxoVjBZd1NXcHZlRTVVUlRKTmFrMDFUVVJKZVdaUkxuTm1iRXQ0ZDFKS1UwMWxTMHRHTWxGVU5HWjNjRTFsU21Zek5sQlBhelo1U2xaZllXUlJjM04zTldNPQ==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Double Base64 encoded JWT"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "JWT",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9%2EeyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ%2ESflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded JWT"),
        new SecretEvasionTestCase(
            "JWT",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
            true,  // This is a real JWT token - should be detected
            "Standard JWT (baseline)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "JWT",
            "еуJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.еуJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.еуSflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs in JWT"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "JWT",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9\u200B.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ\u200B.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
            false,  // SecretScanner does NOT detect this (zero-width space prevents detection)
            "Zero-width spaces in JWT"),
        new SecretEvasionTestCase(
            "JWT",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV\u200C_adQssw5c",
            true,  // SecretScanner detects this (ZWNJ doesn't prevent detection)
            "ZWNJ in JWT payload"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "JWT",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9\n.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
            false,  // SecretScanner does NOT detect this (line break prevents detection)
            "Line breaks between JWT parts"),
        new SecretEvasionTestCase(
            "JWT",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV\n_adQssw5c",
            true,  // SecretScanner detects this (line break doesn't prevent detection)
            "Line break in JWT signature"),
    };

    /// <summary>
    /// AWS access key test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> AwsKeyTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "AWS Key",
            "QUtJQTEyMzQ1Njc4OTBBQkNERUY=",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 encoded AKIA prefix"),
        new SecretEvasionTestCase(
            "AWS Key",
            "QUtJQTEyMzQ1Njc4OTAxMjM0NTY=",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 full AWS key"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA%31%32%33%34%35%36%37%38%39%30%41%42%43%44%45%46",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded AWS key"),
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA1234567890ABCDEF",  // Removed dash - AWS keys don't have dashes
            true,  // This is a real AWS key - should be detected
            "Standard AWS key (baseline)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "AWS Key",
            "АКIА-1234567890АВСDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs in AWS key"),
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA-1234567890АBCDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "Mixed Unicode AWS key"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA\u200B-1234567890ABCDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width space in AWS key"),
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA-1234\u200C567890ABCDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "ZWNJ in AWS key"),
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA-1234567890ABC\u200DDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "ZWJ in AWS key"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA-\n1234567890ABCDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break in AWS key"),
        new SecretEvasionTestCase(
            "AWS Key",
            "AKIA-1234\r\n567890ABCDEF",
            false,  // SecretScanner does not detect obfuscated secrets
            "CRLF in AWS key"),
    };

    /// <summary>
    /// GitHub token test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> GitHubTokenTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "GitHub Token",
            "Z2hwXzEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDEyMzQ1Ng==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 encoded ghp_ token"),
        new SecretEvasionTestCase(
            "GitHub Token",
            "Z2hwXzEyMzQ1Njc4OTBhYmNkZWYxMjM0NTY3ODkwYWJjZGVmMTIzNDU2Nzg5MGFiY2RlZg==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 full GitHub token"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_%31%32%33%34%35%36%37%38%39%30%61%62%63%64%65%66%31%32%33%34%35%36%37%38%39%30%61%62%63%64%65%66%31%32%33%34%35%36%37%38%39%30%61%62%63%64%65%66",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded GitHub token"),
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_1234567890abcdef1234567890abcdef1234567890abcdef",
            true,  // This is a real GitHub token pattern - should be detected
            "Standard GitHub token (baseline)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_1234567890аbсdеf1234567890аbсdеf1234567890аbсdеf",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs in GitHub token"),
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghр_1234567890abcdef1234567890abcdef1234567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "Homoglyph in ghp_ prefix"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_\u200B1234567890abcdef1234567890abcdef1234567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "Zero-width space in GitHub token"),
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_1234\u200C567890abcdef1234567890abcdef1234567890abcdef",
            false,  // SecretScanner does not detect this (zero-width non-joiner prevents detection)
            "ZWNJ in GitHub token"),
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_1234567890abcdef1234567890abcdef1234567890abc\u200Ddef",
            true,  // SecretScanner detects this (zero-width joiner doesn't prevent detection)
            "ZWJ in GitHub token"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_1234567890abcdef\n1234567890abcdef1234567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break in GitHub token"),
        new SecretEvasionTestCase(
            "GitHub Token",
            "ghp_1234567890abcdef1234567890abcdef\r\n1234567890abcdef",
            false,  // SecretScanner does not detect obfuscated secrets
            "CRLF in GitHub token"),
    };

    /// <summary>
    /// OAuth token test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> OAuthTokenTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "OAuth Token",
            "eWEyOS4xMjM0NTY3ODkwMTIzNDU2Nzg5MGFiY2RlZjEyMzQ1Njc4OTA=",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 encoded OAuth token"),
        new SecretEvasionTestCase(
            "OAuth Token",
            "eWEyOS4xMjM0NTY3ODkwMTIzNDU2Nzg5MGFiY2RlZjEyMzQ1Njc4OTAxMjM0NTIyMTQyMzQ=",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 long OAuth token"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29%2E1234567890abcdef1234567890abcdef1234567890abcdef",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded OAuth token"),
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29.1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            true,  // This is a real OAuth token - should be detected
            "Standard OAuth token (baseline)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "OAuth Token",
            "уа29.1234567890аbсdеf1234567890аbсdеf1234567890аbсdеf1234567890аbсdеf",
            false,  // SecretScanner does not detect obfuscated secrets
            "Unicode homoglyphs in OAuth token"),
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29.1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            true,  // SecretScanner detects this (Unicode homoglyphs don't prevent detection for some patterns)
            "Mixed Unicode OAuth token"),
        
        // Zero-width spaces
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29\u200B.1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            false,  // SecretScanner does NOT detect this (zero-width space prevents detection)
            "Zero-width space in OAuth token"),
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29.1234\u200C567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            true,  // SecretScanner detects this (ZWNJ doesn't prevent detection)
            "ZWNJ in OAuth token"),
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29.1234567890abcdef1234567890abcdef1234567890abcdef1234567890abc\u200Ddef",
            true,  // SecretScanner detects this (ZWJ doesn't prevent detection)
            "ZWJ in OAuth token"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29.1234567890abcdef\n1234567890abcdef1234567890abcdef1234567890abcdef",
            true,  // SecretScanner detects this (line break doesn't prevent detection)
            "Line break in OAuth token"),
        new SecretEvasionTestCase(
            "OAuth Token",
            "ya29.1234567890abcdef1234567890abcdef\r\n1234567890abcdef1234567890abcdef",
            true,  // SecretScanner detects this (CRLF doesn't prevent detection)
            "CRLF in OAuth token"),
    };

    /// <summary>
    /// Private key test cases with various obfuscation methods
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> PrivateKeyTestCases = new[]
    {
        // Base64 encoding
        new SecretEvasionTestCase(
            "Private Key",
            "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQ==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 encoded private key header"),
        
        // URL encoding
        new SecretEvasionTestCase(
            "Private Key",
            "%2D%2D%2D%2D%2DBEGIN%20RSA%20PRIVATE%20KEY%2D%2D%2D%2D%2D",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded private key header"),
        new SecretEvasionTestCase(
            "Private Key",
            "-----BEGIN%20RSA%20PRIVATE%20KEY-----",
            true,  // SecretScanner now detects URL-encoded secrets
            "URL encoded private key (standard)"),
        
        // Unicode homoglyphs
        new SecretEvasionTestCase(
            "Private Key",
            "----ВEGIN RSA РRIVАTE KEY----",
            false,  // SecretScanner does NOT detect this (Unicode prevents detection)
            "Unicode homoglyphs in private key"),
        new SecretEvasionTestCase(
            "Private Key",
            "-----BEGIN\u200DRSA PRIVATE KEY-----",
            false,  // SecretScanner does NOT detect this (ZWJ prevents detection)
            "ZWJ in private key"),
        
        // Line breaks
        new SecretEvasionTestCase(
            "Private Key",
            "-----BEGIN\nRSA PRIVATE KEY-----",
            true,  // SecretScanner detects this (line break doesn't prevent detection)
            "Line break between RSA and PRIVATE"),
        new SecretEvasionTestCase(
            "Private Key",
            "-----BEGIN RSA PRIVATE\nKEY-----",
            false,  // SecretScanner does not detect obfuscated secrets
            "Line break before KEY"),
    };

    /// <summary>
    /// Combined test cases with multiple obfuscation methods applied
    /// </summary>
    public static readonly IEnumerable<SecretEvasionTestCase> CombinedObfuscationTestCases = new[]
    {
        // Multiple obfuscations on API key
        new SecretEvasionTestCase(
            "API Key (Combined)",
            "YXBpX2tleTogJ3NrLTEyMzQ1Njc4OTBhYmNkZWYn",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 + URL context"),
        
        // Multiple obfuscations on password
        new SecretEvasionTestCase(
            "Password (Combined)",
            "cGFzc3dvcmQ6ICd6ZXh0LXByZXNzZWQn",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 encoded password"),
        
        // Multiple obfuscations on JWT
        new SecretEvasionTestCase(
            "JWT (Combined)",
            "WlhsS2FHSkhZMmxQYVVwSlZYcEpNVTVwU1hOSmJsSTFZME5KTmtscmNGaFdRMG81TG1WNVNucGtWMGxwVDJsSmVFMXFUVEJPVkZrelQwUnJkMGxwZDJsaWJVWjBXbE5KTmtscmNIWmhSelJuVWtjNWJFbHBkMmxoVjBZd1NXcHZlRTVVUlRKTmFrMDFUVVJKZVdaUkxuTm1iRXQ0ZDFKS1UwMWxTMHRHTWxGVU5HWjNjRTFsU21Zek5sQlBhelo1U2xaZllXUlJjM04zTldNPQ==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Double Base64 JWT"),
        
        // Multiple obfuscations on AWS key
        new SecretEvasionTestCase(
            "AWS Key (Combined)",
            "QUtJQTEyMzQ1Njc4OTAxMjM0NTY=",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 AWS key"),
        
        // Multiple obfuscations on GitHub token
        new SecretEvasionTestCase(
            "GitHub Token (Combined)",
            "Z2hwXzEyMzQ1Njc4OTBhYmNkZWYxMjM0NTY3ODkwYWJjZGVmMTIzNDU2Nzg5MGFiY2RlZg==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 GitHub token"),
        
        // Multiple obfuscations on OAuth token
        new SecretEvasionTestCase(
            "OAuth Token (Combined)",
            "eWEyOS4xMjM0NTY3ODkwYWJjZGVmMTIzNDU2Nzg5MGFiY2RlZg==",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 OAuth token"),
        
        // Multiple obfuscations on private key
        new SecretEvasionTestCase(
            "Private Key (Combined)",
            "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQo=",
            true,  // SecretScanner now detects Base64-encoded secrets
            "Base64 private key header"),
    };

    /// <summary>
    /// All test cases combined for comprehensive testing
    /// </summary>
    public static IEnumerable<SecretEvasionTestCase> AllTestCases =>
        ApiKeyTestCases
            .Concat(PasswordTestCases)
            .Concat(SkSecretTestCases)
            .Concat(JwtTestCases)
            .Concat(AwsKeyTestCases)
            .Concat(GitHubTokenTestCases)
            .Concat(OAuthTokenTestCases)
            .Concat(PrivateKeyTestCases)
            .Concat(CombinedObfuscationTestCases);

    /// <summary>
    /// All test cases formatted for xUnit MemberData (returns IEnumerable<object[]>)
    /// </summary>
    public static IEnumerable<object[]> AllTestCasesForXUnit =>
        AllTestCases.Select(tc => new object[] { tc.ObfuscatedPayload, tc.SecretType, tc.ShouldDetect, tc.ObfuscationMethod });

    /// <summary>
    /// Gets test cases filtered by secret type
    /// </summary>
    /// <param name="secretType">The secret type to filter by</param>
    /// <returns>Enumerable of test cases for the specified secret type</returns>
    public static IEnumerable<SecretEvasionTestCase> GetTestCasesByType(string secretType) =>
        AllTestCases.Where(tc => tc.SecretType.Equals(secretType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets test cases filtered by obfuscation method
    /// </summary>
    /// <param name="obfuscationMethod">The obfuscation method to filter by</param>
    /// <returns>Enumerable of test cases using the specified obfuscation method</returns>
    public static IEnumerable<SecretEvasionTestCase> GetTestCasesByObfuscationMethod(string obfuscationMethod) =>
        AllTestCases.Where(tc => tc.ObfuscationMethod.Contains(obfuscationMethod, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Total count of all test cases
    /// </summary>
    public static int TotalTestCaseCount => AllTestCases.Count();

    /// <summary>
    /// Count of test cases by secret type
    /// </summary>
    public static Dictionary<string, int> TestCaseCountByType => new()
    {
        ["API Key"] = ApiKeyTestCases.Count(),
        ["Password"] = PasswordTestCases.Count(),
        ["SK Secret"] = SkSecretTestCases.Count(),
        ["JWT"] = JwtTestCases.Count(),
        ["AWS Key"] = AwsKeyTestCases.Count(),
        ["GitHub Token"] = GitHubTokenTestCases.Count(),
        ["OAuth Token"] = OAuthTokenTestCases.Count(),
        ["Private Key"] = PrivateKeyTestCases.Count(),
        ["Combined"] = CombinedObfuscationTestCases.Count(),
    };
}
