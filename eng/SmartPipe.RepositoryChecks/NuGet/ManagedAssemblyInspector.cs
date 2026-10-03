using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal static class ManagedAssemblyInspector
{
    public static bool TryInspect(string path, byte[] bytes, out PackageAssemblySnapshot? snapshot)
    {
        var segments = path.Split('/');
        if (segments.Length < 3
            || (!segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase)
                && !segments[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
            || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            snapshot = null;
            return false;
        }

        var family = segments[0].ToLowerInvariant();
        var framework = NuspecPackageReader.CanonicalizeFramework(segments[1]);
        if (framework.Length == 0)
        {
            throw InvalidPackage("managed assembly asset has an empty target framework");
        }

        snapshot = Inspect(path, family, framework, bytes);
        return true;
    }

    public static void ValidateAndSort(List<PackageAssemblySnapshot> assemblies)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            if (!identities.Add(
                    $"{assembly.AssetFamily}\0{assembly.TargetFramework}\0{assembly.Name}\0{assembly.Version}\0{assembly.Culture}\0{assembly.PublicKeyToken}"))
            {
                throw InvalidPackage("package contains a duplicate exact assembly identity in the same asset family and target framework");
            }
        }

        assemblies.Sort(static (left, right) =>
        {
            var framework = StringComparer.OrdinalIgnoreCase.Compare(left.TargetFramework, right.TargetFramework);
            if (framework != 0) return framework;
            var family = StringComparer.OrdinalIgnoreCase.Compare(left.AssetFamily, right.AssetFamily);
            return family != 0 ? family : StringComparer.OrdinalIgnoreCase.Compare(left.AssetPath, right.AssetPath);
        });
    }

    public static PackageAssemblySnapshot Inspect(
        string path,
        string assetFamily,
        string targetFramework,
        byte[] bytes)
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
                .Select(handle => (Handle: handle, Destination: GetForwarderDestination(metadata, handle)))
                .Where(static pair => pair.Destination is not null)
                .Select(pair => (Name: GetExportedTypeName(metadata, pair.Handle), Destination: pair.Destination!))
                .OrderBy(static pair => pair.Name, StringComparer.Ordinal)
                .ToArray();
            return new PackageAssemblySnapshot
            {
                Name = metadata.GetString(definition.Name),
                Version = definition.Version.ToString(),
                Culture = definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture),
                PublicKeyToken = GetPublicKeyToken(
                    metadata.GetBlobBytes(definition.PublicKey),
                    (definition.Flags & AssemblyFlags.PublicKey) != 0),
                AssetFamily = assetFamily,
                AssetPath = path,
                TargetFramework = targetFramework,
                ExportedTypes = exportedTypes,
                TypeForwarders = forwarders.Select(static pair => pair.Name).ToArray(),
                ForwarderDestinations = forwarders.ToDictionary(static pair => pair.Name, static pair => pair.Destination, StringComparer.Ordinal),
            };
        }
        catch (RepositoryCheckException)
        {
            throw;
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
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
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

    private static AssemblyReferenceIdentity? GetForwarderDestination(MetadataReader metadata, ExportedTypeHandle handle)
    {
        var visited = new HashSet<ExportedTypeHandle>();
        while (true)
        {
            if (!visited.Add(handle)) throw InvalidPackage("cyclic ExportedType parent chain");
            var definition = metadata.GetExportedType(handle);
            if (definition.Implementation.Kind == HandleKind.ExportedType)
            {
                handle = (ExportedTypeHandle)definition.Implementation;
                continue;
            }
            if ((definition.Attributes & (TypeAttributes)0x00200000) == 0) return null;
            if (definition.Implementation.Kind != HandleKind.AssemblyReference)
                throw InvalidPackage("type forwarder must resolve to an AssemblyRef");
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)definition.Implementation);
            return new(metadata.GetString(reference.Name), reference.Version,
                reference.Culture.IsNil ? string.Empty : metadata.GetString(reference.Culture),
                GetPublicKeyToken(metadata.GetBlobBytes(reference.PublicKeyOrToken), (reference.Flags & AssemblyFlags.PublicKey) != 0));
        }
    }

    private static string GetPublicKeyToken(byte[] keyOrToken, bool containsFullPublicKey)
    {
        if (keyOrToken.Length == 0 && !containsFullPublicKey)
        {
            return string.Empty;
        }

        if (keyOrToken.Length == 0)
        {
            throw InvalidPackage("assembly full public-key blob must not be empty");
        }

        if (!containsFullPublicKey)
        {
            if (keyOrToken.Length != 8)
            {
                throw InvalidPackage("assembly public-key token blob must contain exactly 8 bytes");
            }

            return Convert.ToHexStringLower(keyOrToken);
        }

        var hash = SHA1.HashData(keyOrToken);
        return Convert.ToHexStringLower(hash.AsSpan(hash.Length - 8).ToArray().Reverse().ToArray());
    }

    private static RepositoryCheckException InvalidPackage(string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet package failed integrity validation: {detail}.");
    }
}
