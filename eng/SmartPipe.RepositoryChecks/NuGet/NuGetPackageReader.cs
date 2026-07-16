using System.IO.Compression;
using System.Security.Cryptography;
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
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await NuGetArchiveSafetyReader.PreflightAsync(stream, _options, cancellationToken).ConfigureAwait(false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = NuGetArchiveSafetyReader.ValidateEntries(archive, _options);
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
                var bytes = await NuGetArchiveSafetyReader.ReadEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                files.Add(new PackageFileSnapshot
                {
                    Path = entry.Path,
                    UncompressedLength = entry.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    Category = entry.Category,
                });

                if (ReferenceEquals(entry.Entry, nuspecEntries[0].Entry))
                {
                    nuspecBytes = bytes;
                }

                if (ManagedAssemblyInspector.TryInspect(entry.Path, bytes, out var assembly))
                {
                    assemblies.Add(assembly!);
                }
            }

            var nuspec = await NuspecPackageReader.ReadAsync(
                nuspecBytes ?? throw InvalidPackage("root nuspec could not be read"),
                cancellationToken).ConfigureAwait(false);
            if (expectedPackageId is not null
                && (!string.Equals(nuspec.Id, expectedPackageId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(nuspec.Version, expectedVersion, StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidPackage("nuspec identity does not match the requested package ID and version");
            }

            ManagedAssemblyInspector.ValidateAndSort(assemblies);
            return CreateSnapshot(nuspec, files, assemblies);
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

    private static NuGetPackageSnapshot CreateSnapshot(
        NuspecPackageMetadata nuspec,
        IReadOnlyList<PackageFileSnapshot> files,
        IReadOnlyList<PackageAssemblySnapshot> assemblies)
    {
        return new NuGetPackageSnapshot
        {
            Id = nuspec.Id,
            Version = nuspec.Version,
            Assets = new PackageAssetSnapshot
            {
                PackageId = nuspec.Id,
                Version = nuspec.Version,
                Files = files,
                Assemblies = assemblies,
            },
            Dependencies = new PackageDependencySnapshot
            {
                PackageId = nuspec.Id,
                Version = nuspec.Version,
                Groups = nuspec.Groups,
            },
        };
    }

    private static RepositoryCheckException InvalidPackage(string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet package failed integrity validation: {detail}.");
    }
}
