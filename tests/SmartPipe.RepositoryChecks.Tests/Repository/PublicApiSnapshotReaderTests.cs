using System.Text;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Repository;

public sealed class PublicApiSnapshotReaderTests
{
    [Fact]
    public void Read_LfCrLfAndBomProduceSameCanonicalHash()
    {
        var lf = ReadSingle("#nullable enable\nType.A() -> void\n");
        var crlf = ReadSingle("#nullable enable\r\nType.A() -> void\r\n");
        var bom = ReadSingleBytes([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("#nullable enable\nType.A() -> void\n")]);

        Assert.Equal(lf.Sha256, crlf.Sha256);
        Assert.Equal(lf.Sha256, bom.Sha256);
        Assert.Equal(2, lf.LineCount);
        Assert.Equal(1, lf.ApiEntryCount);
        Assert.Equal("Type.A() -> void", lf.FirstApiEntry);
        Assert.Equal("Type.A() -> void", lf.LastApiEntry);
    }

    [Fact]
    public void Read_WhitespaceInsideApiEntryChangesHash()
    {
        var original = ReadSingle("Type.A() -> void\n");
        var changed = ReadSingle("Type.A()  -> void\n");

        Assert.NotEqual(original.Sha256, changed.Sha256);
    }

    [Fact]
    public void Read_MissingShippedFileFailsForPackableProject()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", "<Project />");
        var reader = new PublicApiSnapshotReader();

        var exception = Assert.Throws<FileNotFoundException>(() => reader.Read(
            repository.Path,
            [Identity("src/A/A.csproj")]));

        Assert.Contains("PublicAPI.Shipped.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ReportsUnexpectedPublicApiFileOutsidePackableProject()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/A/PublicAPI.Shipped.txt", "A\n");
        repository.Write("orphan/PublicAPI.Unshipped.txt", "Orphan\n");
        var reader = new PublicApiSnapshotReader();

        var snapshot = reader.Read(repository.Path, [Identity("src/A/A.csproj")]);

        Assert.Equal(["orphan/PublicAPI.Unshipped.txt"], snapshot.UnexpectedFiles);
    }

    [Fact]
    public void Read_RejectsLinkedExpectedPublicApiFile()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/A/Actual.txt", "A\n");
        if (!repository.TryCreateFileLink("src/A/PublicAPI.Shipped.txt", "src/A/Actual.txt"))
        {
            return;
        }

        var exception = Assert.Throws<InvalidDataException>(() => new PublicApiSnapshotReader().Read(
            repository.Path,
            [Identity("src/A/A.csproj")]));

        Assert.Contains("link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_UsesPlatformPathSemanticsForExcludedDirectoryNames()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/A/PublicAPI.Shipped.txt", "A\n");
        repository.Write("BIN/PublicAPI.Unshipped.txt", "Unexpected\n");

        var snapshot = new PublicApiSnapshotReader().Read(repository.Path, [Identity("src/A/A.csproj")]);

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(snapshot.UnexpectedFiles);
        }
        else
        {
            Assert.Equal(["BIN/PublicAPI.Unshipped.txt"], snapshot.UnexpectedFiles);
        }
    }

    [Fact]
    public void Read_PreservesCaseDistinctUnixProjectPathsWithOrdinalOrdering()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/A/PublicAPI.Shipped.txt", "A\n");
        repository.Write("src/a/a.csproj", "<Project />");
        repository.Write("src/a/PublicAPI.Shipped.txt", "a\n");

        var snapshot = new PublicApiSnapshotReader().Read(
            repository.Path,
            [Identity("src/a/a.csproj"), Identity("src/A/A.csproj")]);

        Assert.Equal(
            ["src/A/PublicAPI.Shipped.txt", "src/a/PublicAPI.Shipped.txt"],
            snapshot.Files.Select(static file => file.Path));
    }

    private static PublicApiFileSnapshot ReadSingle(string content) => ReadSingleBytes(Encoding.UTF8.GetBytes(content));

    private static PublicApiFileSnapshot ReadSingleBytes(byte[] content)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", "<Project />");
        repository.WriteBytes("src/A/PublicAPI.Shipped.txt", content);
        var snapshot = new PublicApiSnapshotReader().Read(repository.Path, [Identity("src/A/A.csproj")]);
        return Assert.Single(snapshot.Files);
    }

    private static ProjectIdentitySnapshot Identity(string path) => new(path, "A", "1.0.0", "net10.0", "A");
}
