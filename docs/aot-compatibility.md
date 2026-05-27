# AOT And Trimming Compatibility

SmartPipe.Core 1.1.0 uses an evidence-first AOT posture.

## Core

Do not set `IsAotCompatible=true` until a consumer publish harness completes
without actionable IL warnings.

Before making a public compatibility claim, Core may enable analyzer properties:

```xml
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
```

These analyzer properties are not the same as a public AOT-ready claim.

Current local analyzer status:

- Core builds with trim/AOT analyzers enabled and no analyzer warnings after
  moving to stable `Microsoft.Extensions.Logging.Abstractions` 10.x.
- Core still must not claim AOT compatibility until a consumer publish harness
  verifies trimmed and NativeAOT scenarios.

## Extensions

`SmartPipe.Extensions` must maintain an explicit AOT status matrix by integration
area. Reflection-heavy or runtime-codegen integrations such as object mappers,
ORMs, and serializers may be partial or unsupported unless configured with
source-generated metadata or package-specific AOT support.

If preview dependencies return, `SmartPipe.Extensions` must use a preview package
version instead of stable `1.1.0`. The current 1.1.0 project file uses stable
Microsoft 10.x package references.
