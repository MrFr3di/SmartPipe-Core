using System.Text;

namespace SmartPipe.RepositoryChecks.Reporting;

internal static class CheckRunGitHubRenderer
{
    public static string Render(CheckRun run, bool failuresOnly = false)
    {
        var normalized = CheckRunNormalizer.Normalize(run);
        if (failuresOnly && normalized.Success)
        {
            normalized = normalized with { Diagnostics = [] };
        }

        var builder = new StringBuilder();
        if (!normalized.Success)
        {
            foreach (var diagnostic in normalized.Diagnostics)
            {
                builder.Append("::error");
                var properties = new List<string>(2);
                if (diagnostic.Path is not null)
                {
                    properties.Add("file=" + Escape(diagnostic.Path));
                }

                if (diagnostic.Line is not null)
                {
                    properties.Add("line=" + diagnostic.Line.Value);
                }

                if (properties.Count > 0)
                {
                    builder.Append(' ').Append(string.Join(',', properties));
                }

                var message = $"{diagnostic.Code}: {diagnostic.Summary}";
                if (diagnostic.EvidencePath is not null)
                {
                    message += $" [evidence: {diagnostic.EvidencePath}]";
                }

                builder.Append("::").Append(Escape(message)).Append('\n');
            }
        }

        var level = normalized.Success ? "notice" : "error";
        builder.Append("::").Append(level).Append(" title=").Append(Escape(normalized.Check))
            .Append("::").Append(Escape(Summary(normalized))).Append('\n');
        return builder.ToString();
    }

    internal static string Escape(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal)
        .Replace(":", "%3A", StringComparison.Ordinal)
        .Replace(",", "%2C", StringComparison.Ordinal);

    private static string Summary(CheckRun run) =>
        $"{(run.Profile is null ? run.Check : $"{run.Check} [{run.Profile}]")} {(run.Success ? "succeeded" : "failed")} (exit code {run.ExitCode})";
}
