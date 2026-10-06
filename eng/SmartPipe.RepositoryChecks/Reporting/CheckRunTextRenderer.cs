using System.Text;

namespace SmartPipe.RepositoryChecks.Reporting;

internal static class CheckRunTextRenderer
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
                builder.Append('[').Append(diagnostic.Code).Append("] ").Append(diagnostic.Summary);
                if (diagnostic.Path is not null)
                {
                    builder.Append(" (").Append(diagnostic.Path);
                    if (diagnostic.Line is not null)
                    {
                        builder.Append(':').Append(diagnostic.Line.Value);
                    }

                    builder.Append(')');
                }

                if (diagnostic.EvidencePath is not null)
                {
                    builder.Append(" [evidence: ").Append(diagnostic.EvidencePath).Append(']');
                }

                builder.Append('\n');
            }
        }

        builder.Append("summary: ").Append(Identity(normalized)).Append(' ')
            .Append(normalized.Success ? "succeeded" : "failed")
            .Append(" (exit code ").Append(normalized.ExitCode).Append(")\n");
        return builder.ToString();
    }

    private static string Identity(CheckRun run) =>
        run.Profile is null ? run.Check : $"{run.Check} [{run.Profile}]";
}
