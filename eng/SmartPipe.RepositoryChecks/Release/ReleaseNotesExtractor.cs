using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartPipe.RepositoryChecks.Release;

public sealed class ReleaseNotesException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static partial class ReleaseNotesExtractor
{
    [GeneratedRegex(@"^## \[(?<version>[^\]]+)\](?<suffix>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionHeadingRegex();

    [GeneratedRegex(@"^\[[^\]]+\]:\s+\S.*$", RegexOptions.CultureInvariant)]
    private static partial Regex LinkReferenceRegex();

    internal static string Extract(string changelog, string version)
    {
        ArgumentNullException.ThrowIfNull(changelog);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        _ = ReleaseVersionValidator.ParseTag($"v{version}");

        var lines = changelog
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var matches = lines
            .Select((line, index) => (Line: line, Index: index, Match: VersionHeadingRegex().Match(line)))
            .Where(item => item.Match.Success
                && string.Equals(item.Match.Groups["version"].Value, version, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new ReleaseNotesException(
                "SPRELNOTES001",
                $"CHANGELOG.md has no section for version {version}.");
        }

        if (matches.Length > 1)
        {
            throw new ReleaseNotesException(
                "SPRELNOTES003",
                $"CHANGELOG.md contains multiple sections for version {version}.");
        }

        var selected = matches[0];
        var suffix = selected.Match.Groups["suffix"].Value;
        if (!suffix.StartsWith(" - ", StringComparison.Ordinal)
            || !DateOnly.TryParseExact(
                suffix[3..],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new ReleaseNotesException(
                "SPRELNOTES002",
                $"CHANGELOG.md section for {version} must use a dated '## [{version}] - YYYY-MM-DD' heading.");
        }

        var end = lines.Length;
        for (var index = selected.Index + 1; index < lines.Length; index++)
        {
            if (VersionHeadingRegex().IsMatch(lines[index]))
            {
                end = index;
                break;
            }
        }

        var body = lines[(selected.Index + 1)..end]
            .Where(line => !LinkReferenceRegex().IsMatch(line))
            .ToList();

        while (body.Count > 0 && string.IsNullOrWhiteSpace(body[0]))
        {
            body.RemoveAt(0);
        }

        while (body.Count > 0 && string.IsNullOrWhiteSpace(body[^1]))
        {
            body.RemoveAt(body.Count - 1);
        }

        if (body.Count == 0 || body.All(string.IsNullOrWhiteSpace))
        {
            throw new ReleaseNotesException(
                "SPRELNOTES004",
                $"CHANGELOG.md section for {version} is empty.");
        }

        return string.Join("\n", body) + "\n";
    }
}
