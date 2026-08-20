using System.Text.Json;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Reporting;

internal static class CheckRunJsonlRenderer
{
    public static string Render(CheckRun run, bool failuresOnly = false)
    {
        var normalized = CheckRunNormalizer.Normalize(run);
        if (failuresOnly && normalized.Success)
        {
            normalized = normalized with { Diagnostics = [] };
        }

        var json = JsonSerializer.Serialize(normalized, CheckRunJsonContext.Default.CheckRun);
        return json.TrimEnd('\r', '\n') + "\n";
    }
}
