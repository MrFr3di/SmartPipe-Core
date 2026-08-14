using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.Reporting;

namespace SmartPipe.RepositoryChecks.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CheckRun))]
[JsonSerializable(typeof(CheckDiagnostic))]
internal partial class CheckRunJsonContext : JsonSerializerContext;
