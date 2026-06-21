# Security Policy

## Supported Versions

| Version | Supported |
|---|---|
| 1.1.x | Active |
| 1.0.x | Maintenance |

## Reporting A Vulnerability

Please report vulnerabilities privately through the repository security advisory
workflow when available, or contact the maintainers before publishing details.
Include affected package versions, reproduction steps, and impact.

## Secret Scanning

SmartPipe includes an opt-in `SecretScanner` feature flag for legacy pipelines.
It is disabled by default and should be enabled only when its behavior is
appropriate for the application data path.
