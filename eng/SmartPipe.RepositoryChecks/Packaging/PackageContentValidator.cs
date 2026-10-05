using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Packaging;

internal sealed record PackageMetadataViolation(string Code, string PackageId, string Rule, string? Path = null);
internal sealed record PackageMetadataReport
{
    public required string Mode { get; init; }
    public required int Packages { get; init; }
    public required IReadOnlyList<PackageMetadataViolation> Violations { get; init; }
    public bool Success => Violations.Count == 0;
}

internal sealed class PackageContentValidator
{
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private readonly NuGetPackageReaderOptions _options = new();

    public async Task<IReadOnlyList<PackageMetadataViolation>> ValidateAsync(
        PackageNode node,
        string version,
        PackageMetadata metadata,
        string snupkgPath,
        PackageGraphMode mode,
        CancellationToken ct)
    {
        var errors = new List<PackageMetadataViolation>();
        void Add(string code, string rule, string? path = null) =>
            errors.Add(new(code, node.Id, rule, path));

        ValidateMetadata(node, version, metadata, mode, Add);

        var files = metadata.Snapshot.Assets.Files
            .Select(static asset => asset.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateRequiredContent(node.Id, files, Add);
        ValidatePackageContent(node.Id, files, Add);

        var dllPath = $"lib/net10.0/{node.Id}.dll";
        await ValidatePeAsync(metadata, dllPath, Add, ct).ConfigureAwait(false);
        await ValidateSymbolsAsync(
            node,
            version,
            snupkgPath,
            metadata.RepositoryCommit,
            Add,
            ct).ConfigureAwait(false);
        return errors;
    }

    private static void ValidateMetadata(
        PackageNode node,
        string version,
        PackageMetadata metadata,
        PackageGraphMode mode,
        Action<string, string, string?> add)
    {
        if (metadata.Snapshot.Id != node.Id || metadata.Snapshot.Version != version)
            add("SPMETA001", $"identity must be {node.Id} {version}");
        if (metadata.Description.Length < 20
            || metadata.Description.Equals("Package Description", StringComparison.OrdinalIgnoreCase))
            add("SPMETA002", "description must be non-empty and package-specific");
        if (metadata.Authors != "SmartPipe"
            || metadata.Copyright.Length == 0
            || metadata.LicenseExpression != "MIT")
            add("SPMETA003", "authors/copyright/license metadata is invalid");
        if (metadata.RepositoryUrl != "https://github.com/MrFr3di/SmartPipe-Core"
            || metadata.RepositoryType != "git"
            || !IsCommit(metadata.RepositoryCommit))
            add("SPMETA004", "repository URL/type/40-hex commit is required");
        if (metadata.Readme != "README.md" || metadata.Icon != "icon.png" || metadata.Tags.Length == 0)
            add("SPMETA005", "readme/icon/tags metadata is invalid");
        if (mode == PackageGraphMode.Release && metadata.ReleaseNotes is null)
            add("SPMETA006", "release notes are required in release mode");
    }

    private static void ValidateRequiredContent(
        string packageId,
        IReadOnlySet<string> files,
        Action<string, string, string?> add)
    {
        var requiredFiles = new[]
        {
            "README.md",
            "icon.png",
            $"{packageId}.nuspec",
            $"lib/net10.0/{packageId}.dll",
            $"lib/net10.0/{packageId}.xml",
            "_rels/.rels",
            "[Content_Types].xml",
        };

        foreach (var required in requiredFiles.Where(required => !files.Contains(required)))
            add("SPMETA007", "required package content is missing", required);
    }

    private static void ValidatePackageContent(
        string packageId,
        IEnumerable<string> files,
        Action<string, string, string?> add)
    {
        foreach (var file in files)
        {
            var lower = file.ToLowerInvariant();
            if (IsForbiddenPackageContent(lower))
                add("SPMETA008", "forbidden source/test/obj/lock/local-path content", file);
            if (IsUnexpectedTargetPath(lower))
                add("SPMETA009", "unexpected TFM or RID", file);
            if (!IsAllowedPackagePath(packageId, file))
                add("SPMETA017", "package file is outside the exact content allowlist", file);
        }
    }

    private static bool IsForbiddenPackageContent(string path) =>
        path.EndsWith(".cs")
        || path.Contains("/obj/")
        || path.Contains("/tests/")
        || path.EndsWith(".tests.dll")
        || path.EndsWith(".test.dll")
        || path.EndsWith("packages.lock.json")
        || path.Contains(":\\");

    private static bool IsUnexpectedTargetPath(string path) =>
        (path.StartsWith("lib/") || path.StartsWith("runtimes/"))
        && !path.StartsWith("lib/net10.0/");

    private async Task ValidatePeAsync(PackageMetadata metadata, string dllPath, Action<string, string, string?> add, CancellationToken ct)
    {
        var file = metadata.Snapshot.Assets.Files.SingleOrDefault(x => x.Path.Equals(dllPath, StringComparison.OrdinalIgnoreCase));
        if (file is null) return;
        await using var stream = new FileStream(metadata.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await NuGetArchiveSafetyReader.PreflightAsync(stream, _options, ct).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = NuGetArchiveSafetyReader.ValidateEntries(archive, _options).Single(x => x.Path.Equals(dllPath, StringComparison.OrdinalIgnoreCase));
        var bytes = await NuGetArchiveSafetyReader.ReadEntryAsync(entry, ct).ConfigureAwait(false);
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, false));
            var debug = pe.ReadDebugDirectory();
            if (!debug.Any(x => x.Type == DebugDirectoryEntryType.Reproducible) || !debug.Any(x => x.Type == DebugDirectoryEntryType.CodeView))
                add("SPMETA015", "assembly must contain reproducible and CodeView debug entries", dllPath);
        }
        catch (BadImageFormatException) { add("SPMETA015", "assembly PE debug directory is invalid", dllPath); }
    }

    private async Task ValidateSymbolsAsync(PackageNode node, string version, string snupkgPath, string commit, Action<string, string, string?> add, CancellationToken ct)
    {
        if (!File.Exists(snupkgPath)) { add("SPMETA010", "matching .snupkg is required", snupkgPath); return; }
        await using var stream = new FileStream(snupkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await NuGetArchiveSafetyReader.PreflightAsync(stream, _options, ct).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = NuGetArchiveSafetyReader.ValidateEntries(archive, _options);
        foreach (var entry in entries)
            if (!IsAllowedSymbolPath(node.Id, entry.Path)) add("SPMETA018", "symbol package file is outside the exact content allowlist", entry.Path);
        var nuspec = entries.SingleOrDefault(x => !x.Path.Contains('/') && x.Path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (nuspec is null) { add("SPMETA011", "symbol package requires one root nuspec", snupkgPath); return; }
        var nuspecBytes = await NuGetArchiveSafetyReader.ReadEntryAsync(nuspec, ct).ConfigureAwait(false);
        var symbolIdentity = await NuspecPackageReader.ReadAsync(nuspecBytes, ct).ConfigureAwait(false);
        var symbolCommit = ReadRepositoryCommit(nuspecBytes);
        if (!symbolIdentity.Id.Equals(node.Id, StringComparison.Ordinal) || !symbolIdentity.Version.Equals(version, StringComparison.Ordinal) || !symbolCommit.Equals(commit, StringComparison.Ordinal))
            add("SPMETA011", "symbol package identity/repository commit must match", nuspec.Path);
        var pdbPath = $"lib/net10.0/{node.Id}.pdb";
        var pdbEntry = entries.SingleOrDefault(x => x.Path.Equals(pdbPath, StringComparison.OrdinalIgnoreCase));
        if (pdbEntry is null) { add("SPMETA012", "matching portable PDB is required", pdbPath); return; }
        var bytes = await NuGetArchiveSafetyReader.ReadEntryAsync(pdbEntry, ct).ConfigureAwait(false);
        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(bytes, false));
            var reader = provider.GetMetadataReader();
            var sourceLinkHandle = reader.GetCustomDebugInformation(MetadataTokens.EntityHandle(0x00000001))
                .FirstOrDefault(handle => reader.GetGuid(reader.GetCustomDebugInformation(handle).Kind) == SourceLinkKind);
            if (sourceLinkHandle.IsNil) { add("SPMETA013", "portable PDB must contain Source Link JSON", pdbPath); return; }
            var json = reader.GetBlobBytes(reader.GetCustomDebugInformation(sourceLinkHandle).Value);
            using var document = JsonDocument.Parse(json);
            var documents = document.RootElement.GetProperty("documents");
            foreach (var property in documents.EnumerateObject())
                if (IsLocalAbsoluteSourceRoot(property.Name)) add("SPMETA014", "Source Link must not expose local absolute roots", property.Name);
        }
        catch (BadImageFormatException) { add("SPMETA012", "PDB must use portable format", pdbPath); }
    }

    private static bool IsCommit(string value) => value.Length == 40 && value.All(c => char.IsAsciiHexDigit(c));
    internal static bool IsAllowedPackagePath(string packageId, string path) =>
        path.Equals("README.md", StringComparison.OrdinalIgnoreCase)
        || path.Equals("icon.png", StringComparison.OrdinalIgnoreCase)
        || path.Equals($"{packageId}.nuspec", StringComparison.OrdinalIgnoreCase)
        || path.Equals($"lib/net10.0/{packageId}.dll", StringComparison.OrdinalIgnoreCase)
        || path.Equals($"lib/net10.0/{packageId}.xml", StringComparison.OrdinalIgnoreCase)
        || path.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)
        || path.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("package/services/metadata/core-properties/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase);
    private static bool IsAllowedSymbolPath(string packageId, string path) =>
        path.Equals($"{packageId}.nuspec", StringComparison.OrdinalIgnoreCase)
        || path.Equals($"lib/net10.0/{packageId}.pdb", StringComparison.OrdinalIgnoreCase)
        || path.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)
        || path.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("package/services/metadata/core-properties/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase);
    private static string ReadRepositoryCommit(byte[] nuspec)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = nuspec.Length };
        using var reader = System.Xml.XmlReader.Create(new MemoryStream(nuspec, false), settings);
        var document = System.Xml.Linq.XDocument.Load(reader, System.Xml.Linq.LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("symbol nuspec root missing");
        var ns = root.Name.Namespace;
        var repositories = root.Element(ns + "metadata")?.Elements(ns + "repository").ToArray() ?? [];
        return repositories.Length == 1 && (string?)repositories[0].Attribute("commit") is { Length: > 0 } value
            ? value : throw new InvalidDataException("symbol nuspec repository commit missing or duplicate");
    }
    private static bool IsLocalAbsoluteSourceRoot(string value) =>
        value.Contains(":\\", StringComparison.Ordinal)
        || value.StartsWith("\\\\", StringComparison.Ordinal)
        || value.StartsWith("/", StringComparison.Ordinal)
           && !value.Equals("/_/*", StringComparison.Ordinal);
}
