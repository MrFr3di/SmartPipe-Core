using System.Buffers.Binary;
using System.IO.Compression;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal static class NuGetArchiveSafetyReader
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const int EndOfCentralDirectorySize = 22;
    private const int MaximumCommentLength = ushort.MaxValue;
    private const int Zip64LocatorSize = 20;
    private const int Zip64MinimumRecordSize = 56;

    public static async Task PreflightAsync(
        FileStream stream,
        NuGetPackageReaderOptions options,
        CancellationToken cancellationToken)
    {
        if (stream.Length < EndOfCentralDirectorySize)
        {
            throw PreflightFailure("archive is too short to contain an end-of-central-directory record");
        }

        var tailLength = (int)Math.Min(stream.Length, EndOfCentralDirectorySize + MaximumCommentLength);
        var tailOffset = stream.Length - tailLength;
        var tail = new byte[tailLength];
        await ReadExactlyAtAsync(stream, tailOffset, tail, cancellationToken).ConfigureAwait(false);
        var relativeEocdOffset = FindEndOfCentralDirectory(tail);
        if (relativeEocdOffset < 0)
        {
            throw PreflightFailure("end-of-central-directory record is missing or has an invalid comment length");
        }

        var eocdOffset = checked(tailOffset + relativeEocdOffset);
        var record = tail.AsSpan(relativeEocdOffset, EndOfCentralDirectorySize);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (diskNumber != 0 || centralDirectoryDisk != 0)
        {
            throw PreflightFailure("multi-disk ZIP archives are not supported");
        }

        var requiresZip64 = entriesOnDisk == ushort.MaxValue
            || totalEntries == ushort.MaxValue
            || centralDirectorySize == uint.MaxValue
            || centralDirectoryOffset == uint.MaxValue;
        if (requiresZip64)
        {
            await ValidateZip64Async(stream, eocdOffset, options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (entriesOnDisk != totalEntries)
            {
                throw PreflightFailure("central-directory entry counts disagree");
            }

            ValidateDirectoryBounds(totalEntries, centralDirectoryOffset, centralDirectorySize, eocdOffset, options);
        }

        stream.Position = 0;
    }

    public static IReadOnlyList<ValidatedPackageEntry> ValidateEntries(
        ZipArchive archive,
        NuGetPackageReaderOptions options)
    {
        if (archive.Entries.Count > options.MaxEntryCount)
        {
            throw InvalidPackage("archive entry count exceeds its safety limit");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ValidatedPackageEntry>(archive.Entries.Count);
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
            if (length < 0 || compressedLength < 0 || length > options.MaxEntryUncompressedBytes)
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

            if (totalLength > options.MaxTotalUncompressedBytes)
            {
                throw InvalidPackage("archive total uncompressed length exceeds its safety limit");
            }

            if (length > 0
                && (compressedLength == 0 || (double)length / compressedLength > options.MaxCompressionRatio))
            {
                throw InvalidPackage("archive entry compression ratio is suspicious");
            }

            entries.Add(new ValidatedPackageEntry(entry, path, length, Categorize(path)));
        }

        return entries;
    }

    public static async Task<byte[]> ReadEntryAsync(
        ValidatedPackageEntry entry,
        CancellationToken cancellationToken)
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

    private static int FindEndOfCentralDirectory(byte[] tail)
    {
        for (var offset = tail.Length - EndOfCentralDirectorySize; offset >= 0; offset--)
        {
            var candidate = tail.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(candidate[20..]);
            if (offset + EndOfCentralDirectorySize + commentLength == tail.Length)
            {
                return offset;
            }
        }

        return -1;
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

    private static string Categorize(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return "assembly";
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return "xml-doc";
        if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)) return "pdb";
        if (extension.Equals(".nuspec", StringComparison.OrdinalIgnoreCase)) return "nuspec";

        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("readme", StringComparison.OrdinalIgnoreCase)) return "readme";
        if (fileName.StartsWith("icon.", StringComparison.OrdinalIgnoreCase)) return "icon";
        return "other";
    }

    private static async Task ValidateZip64Async(
        FileStream stream,
        long eocdOffset,
        NuGetPackageReaderOptions options,
        CancellationToken cancellationToken)
    {
        var locatorOffset = eocdOffset - Zip64LocatorSize;
        if (locatorOffset < 0)
        {
            throw PreflightFailure("ZIP64 locator is truncated");
        }

        var locator = new byte[Zip64LocatorSize];
        await ReadExactlyAtAsync(stream, locatorOffset, locator, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
        {
            throw PreflightFailure("ZIP64 locator is missing or truncated");
        }

        var recordDisk = BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4));
        var recordOffsetValue = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        var diskCount = BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16));
        if (recordDisk != 0 || diskCount != 1 || recordOffsetValue > long.MaxValue)
        {
            throw PreflightFailure("ZIP64 locator is not a valid single-disk locator");
        }

        var recordOffset = (long)recordOffsetValue;
        if (recordOffset < 0 || recordOffset > locatorOffset - Zip64MinimumRecordSize)
        {
            throw PreflightFailure("ZIP64 end-of-central-directory record is outside the archive or truncated");
        }

        var record = new byte[Zip64MinimumRecordSize];
        await ReadExactlyAtAsync(stream, recordOffset, record, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature)
        {
            throw PreflightFailure("ZIP64 end-of-central-directory signature is invalid");
        }

        var payloadSize = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(4));
        if (payloadSize < 44
            || payloadSize > long.MaxValue
            || !TryAdd(recordOffset, 12 + (long)payloadSize, out var recordEnd)
            || recordEnd > locatorOffset)
        {
            throw PreflightFailure("ZIP64 end-of-central-directory record is truncated or overflows");
        }

        var diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16));
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20));
        var entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24));
        var totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
        var directorySize = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
        var directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        if (diskNumber != 0 || centralDirectoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            throw PreflightFailure("ZIP64 multi-disk or inconsistent entry counts are not supported");
        }

        ValidateDirectoryBounds(totalEntries, directoryOffset, directorySize, recordOffset, options);
    }

    private static void ValidateDirectoryBounds(
        ulong entryCount,
        ulong directoryOffset,
        ulong directorySize,
        long directoryLimit,
        NuGetPackageReaderOptions options)
    {
        if (entryCount > (ulong)options.MaxEntryCount)
        {
            throw PreflightFailure("central-directory entry count exceeds its safety limit");
        }

        if (directoryOffset > long.MaxValue
            || directorySize > long.MaxValue
            || !TryAdd((long)directoryOffset, (long)directorySize, out var directoryEnd)
            || directoryEnd > directoryLimit)
        {
            throw PreflightFailure("central-directory offset or size is outside the archive or overflows");
        }
    }

    private static async Task ReadExactlyAtAsync(
        FileStream stream,
        long offset,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryAdd(long left, long right, out long result)
    {
        try
        {
            result = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static RepositoryCheckException PreflightFailure(string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet ZIP central-directory preflight failed: {detail}.");
    }

    private static RepositoryCheckException InvalidPackage(string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet package failed integrity validation: {detail}.");
    }
}

internal sealed record ValidatedPackageEntry(
    ZipArchiveEntry Entry,
    string Path,
    long Length,
    string Category);
