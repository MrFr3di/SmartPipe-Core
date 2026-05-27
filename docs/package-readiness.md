# Package Readiness

SmartPipe.Core 1.1.0 package readiness requires evidence across build, API,
consumer, and security dimensions.

## Core Package

Required release checks:

- restore;
- Release build;
- Core tests;
- package validation;
- `dotnet pack -c Release`;
- public API baseline review;
- XML docs for changed public/protected APIs;
- Source Link and symbol package metadata where configured;
- consumer smoke test;
- trim/AOT smoke before any AOT-ready claim.

Core currently treats compiler/analyzer warnings as errors for the production
package project.

## Extensions Package

`SmartPipe.Extensions` stable `1.1.0` is blocked if preview dependencies remain.
The current 1.1.0 project file uses stable Microsoft 10.x package references;
if preview dependencies return, the package version must be `1.1.0-preview.*`.

Package splitting should wait until Core execution, envelope, observer, and
failure APIs are stable enough to avoid multiplying unstable public contracts.

## Consumer Smoke Test

The consumer matrix should cover:

- local package install;
- minimal pipeline compile/run;
- typed chain compile/run;
- background output;
- dead-letter serializer/redactor usage;
- trim publish;
- NativeAOT smoke where supported.
