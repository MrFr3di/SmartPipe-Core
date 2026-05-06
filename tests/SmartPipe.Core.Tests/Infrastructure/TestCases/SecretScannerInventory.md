# SecretScanner Inventory

**Class**: `SmartPipe.Core.SecretScanner`  
**Type**: `static class`  
**Location**: `src/SmartPipe.Core/SecretScanner.cs`

---

## Overview

`SecretScanner` is a static utility class that detects and redacts secrets (API keys, passwords, private keys) in string content. It uses a fixed set of compiled regular expressions based on OWASP security best practices. The class is synchronous only, with no dependencies on `ILogger`, `async/await`, or streams.

---

## Regex Patterns

| # | Pattern | RegexOptions | Secret Type | Description / Context |
|--|---------|--------------|-------------|----------------------|
| 1 | `api[_-]?key\s*[:=]\s*['\"].+?['\"]` | `IgnoreCase \| Compiled` | API Key (generic) | Matches `api-key` or `api_key` followed by `:` or `=` and a quoted value (single or double quotes). Case-insensitive. |
| 2 | `password\s*[:=]\s*['\"].+?['\"]` | `IgnoreCase \| Compiled` | Password (generic) | Matches `password` followed by `:` or `=` and a quoted value. Case-insensitive. |
| 3 | `sk-[a-zA-Z0-9]{32,}` | `IgnoreCase \| Compiled` | OpenAI API Key | Matches OpenAI secret keys starting with `sk-` followed by 32+ alphanumeric characters. Case-insensitive. |
| 4 | `-----BEGIN\s(?:RSA\|OPENSSH\|DSA\|EC)\sPRIVATE KEY-----` | `Singleline \| Compiled` | Private Key (PEM) | Matches PEM-encoded private key headers for RSA, OPENSSH, DSA, or EC keys. Uses `Singleline` mode so `.` matches newlines if needed across the header. |
| 5 | `eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+` | `Compiled` | JWT (JSON Web Token) | Matches JWT tokens that start with `eyJ` (typical base64-encoded JWT header) with three dot-separated parts. |
| 6 | `AKIA[0-9A-Z]{16}` | `Compiled` | AWS Access Key ID | Matches AWS access key IDs that start with `AKIA` followed by exactly 16 alphanumeric characters. |
| 7 | `ghp_[A-Za-z0-9]{36}` | `Compiled` | GitHub Personal Access Token | Matches GitHub personal access tokens starting with `ghp_` followed by exactly 36 alphanumeric characters. |
| 8 | `ya29\.[A-Za-z0-9_-]+` | `Compiled` | Google OAuth Token | Matches Google OAuth 2.0 access tokens starting with `ya29.` followed by alphanumeric characters, underscores, or hyphens. |

**Notes**:
- All patterns use `RegexOptions.Compiled` for performance.
- Patterns are stored in a static readonly array `Patterns` and evaluated in order.
- No capture groups are used; patterns are designed for detection and redaction.

---

## Public Methods

| Method Name | Parameters | Return Type | Description |
|-------------|------------|-------------|-------------|
| `HasSecrets` | `string content` | `bool` | Returns `true` if any of the regex patterns match the input content; otherwise `false`. Iterates through all patterns and short-circuits on first match. |
| `Redact` | `string content` | `string` | Returns a new string with all matched secrets replaced by `"***REDACTED***"`. Each pattern is applied sequentially to the content. |

**Signatures**:

```csharp
public static bool HasSecrets(string content)
public static string Redact(string content)
```

---

## Additional Notes

- **Static Class**: All members are static. No instance state.
- **Synchronous Only**: No `async` methods. No `Task` or `ValueTask` returns.
- **No Dependencies**: No `ILogger<T>`, no DI, no streams, no file I/O.
- **Thread Safety**: The class is thread-safe for read-only operations since `Regex` instances are immutable and `content` strings are not mutated in place (new strings are returned by `Redact`).
- **Performance**: Uses compiled regexes for faster repeated matching. Consider caching compiled patterns (already done via static readonly array).
