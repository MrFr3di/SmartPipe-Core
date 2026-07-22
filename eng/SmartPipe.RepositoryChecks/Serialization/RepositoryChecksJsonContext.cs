using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Consumers;

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
internal partial class RepositoryChecksJsonContext : JsonSerializerContext;
