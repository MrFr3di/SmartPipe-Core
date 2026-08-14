using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Reporting;

namespace SmartPipe.RepositoryChecks.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(PackageGraphDocument))]
[JsonSerializable(typeof(PackageMetadataReport))]
[JsonSerializable(typeof(ScaffoldReport))]
[JsonSerializable(typeof(ConsumerScenarioDocument))]
[JsonSerializable(typeof(ConsumerScenarioResult))]
[JsonSerializable(typeof(PackagePackManifest))]
[JsonSerializable(typeof(CheckRun))]
[JsonSerializable(typeof(CheckDiagnostic))]
internal partial class RepositoryChecksJsonContext : JsonSerializerContext;
