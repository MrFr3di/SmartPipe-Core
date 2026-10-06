using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.Packaging;

internal sealed record PackageMetadata(
    string PackagePath, NuGetPackageSnapshot Snapshot, string Description, string Authors, string Copyright,
    string LicenseExpression, string RepositoryUrl, string RepositoryType, string RepositoryCommit,
    string Readme, string Icon, string Tags, string? ReleaseNotes);

internal sealed class PackageMetadataReader
{
    private readonly NuGetPackageReaderOptions _options = new();
    public async Task<PackageMetadata> ReadAsync(string path, CancellationToken ct)
    {
        var snapshot = await new NuGetPackageReader(_options).ReadAsync(path, ct).ConfigureAwait(false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await NuGetArchiveSafetyReader.PreflightAsync(stream, _options, ct).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = NuGetArchiveSafetyReader.ValidateEntries(archive, _options);
        var nuspec = entries.Where(x => !x.Path.Contains('/') && x.Path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nuspec.Length != 1) throw Invalid("package must contain exactly one root nuspec");
        var bytes = await NuGetArchiveSafetyReader.ReadEntryAsync(nuspec[0], ct).ConfigureAwait(false);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = bytes.Length };
        using var xml = XmlReader.Create(new MemoryStream(bytes, false), settings);
        var document = XDocument.Load(xml, LoadOptions.None);
        var root = document.Root ?? throw Invalid("nuspec root is missing");
        var ns = root.Name.Namespace;
        var metadata = Single(root, ns + "metadata");
        var repository = Single(metadata, ns + "repository");
        var license = Single(metadata, ns + "license");
        if (!string.Equals((string?)license.Attribute("type"), "expression", StringComparison.Ordinal)) throw Invalid("license must be an SPDX expression");
        return new(path, snapshot, Value(metadata, ns + "description"), Value(metadata, ns + "authors"), Value(metadata, ns + "copyright"),
            license.Value.Trim(), RequiredAttribute(repository, "url"), RequiredAttribute(repository, "type"), RequiredAttribute(repository, "commit"),
            Value(metadata, ns + "readme"), Value(metadata, ns + "icon"), Value(metadata, ns + "tags"), OptionalValue(metadata, ns + "releaseNotes"));
    }

    private static XElement Single(XContainer parent, XName name)
    {
        var values = parent.Elements(name).ToArray();
        return values.Length == 1 ? values[0] : throw Invalid($"nuspec requires exactly one {name.LocalName}");
    }
    private static string Value(XContainer parent, XName name)
    {
        var value = Single(parent, name).Value.Trim();
        return value.Length > 0 ? value : throw Invalid($"nuspec {name.LocalName} must not be empty");
    }
    private static string? OptionalValue(XContainer parent, XName name) => parent.Elements(name).SingleOrDefault()?.Value.Trim() is { Length: > 0 } value ? value : null;
    private static string RequiredAttribute(XElement element, XName name) => ((string?)element.Attribute(name))?.Trim() is { Length: > 0 } value ? value : throw Invalid($"nuspec repository {name} is required");
    private static RepositoryCheckException Invalid(string detail) => new(ExitCodes.PackedPackageViolation, $"NuGet metadata invalid: {detail}.");
}
