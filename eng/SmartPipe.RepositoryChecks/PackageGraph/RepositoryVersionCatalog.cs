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
            var groups = document.Root?.Elements().Where(element => element.Name.LocalName == "PropertyGroup") ?? [];
            var properties = groups.SelectMany(group => group.Elements())
                .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(element => element.Value.Trim()).Where(value => value.Length > 0).ToArray(), StringComparer.Ordinal);
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
        if (!properties.TryGetValue(name, out var values) || values.Length != 1)
            throw new InvalidDataException($"Version catalog must define exactly one {name}.");
        return values[0];
    }

    private static int CompareStable(string left, string right)
    {
        var leftParts = left.Split('.').Select(int.Parse).ToArray();
        var rightParts = right.Split('.').Select(int.Parse).ToArray();
        for (var index = 0; index < 3; index++)
        {
            var comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }
}
