# Security policy

## Supported versions

Support status is defined in [SUPPORT.md](https://github.com/MrFr3di/SmartPipe-Core/blob/main/SUPPORT.md). Do not infer security
support from package availability or the compatibility baseline.

`2.2.x` is the current stable release line. `2.2.0` was published on
2026-10-07, and security fixes for this line are handled as PATCH servicing
changes unless an incompatible contract change requires a different release.

## Reporting a vulnerability

Do not open a public issue with exploit details.

Use GitHub private vulnerability reporting/security advisories when available.
If private reporting is unavailable, contact the maintainers before publishing
technical details.

Include, when possible:

- affected SmartPipe package IDs and versions;
- affected runtime/framework and operating system;
- a minimal reproduction or proof of concept;
- realistic impact and preconditions;
- whether credentials, data integrity, availability, provenance, parser bounds,
  or resource exhaustion are involved;
- any known workaround;
- logs or artifacts with secrets removed.

## Scope

Security reports are appropriate for issues such as:

- credential or sensitive-data exposure caused by SmartPipe;
- unsafe parser/resource-bound behavior;
- package/provenance or release-integrity bypass;
- denial of service caused by an implementation defect inside SmartPipe's
  documented ownership boundary;
- incorrect ownership/lifetime behavior that creates a security impact.

Ordinary correctness bugs that do not require coordinated disclosure should use
the normal issue flow.

## Product security features

Product features such as `SecretScanner` are runtime APIs, not part of the
repository disclosure policy. Their behavior belongs in API/reference
documentation and executable tests.
