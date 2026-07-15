using System.IO.Compression;
using System.Text;

namespace SmartPipe.RepositoryChecks.Tests.NuGet;

internal sealed class SyntheticNuGetPackage : IDisposable
{
    private readonly string _directory;

    private SyntheticNuGetPackage(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    public string Path { get; }

    public static SyntheticNuGetPackage Create(
        string packageId = "SmartPipe.Core",
        string version = "2.1.2",
        IEnumerable<(string Path, byte[] Content)>? entries = null,
        string? nuspec = null,
        string? nuspecPath = null,
        DateTimeOffset? timestamp = null,
        bool nuspecLast = false)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smartpipe-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"{packageId}.{version}.nupkg");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var packageEntries = (entries ?? []).ToArray();
        if (!nuspecLast)
        {
            AddEntry(archive, nuspecPath ?? $"{packageId}.nuspec", Encoding.UTF8.GetBytes(nuspec ?? CreateNuspec(packageId, version)), timestamp);
        }

        foreach (var entry in packageEntries)
        {
            AddEntry(archive, entry.Path, entry.Content, timestamp);
        }

        if (nuspecLast)
        {
            AddEntry(archive, nuspecPath ?? $"{packageId}.nuspec", Encoding.UTF8.GetBytes(nuspec ?? CreateNuspec(packageId, version)), timestamp);
        }

        return new SyntheticNuGetPackage(directory, path);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    public static string CreateNuspec(string packageId, string version, string dependencyXml = "") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <authors>SmartPipe</authors>
            <description>Synthetic test package.</description>
            {{dependencyXml}}
          </metadata>
        </package>
        """;

    private static void AddEntry(ZipArchive archive, string path, byte[] content, DateTimeOffset? timestamp = null)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        if (timestamp.HasValue)
        {
            entry.LastWriteTime = timestamp.Value;
        }

        using var destination = entry.Open();
        destination.Write(content);
    }
}
