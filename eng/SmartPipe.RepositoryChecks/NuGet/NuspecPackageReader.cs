using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal sealed record NuspecPackageMetadata(
    string Id,
    string Version,
    IReadOnlyList<PackageDependencyGroupSnapshot> Groups);

internal static partial class NuspecPackageReader
{
    private static readonly HashSet<string> SupportedNamespaces = new(StringComparer.Ordinal)
    {
        string.Empty,
        "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2011/10/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2013/01/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd",
    };

    private static readonly HashSet<string> SemanticElementNames = new(StringComparer.Ordinal)
    {
        "metadata",
        "id",
        "version",
        "dependencies",
        "group",
        "dependency",
    };

    private static readonly HashSet<string> SupportedNetCoreAppFrameworks = new(StringComparer.Ordinal)
    {
        "netcoreapp1.0", "netcoreapp1.1",
        "netcoreapp2.0", "netcoreapp2.1", "netcoreapp2.2",
        "netcoreapp3.0", "netcoreapp3.1",
    };

    private static readonly HashSet<string> SupportedNetFrameworks = new(StringComparer.Ordinal)
    {
        "net10", "net11", "net20", "net30", "net35", "net40", "net403",
        "net45", "net451", "net452", "net46", "net461", "net462",
        "net47", "net471", "net472", "net48", "net481",
    };

    public static async Task<NuspecPackageMetadata> ReadAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = bytes.Length,
        };
        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var root = document.Root;
            if (root?.Name.LocalName != "package" || !SupportedNamespaces.Contains(root.Name.NamespaceName))
            {
                throw InvalidPackage("nuspec root namespace is unknown or the root element is not package");
            }

            var ns = root.Name.Namespace;
            if (root.Descendants().Any(element =>
                    SemanticElementNames.Contains(element.Name.LocalName)
                    && element.Name.Namespace != ns))
            {
                throw InvalidPackage("nuspec semantic elements must use the root namespace");
            }

            var metadataElements = root.Elements(ns + "metadata").ToArray();
            if (metadataElements.Length != 1)
            {
                throw InvalidPackage("nuspec must contain exactly one metadata element");
            }

            var metadata = metadataElements[0];
            var id = RequiredSingleValue(metadata, ns + "id");
            var version = RequiredSingleValue(metadata, ns + "version");
            var dependencies = metadata.Elements(ns + "dependencies").ToArray();
            if (dependencies.Length > 1)
            {
                throw InvalidPackage("nuspec contains multiple dependencies elements");
            }

            return new NuspecPackageMetadata(
                id,
                version,
                dependencies.Length == 0 ? [] : ReadDependencyGroups(dependencies[0], ns));
        }
        catch (RepositoryCheckException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            throw new RepositoryCheckException(
                ExitCodes.IntegrityOrSignatureFailure,
                "NuGet package nuspec is invalid or unsafe.",
                exception);
        }
    }

    internal static string CanonicalizeFramework(string? framework)
    {
        if (framework is null)
        {
            return string.Empty;
        }

        if (framework.Length == 0 || framework.Any(char.IsWhiteSpace))
        {
            throw InvalidPackage("nuspec target framework is empty or contains whitespace");
        }

        return framework.Contains(',', StringComparison.Ordinal)
            ? CanonicalizeLongFramework(framework)
            : CanonicalizeShortFramework(framework.ToLowerInvariant());
    }

    private static string RequiredSingleValue(XElement parent, XName name)
    {
        var elements = parent.Elements(name).ToArray();
        var value = elements.Length == 1 ? elements[0].Value.Trim() : null;
        if (string.IsNullOrEmpty(value))
        {
            throw InvalidPackage($"nuspec metadata must contain exactly one non-empty {name.LocalName}");
        }

        return value;
    }

    private static IReadOnlyList<PackageDependencyGroupSnapshot> ReadDependencyGroups(
        XElement dependencies,
        XNamespace ns)
    {
        var explicitGroups = dependencies.Elements(ns + "group").ToArray();
        var directDependencies = dependencies.Elements(ns + "dependency").ToArray();
        if (explicitGroups.Length > 0 && directDependencies.Length > 0)
        {
            throw InvalidPackage("nuspec mixes grouped and ungrouped dependencies");
        }

        IEnumerable<(string Framework, IEnumerable<XElement> Dependencies)> groups = explicitGroups.Length == 0
            ? directDependencies.Length == 0
                ? Array.Empty<(string Framework, IEnumerable<XElement> Dependencies)>()
                : [(string.Empty, (IEnumerable<XElement>)directDependencies)]
            : explicitGroups.Select(group =>
                (CanonicalizeFramework((string?)group.Attribute("targetFramework")), group.Elements(ns + "dependency")));
        var frameworkIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<PackageDependencyGroupSnapshot>();
        foreach (var group in groups)
        {
            if (!frameworkIdentities.Add(group.Framework))
            {
                throw InvalidPackage("nuspec contains duplicate dependency groups for the same target framework");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<PackageDependencyItemSnapshot>();
            foreach (var dependency in group.Dependencies)
            {
                var id = ((string?)dependency.Attribute("id"))?.Trim();
                var version = ((string?)dependency.Attribute("version"))?.Trim();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
                {
                    throw InvalidPackage("nuspec dependency ID and version are required");
                }

                if (!ids.Add(id))
                {
                    throw InvalidPackage("nuspec dependency group contains a duplicate package ID");
                }

                items.Add(new PackageDependencyItemSnapshot { Id = id, VersionRange = version });
            }

            items.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
            snapshots.Add(new PackageDependencyGroupSnapshot
            {
                TargetFramework = group.Framework,
                Dependencies = items,
            });
        }

        snapshots.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.TargetFramework, right.TargetFramework));
        return snapshots;
    }

    private static string CanonicalizeLongFramework(string framework)
    {
        FrameworkName parsed;
        try
        {
            parsed = new FrameworkName(framework);
        }
        catch (ArgumentException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.IntegrityOrSignatureFailure,
                "NuGet package failed integrity validation: nuspec target framework is invalid.",
                exception);
        }

        var profile = parsed.Profile.ToLowerInvariant();
        if (parsed.Identifier.Equals(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            var version = parsed.Version;
            var baseFramework = version.Major switch
            {
                >= 1 and <= 3 => $"netcoreapp{version.Major}.{version.Minor}",
                >= 5 => $"net{version.Major}.{version.Minor}",
                _ => throw InvalidPackage("nuspec .NETCoreApp version is unsupported"),
            };
            if (version.Build > 0
                || version.Revision > 0
                || baseFramework.StartsWith("netcoreapp", StringComparison.Ordinal)
                    && !SupportedNetCoreAppFrameworks.Contains(baseFramework))
            {
                throw InvalidPackage("nuspec .NETCoreApp version is unsupported");
            }

            return AppendPlatformProfile(baseFramework, profile);
        }

        if (parsed.Identifier.Equals(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.Length != 0
                || parsed.Version.Build > 0
                || parsed.Version.Revision > 0
                || !IsSupportedNetStandard(parsed.Version))
            {
                throw InvalidPackage("nuspec .NETStandard framework or profile is unsupported");
            }

            return $"netstandard{parsed.Version.Major}.{parsed.Version.Minor}";
        }

        if (parsed.Identifier.Equals(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            if (parsed.Version.Major is < 1 or > 4 || profile is not ("" or "client" or "full"))
            {
                throw InvalidPackage("nuspec .NETFramework version or profile is unsupported");
            }

            var digits = $"{parsed.Version.Major}{parsed.Version.Minor}"
                + (parsed.Version.Build > 0 ? parsed.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
                + (parsed.Version.Revision > 0 ? parsed.Version.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
            var baseFramework = "net" + digits;
            if (!SupportedNetFrameworks.Contains(baseFramework))
            {
                throw InvalidPackage("nuspec .NETFramework version is unsupported");
            }

            return baseFramework + (profile == "client" ? "-client" : string.Empty);
        }

        throw InvalidPackage("nuspec target framework identifier is unsupported");
    }

    private static string CanonicalizeShortFramework(string framework)
    {
        var match = ShortFrameworkRegex().Match(framework);
        if (!match.Success)
        {
            throw InvalidPackage("nuspec target framework does not use the supported canonical short grammar");
        }

        var baseFramework = match.Groups["base"].Value;
        var platform = match.Groups["platform"].Value;
        if (baseFramework.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            if (!SupportedNetCoreAppFrameworks.Contains(baseFramework))
            {
                throw InvalidPackage("nuspec netcoreapp short framework version is unsupported");
            }
        }
        else if (baseFramework.StartsWith("netstandard", StringComparison.Ordinal)
            && !IsSupportedNetStandard(new Version(
                int.Parse(match.Groups["standardmajor"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["standardminor"].Value, System.Globalization.CultureInfo.InvariantCulture))))
        {
            throw InvalidPackage("nuspec netstandard short framework version is unsupported");
        }

        if (!baseFramework.Contains('.', StringComparison.Ordinal)
            && !SupportedNetFrameworks.Contains(baseFramework))
        {
            throw InvalidPackage("nuspec .NETFramework short version is unsupported");
        }

        if (platform.Length != 0 && !baseFramework.Contains('.', StringComparison.Ordinal))
        {
            if (platform != "client")
            {
                throw InvalidPackage("nuspec .NETFramework short profile is unsupported");
            }
        }
        else if (platform.Length != 0 && !PlatformProfileRegex().IsMatch(platform))
        {
            throw InvalidPackage("nuspec short framework platform is unsupported");
        }

        return framework;
    }

    private static string AppendPlatformProfile(string baseFramework, string profile)
    {
        if (profile.Length == 0)
        {
            return baseFramework;
        }

        if (!PlatformProfileRegex().IsMatch(profile))
        {
            throw InvalidPackage("nuspec .NETCoreApp platform profile is unsupported");
        }

        return $"{baseFramework}-{profile}";
    }

    private static bool IsSupportedNetStandard(Version version)
    {
        return version.Major == 1 && version.Minor is >= 0 and <= 6
            || version.Major == 2 && version.Minor is >= 0 and <= 1;
    }

    [GeneratedRegex("^(?<base>(?:netcoreapp(?<coremajor>[1-9]\\d*)\\.\\d+)|(?:netstandard(?<standardmajor>[1-9]\\d*)\\.(?<standardminor>\\d+))|(?:net(?:[1-4]\\d{1,2}|(?:[5-9]|[1-9]\\d+)\\.\\d+)))(?:-(?<platform>[a-z][a-z0-9]*(?:\\d+(?:\\.\\d+){0,3})?))?$")]
    private static partial Regex ShortFrameworkRegex();

    [GeneratedRegex("^(?:windows|android|ios|maccatalyst|macos|tvos|browser|linux)(?:\\d+(?:\\.\\d+){0,3})?$")]
    private static partial Regex PlatformProfileRegex();

    private static RepositoryCheckException InvalidPackage(string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet package failed integrity validation: {detail}.");
    }
}
