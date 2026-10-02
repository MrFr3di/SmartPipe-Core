# Security policy

## Supported versions

Support status is defined in [SUPPORT.md](SUPPORT.md). Do not infer security
support from package availability or the compatibility baseline.

The current `2.2.0` tree is a release train until publication; release-candidate
source is not labeled as a supported stable line merely because the version is
present in the repository.

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
