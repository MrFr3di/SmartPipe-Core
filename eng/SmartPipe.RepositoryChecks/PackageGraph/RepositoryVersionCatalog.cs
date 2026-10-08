using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.PackageGraph;

internal sealed record RepositoryVersionCatalog(string VersionPrefix, string PreviousStableVersion)
{
    internal static RepositoryVersionCatalog Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "eng", "SmartPipe.Versions.props");
        try
        {
            using var reader = XmlReader.Create(path, RepositoryXml.CreateSettings());
            var document = XDocument.Load(reader, LoadOptions.None);
            // Keep this repository catalog deliberately static. MSBuild evaluates Condition and Import,
            // whereas an XML reader does not; accepting either could make release checks disagree with build.
            var projectRoot = document.Root;
            if (projectRoot is null || projectRoot.Name != "Project" || projectRoot.HasAttributes)
                throw new InvalidDataException("Version catalog must contain an unconditional Project root.");

            var groups = projectRoot.Elements().ToArray();
            if (groups.Length != 1 || groups[0].Name != "PropertyGroup" || groups[0].HasAttributes)
                throw new InvalidDataException("Version catalog must contain exactly one unconditional PropertyGroup.");

            var definitions = groups[0].Elements().ToArray();
            if (definitions.Length != 2 || definitions.Any(element =>
                    element.HasAttributes || element.HasElements
                    || element.Name != "SmartPipeVersionPrefix" && element.Name != "SmartPipePreviousStableVersion"))
                throw new InvalidDataException("Version catalog must contain only two unconditional version properties.");

            var properties = definitions
                .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(element => element.Value.Trim()).ToArray(), StringComparer.Ordinal);
            var current = RequiredSingle(properties, "SmartPipeVersionPrefix");
            var previous = RequiredSingle(properties, "SmartPipePreviousStableVersion");
            if (!IsCanonicalStableVersion(current) || !IsCanonicalStableVersion(previous))
                throw new InvalidDataException("SmartPipe version catalog values must be canonical stable SemVer cores.");
            if (CompareStable(previous, current) >= 0)
                throw new InvalidDataException("SmartPipePreviousStableVersion must be lower than SmartPipeVersionPrefix.");
            return new(current, previous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            throw new PackageGraphException("SPGRAPH019", $"Repository version catalog is invalid: {exception.Message}", exception);
        }
    }

    internal static bool IsCanonicalStableVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && parts.All(static part =>
            part.Length > 0
            && part.All(char.IsAsciiDigit)
            && (part.Length == 1 || part[0] != '0'));
    }

    private static string RequiredSingle(IReadOnlyDictionary<string, string[]> properties, string name)
    {
        if (!properties.TryGetValue(name, out var values) || values.Length != 1 || values[0].Length == 0)
            throw new InvalidDataException($"Version catalog must define exactly one non-empty {name}.");
        return values[0];
    }

    private static int CompareStable(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < 3; index++)
        {
            var lengthComparison = leftParts[index].Length.CompareTo(rightParts[index].Length);
            if (lengthComparison != 0)
                return lengthComparison;

            var ordinalComparison = string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (ordinalComparison != 0)
                return ordinalComparison;
        }

        return 0;
    }
}
