using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal sealed record NuGetPackageReaderOptions
{
    public int MaxEntryCount { get; init; } = 4096;

    public long MaxEntryUncompressedBytes { get; init; } = 64 * 1024 * 1024;

    public long MaxTotalUncompressedBytes { get; init; } = 512 * 1024 * 1024;

    public double MaxCompressionRatio { get; init; } = 1000;
}

internal sealed class NuGetPackageReader
{
    private readonly NuGetPackageReaderOptions _options;

    public NuGetPackageReader(NuGetPackageReaderOptions? options = null)
    {
        _options = options ?? new NuGetPackageReaderOptions();
        if (_options.MaxEntryCount <= 0
            || _options.MaxEntryUncompressedBytes <= 0
            || _options.MaxEntryUncompressedBytes > int.MaxValue
            || _options.MaxTotalUncompressedBytes <= 0
            || _options.MaxCompressionRatio < 1
            || !double.IsFinite(_options.MaxCompressionRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "NuGet archive safety limits must be positive and finite.");
        }
    }

    public Task<NuGetPackageSnapshot> ReadAsync(string packagePath, CancellationToken cancellationToken)
    {
        return ReadCoreAsync(packagePath, expectedPackageId: null, expectedVersion: null, cancellationToken);
    }

    public Task<NuGetPackageSnapshot> ReadAsync(
        string packagePath,
        string expectedPackageId,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        return ReadCoreAsync(packagePath, expectedPackageId, expectedVersion, cancellationToken);
    }

    private async Task<NuGetPackageSnapshot> ReadCoreAsync(
        string packagePath,
        string? expectedPackageId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        try
        {
            await using var stream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = ValidateEntries(archive);
            var nuspecEntries = entries
                .Where(static entry =>
                    !entry.Path.Contains('/')
                    && entry.Path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nuspecEntries.Length != 1)
            {
                throw InvalidPackage("package must contain exactly one root nuspec");
            }

            var files = new List<PackageFileSnapshot>(entries.Count);
            var assemblies = new List<PackageAssemblySnapshot>();
            byte[]? nuspecBytes = null;
            foreach (var entry in entries.OrderBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await ReadEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                files.Add(new PackageFileSnapshot
                {
                    Path = entry.Path,
                    UncompressedLength = entry.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    Category = Categorize(entry.Path),
                });

                if (ReferenceEquals(entry.Entry, nuspecEntries[0].Entry))
                {
                    nuspecBytes = bytes;
                }

                if (TryGetManagedAsset(entry.Path, out var targetFramework))
                {
                    assemblies.Add(ReadAssembly(entry.Path, targetFramework, bytes));
                }
            }

            var (id, version, dependencyGroups) = await ReadNuspecAsync(
                nuspecBytes ?? throw InvalidPackage("root nuspec could not be read"),
                cancellationToken).ConfigureAwait(false);
            if (expectedPackageId is not null
                && (!string.Equals(id, expectedPackageId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidPackage("nuspec identity does not match the requested package ID and version");
            }

            RejectDuplicateAssemblyIdentities(assemblies);
            assemblies.Sort(CompareAssemblies);
            return new NuGetPackageSnapshot
            {
                Id = id,
                Version = version,
                Assets = new PackageAssetSnapshot
                {
                    PackageId = id,
                    Version = version,
                    Files = files,
                    Assemblies = assemblies,
                },
                Dependencies = new PackageDependencySnapshot
                {
                    PackageId = id,
                    Version = version,
                    Groups = dependencyGroups,
                },
            };
        }
        catch (RepositoryCheckException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new RepositoryCheckException(
                ExitCodes.IntegrityOrSignatureFailure,
                "NuGet package archive is invalid or unreadable.",
                exception);
        }
    }

    private List<ValidatedEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > _options.MaxEntryCount)
        {
            throw InvalidPackage("archive entry count exceeds its safety limit");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ValidatedEntry>(archive.Entries.Count);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            var path = NormalizePath(entry.FullName);
            if (!paths.Add(path))
            {
                throw InvalidPackage("archive contains duplicate normalized paths");
            }

            var length = entry.Length;
            var compressedLength = entry.CompressedLength;
            if (length < 0 || compressedLength < 0 || length > _options.MaxEntryUncompressedBytes)
            {
                throw InvalidPackage("archive entry length exceeds its safety limit");
            }

            try
            {
                totalLength = checked(totalLength + length);
            }
            catch (OverflowException)
            {
                throw InvalidPackage("archive total length overflowed");
            }

            if (totalLength > _options.MaxTotalUncompressedBytes)
            {
                throw InvalidPackage("archive total uncompressed length exceeds its safety limit");
            }

            if (length > 0
                && (compressedLength == 0 || (double)length / compressedLength > _options.MaxCompressionRatio))
            {
                throw InvalidPackage("archive entry compression ratio is suspicious");
            }

            entries.Add(new ValidatedEntry(entry, path, length));
        }

        return entries;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Contains('\\')
            || path.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(path)
            || path.Any(static character => character == '\0' || char.IsControl(character)))
        {
            throw InvalidPackage("archive contains an unsafe path");
        }

        var segments = path.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':')))
        {
            throw InvalidPackage("archive contains an unsafe path segment");
        }

        return string.Join('/', segments);
    }

    private static async Task<byte[]> ReadEntryAsync(ValidatedEntry entry, CancellationToken cancellationToken)
    {
        var bytes = new byte[(int)entry.Length];
        await using var source = entry.Entry.Open();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await source.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw InvalidPackage("archive entry ended before its declared uncompressed length");
            }

            offset += read;
        }

        if (await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw InvalidPackage("archive entry exceeded its declared uncompressed length");
        }

        return bytes;
    }

    private static async Task<(string Id, string Version, IReadOnlyList<PackageDependencyGroupSnapshot> Groups)> ReadNuspecAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
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
            if (root?.Name.LocalName != "package")
            {
                throw InvalidPackage("nuspec root element must be package");
            }

            var metadataElements = root.Elements().Where(static element => element.Name.LocalName == "metadata").ToArray();
            if (metadataElements.Length != 1)
            {
                throw InvalidPackage("nuspec must contain exactly one metadata element");
            }

            var metadata = metadataElements[0];
            var id = RequiredSingleValue(metadata, "id");
            var version = RequiredSingleValue(metadata, "version");
            var dependencies = metadata.Elements().Where(static element => element.Name.LocalName == "dependencies").ToArray();
            if (dependencies.Length > 1)
            {
                throw InvalidPackage("nuspec contains multiple dependencies elements");
            }

            return (id, version, dependencies.Length == 0 ? [] : ReadDependencyGroups(dependencies[0]));
        }
        catch (RepositoryCheckException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.IntegrityOrSignatureFailure,
                "NuGet package nuspec is invalid or unsafe.",
                exception);
        }
    }

    private static string RequiredSingleValue(XElement parent, string localName)
    {
        var elements = parent.Elements().Where(element => element.Name.LocalName == localName).ToArray();
        var value = elements.Length == 1 ? elements[0].Value.Trim() : null;
        if (string.IsNullOrEmpty(value))
        {
            throw InvalidPackage($"nuspec metadata must contain exactly one non-empty {localName}");
        }

        return value;
    }

    private static IReadOnlyList<PackageDependencyGroupSnapshot> ReadDependencyGroups(XElement dependencies)
    {
        var explicitGroups = dependencies.Elements().Where(static element => element.Name.LocalName == "group").ToArray();
        var directDependencies = dependencies.Elements().Where(static element => element.Name.LocalName == "dependency").ToArray();
        if (explicitGroups.Length > 0 && directDependencies.Length > 0)
        {
            throw InvalidPackage("nuspec mixes grouped and ungrouped dependencies");
        }

        IEnumerable<(string Framework, IEnumerable<XElement> Dependencies)> groups = explicitGroups.Length == 0
            ? directDependencies.Length == 0
                ? Array.Empty<(string Framework, IEnumerable<XElement> Dependencies)>()
                : [(string.Empty, (IEnumerable<XElement>)directDependencies)]
            : explicitGroups.Select(static group =>
                (NormalizeFramework((string?)group.Attribute("targetFramework")),
                    group.Elements().Where(static element => element.Name.LocalName == "dependency")));
        var frameworkIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<PackageDependencyGroupSnapshot>();
        foreach (var group in groups)
        {
            if (!frameworkIdentities.Add(group.Framework))
            {
                throw InvalidPackage("nuspec contains duplicate dependency groups for the same target framework");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dependenciesInGroup = new List<PackageDependencyItemSnapshot>();
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

                dependenciesInGroup.Add(new PackageDependencyItemSnapshot { Id = id, VersionRange = version });
            }

            dependenciesInGroup.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
            snapshots.Add(new PackageDependencyGroupSnapshot
            {
                TargetFramework = group.Framework,
                Dependencies = dependenciesInGroup,
            });
        }

        snapshots.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.TargetFramework, right.TargetFramework));
        return snapshots;
    }

    private static string NormalizeFramework(string? framework)
    {
        var normalized = framework?.Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal)
            ?? string.Empty;
        return NormalizeLongFramework(normalized, ".netcoreapp,version=v", "net", preserveDots: true)
            ?? NormalizeLongFramework(normalized, ".netstandard,version=v", "netstandard", preserveDots: true)
            ?? NormalizeLongFramework(normalized, ".netframework,version=v", "net", preserveDots: false)
            ?? normalized;
    }

    private static string? NormalizeLongFramework(
        string framework,
        string prefix,
        string shortPrefix,
        bool preserveDots)
    {
        if (!framework.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var version = framework[prefix.Length..];
        if (version.Length == 0 || version.Any(static character => !char.IsAsciiDigit(character) && character != '.'))
        {
            return framework;
        }

        return shortPrefix + (preserveDots ? version : version.Replace(".", string.Empty, StringComparison.Ordinal));
    }

    private static string Categorize(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return "assembly";
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return "xml-doc";
        }

        if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            return "pdb";
        }

        if (extension.Equals(".nuspec", StringComparison.OrdinalIgnoreCase))
        {
            return "nuspec";
        }

        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("readme", StringComparison.OrdinalIgnoreCase))
        {
            return "readme";
        }

        if (fileName.StartsWith("icon.", StringComparison.OrdinalIgnoreCase))
        {
            return "icon";
        }

        return "other";
    }

    private static bool TryGetManagedAsset(string path, out string targetFramework)
    {
        var segments = path.Split('/');
        if (segments.Length >= 3
            && (segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase)
                || segments[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
            && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            targetFramework = NormalizeFramework(segments[1]);
            if (targetFramework.Length == 0)
            {
                throw InvalidPackage("managed assembly asset has an empty target framework");
            }

            return true;
        }

        targetFramework = string.Empty;
        return false;
    }

    private static PackageAssemblySnapshot ReadAssembly(string path, string targetFramework, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                throw new BadImageFormatException("PE image has no managed metadata.");
            }

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                throw new BadImageFormatException("PE metadata does not describe an assembly.");
            }

            var definition = metadata.GetAssemblyDefinition();
            var exportedTypes = metadata.TypeDefinitions
                .Select(handle => (Handle: handle, Definition: metadata.GetTypeDefinition(handle)))
                .Where(pair => IsExportedPublic(metadata, pair.Handle))
                .Select(pair => GetTypeDefinitionName(metadata, pair.Handle))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var forwarders = metadata.ExportedTypes
                .Select(handle => (Handle: handle, Definition: metadata.GetExportedType(handle)))
                .Where(static pair => (pair.Definition.Attributes & (TypeAttributes)0x00200000) != 0)
                .Select(pair => GetExportedTypeName(metadata, pair.Handle))
                .Order(StringComparer.Ordinal)
                .ToArray();
            return new PackageAssemblySnapshot
            {
                Name = metadata.GetString(definition.Name),
                Version = definition.Version.ToString(),
                Culture = definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture),
                PublicKeyToken = GetPublicKeyToken(metadata.GetBlobBytes(definition.PublicKey)),
                AssetPath = path,
                TargetFramework = targetFramework,
                ExportedTypes = exportedTypes,
                TypeForwarders = forwarders,
            };
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException or IOException)
        {
            throw new RepositoryCheckException(
                ExitCodes.IntegrityOrSignatureFailure,
                $"NuGet managed assembly asset '{path}' is invalid or truncated.",
                exception);
        }
    }

    private static bool IsExportedPublic(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var attributes = definition.Attributes;
        var visibility = attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public)
        {
            return true;
        }

        if (visibility != TypeAttributes.NestedPublic)
        {
            return false;
        }

        var declaringType = definition.GetDeclaringType();
        return !declaringType.IsNil && IsExportedPublic(metadata, declaringType);
    }

    private static string GetTypeDefinitionName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(definition.Name);
        var declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetTypeDefinitionName(metadata, declaringType)}+{name}";
        }

        var typeNamespace = metadata.GetString(definition.Namespace);
        return typeNamespace.Length == 0 ? name : $"{typeNamespace}.{name}";
    }

    private static string GetExportedTypeName(MetadataReader metadata, ExportedTypeHandle handle)
    {
        var definition = metadata.GetExportedType(handle);
        var name = metadata.GetString(definition.Name);
        if (definition.Implementation.Kind == HandleKind.ExportedType)
        {
            return $"{GetExportedTypeName(metadata, (ExportedTypeHandle)definition.Implementation)}+{name}";
        }

        var typeNamespace = metadata.GetString(definition.Namespace);
        return typeNamespace.Length == 0 ? name : $"{typeNamespace}.{name}";
    }

    private static string GetPublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return string.Empty;
        }

        var hash = SHA1.HashData(publicKey);
        return Convert.ToHexStringLower(hash.AsSpan(hash.Length - 8).ToArray().Reverse().ToArray());
    }

    private static void RejectDuplicateAssemblyIdentities(IEnumerable<PackageAssemblySnapshot> assemblies)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            if (!identities.Add($"{assembly.TargetFramework}\0{assembly.Name}"))
            {
                throw InvalidPackage("package contains a duplicate assembly identity for the same target framework");
            }
        }
    }

    private static int CompareAssemblies(PackageAssemblySnapshot left, PackageAssemblySnapshot right)
    {
        var framework = StringComparer.OrdinalIgnoreCase.Compare(left.TargetFramework, right.TargetFramework);
        return framework != 0
            ? framework
            : StringComparer.OrdinalIgnoreCase.Compare(left.AssetPath, right.AssetPath);
    }

    private static RepositoryCheckException InvalidPackage(string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet package failed integrity validation: {detail}.");
    }

    private sealed record ValidatedEntry(ZipArchiveEntry Entry, string Path, long Length);
}
