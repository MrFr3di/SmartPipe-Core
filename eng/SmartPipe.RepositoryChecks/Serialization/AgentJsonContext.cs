using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.Agent;

namespace SmartPipe.RepositoryChecks.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentContext))]
[JsonSerializable(typeof(AgentPrerequisiteStatus))]
[JsonSerializable(typeof(AgentReadSlice))]
[JsonSerializable(typeof(AgentEvidence))]
[JsonSerializable(typeof(AgentEvidenceCheck))]
internal partial class AgentOutputJsonContext : JsonSerializerContext;
