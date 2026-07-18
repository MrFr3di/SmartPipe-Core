using System.IO.Compression;
using System.Buffers.Binary;
using System.Configuration.Assemblies;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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

    public void ForgeEndOfCentralDirectory(
        ushort entryCount,
        uint centralDirectorySize,
        uint centralDirectoryOffset)
    {
        var bytes = File.ReadAllBytes(Path);
        var offset = FindEndOfCentralDirectory(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10, 2), entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12, 4), centralDirectorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 16, 4), centralDirectoryOffset);
        File.WriteAllBytes(Path, bytes);
    }

    public void ForgeZip64WithLocatorOutsideArchive()
    {
        var bytes = File.ReadAllBytes(Path);
        var eocdOffset = FindEndOfCentralDirectory(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocdOffset + 8, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocdOffset + 10, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(eocdOffset + 12, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(eocdOffset + 16, 4), uint.MaxValue);
        var locatorOffset = eocdOffset - 20;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(locatorOffset, 4), 0x07064b50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(locatorOffset + 4, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(locatorOffset + 8, 8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(locatorOffset + 16, 4), 1);
        File.WriteAllBytes(Path, bytes);
    }

    public void ForgeZip64WithTruncatedRecord()
    {
        var bytes = File.ReadAllBytes(Path);
        var eocdOffset = FindEndOfCentralDirectory(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocdOffset + 8, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocdOffset + 10, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(eocdOffset + 12, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(eocdOffset + 16, 4), uint.MaxValue);
        var locatorOffset = eocdOffset - 20;
        var recordOffset = locatorOffset - 12;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(locatorOffset, 4), 0x07064b50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(locatorOffset + 4, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(locatorOffset + 8, 8), (ulong)recordOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(locatorOffset + 16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(recordOffset, 4), 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(recordOffset + 4, 8), 44);
        File.WriteAllBytes(Path, bytes);
    }

    public void ConvertToValidZip64()
    {
        var bytes = File.ReadAllBytes(Path);
        var eocdOffset = FindEndOfCentralDirectory(bytes);
        var originalEocd = bytes.AsSpan(eocdOffset);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(originalEocd[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(originalEocd[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalEocd[16..]);
        const int zip64RecordAndLocatorSize = 76;
        var converted = new byte[bytes.Length + zip64RecordAndLocatorSize];
        bytes.AsSpan(0, eocdOffset).CopyTo(converted);

        var record = converted.AsSpan(eocdOffset, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record[24..], entryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(record[32..], entryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(record[40..], directorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(record[48..], directoryOffset);

        var locator = converted.AsSpan(eocdOffset + 56, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)eocdOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);

        originalEocd.CopyTo(converted.AsSpan(eocdOffset + zip64RecordAndLocatorSize));
        var convertedEocd = converted.AsSpan(eocdOffset + zip64RecordAndLocatorSize);
        BinaryPrimitives.WriteUInt16LittleEndian(convertedEocd[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(convertedEocd[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(convertedEocd[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(convertedEocd[16..], uint.MaxValue);
        File.WriteAllBytes(Path, converted);
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

    public static byte[] CreateManagedAssembly(
        string name,
        Version version,
        byte[]? publicKeyOrToken = null,
        bool containsFullPublicKey = false)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            generation: 0,
            metadata.GetOrAddString($"{name}.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString(name),
            version,
            default,
            publicKeyOrToken is null ? default : metadata.GetOrAddBlob(publicKeyOrToken),
            containsFullPublicKey ? AssemblyFlags.PublicKey : 0,
            System.Reflection.AssemblyHashAlgorithm.Sha1);
        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            new BlobBuilder(),
            flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        peBuilder.Serialize(image);
        return image.ToArray();
    }

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

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        for (var offset = bytes.Length - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) == 0x06054b50)
            {
                return offset;
            }
        }

        throw new InvalidOperationException("Synthetic package has no EOCD record.");
    }
}
