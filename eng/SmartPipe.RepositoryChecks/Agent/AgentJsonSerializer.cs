using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Agent;

internal static class AgentJsonSerializer
{
    public static string Serialize(AgentContext value) =>
        CanonicalJson.Serialize(value, AgentOutputJsonContext.Default.AgentContext);

    public static string Serialize(AgentEvidence value) =>
        CanonicalJson.Serialize(value, AgentOutputJsonContext.Default.AgentEvidence);
}
