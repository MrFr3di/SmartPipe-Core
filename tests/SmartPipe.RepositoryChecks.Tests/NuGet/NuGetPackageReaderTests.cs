using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Infrastructure;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartPipe.RepositoryChecks.Tests.NuGet;

public sealed class NuGetPackageReaderTests
{
    [Fact]
    public async Task ReadAsync_ReadsPackageIdentityFromSingleRootNuspec()
    {
        using var package = SyntheticNuGetPackage.Create();
        var reader = new NuGetPackageReader();

        var result = await reader.ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal("SmartPipe.Core", result.Id);
        Assert.Equal("2.1.2", result.Version);
    }

    [Fact]
    public async Task ReadAsync_CanonicalizesDependenciesAndFiles()
    {
        const string dependencies = """
            <dependencies>
              <group targetFramework="NET10.0">
                <dependency id="SmartPipe.Core" version="[2.1.2]" />
                <dependency id="Microsoft.Extensions.Logging.Abstractions" version="10.0.8" />
              </group>
              <group targetFramework="net8.0" />
            </dependencies>
            """;
        var readme = Encoding.UTF8.GetBytes("hello\r\n");
        using var package = SyntheticNuGetPackage.Create(
            packageId: "SmartPipe.Extensions.Json",
            entries: [("README.md", readme), ("lib/net10.0/SmartPipe.Extensions.Json.xml", [1, 2, 3])],
            nuspec: SyntheticNuGetPackage.CreateNuspec("SmartPipe.Extensions.Json", "2.1.2", dependencies));

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal(["net10.0", "net8.0"], result.Dependencies.Groups.Select(static group => group.TargetFramework));
        Assert.Equal(
            ["Microsoft.Extensions.Logging.Abstractions", "SmartPipe.Core"],
            result.Dependencies.Groups[0].Dependencies.Select(static dependency => dependency.Id));
        Assert.Equal("[2.1.2]", result.Dependencies.Groups[0].Dependencies[1].VersionRange);
        var readmeFile = Assert.Single(result.Assets.Files, static file => file.Path == "README.md");
        Assert.Equal(readme.Length, readmeFile.UncompressedLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(readme)), readmeFile.Sha256);
        Assert.Equal("readme", readmeFile.Category);
        Assert.Contains(result.Assets.Files, static file => file.Category == "xml-doc");
        Assert.Contains(result.Assets.Files, static file => file.Category == "nuspec");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("lib/../escape.txt")]
    [InlineData("lib/./asset.dll")]
    [InlineData("lib//asset.dll")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/rooted.txt")]
    [InlineData("lib/a:b.txt")]
    [InlineData("lib\\net10.0\\asset.dll")]
    [InlineData("bad\0name.txt")]
    [InlineData("bad\nname.txt")]
    public async Task ReadAsync_RejectsUnsafeArchivePaths(string unsafePath)
    {
        using var package = SyntheticNuGetPackage.Create(entries: [(unsafePath, [1])]);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, exception.ExitCode);
    }

    [Fact]
    public async Task ReadAsync_RejectsCaseInsensitiveDuplicatePaths()
    {
        using var package = SyntheticNuGetPackage.Create(entries: [("README.md", [1]), ("readme.md", [2])]);

        await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsMissingOrMultipleRootNuspecs()
    {
        using var nestedOnly = SyntheticNuGetPackage.Create(nuspecPath: "nested/package.nuspec");
        using var multiple = SyntheticNuGetPackage.Create(entries: [("second.nuspec", Encoding.UTF8.GetBytes(SyntheticNuGetPackage.CreateNuspec("Other", "1.0.0")))]);

        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(nestedOnly.Path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(multiple.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsRequestedIdentityMismatchAndDtd()
    {
        using var mismatch = SyntheticNuGetPackage.Create();
        using var dtd = SyntheticNuGetPackage.Create(nuspec: """
            <!DOCTYPE package [ <!ENTITY xxe SYSTEM "file:///does-not-exist"> ]>
            <package><metadata><id>&xxe;</id><version>2.1.2</version></metadata></package>
            """);

        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(mismatch.Path, "Other", "2.1.2", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(dtd.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsDuplicateDependencyGroupsAndIds()
    {
        var duplicateGroups = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", """
            <dependencies><group targetFramework="net10.0" /><group targetFramework=".NETCoreApp,Version=v10.0" /></dependencies>
            """);
        var duplicateIds = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", """
            <dependencies><group targetFramework="net10.0"><dependency id="A" version="1" /><dependency id="a" version="2" /></group></dependencies>
            """);
        using var groupsPackage = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: duplicateGroups);
        using var idsPackage = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: duplicateIds);

        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(groupsPackage.Path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(idsPackage.Path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v3.1", "netcoreapp3.1")]
    [InlineData(".NETCoreApp,Version=v5.0", "net5.0")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    [InlineData(".NETFramework,Version=v4.8,Profile=Client", "net48-client")]
    [InlineData("NET10.0-WINDOWS10.0.19041.0", "net10.0-windows10.0.19041.0")]
    [InlineData("net10.00", "net10.0")]
    [InlineData("netstandard2.00", "netstandard2.0")]
    [InlineData("net10.0-windows01.02", "net10.0-windows1.2")]
    [InlineData(".NETCoreApp,Version=v10.0,Profile=Windows01.02", "net10.0-windows1.2")]
    public async Task ReadAsync_CanonicalizesSupportedFrameworkIdentities(string framework, string expected)
    {
        var nuspec = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", $"""
            <dependencies><group targetFramework="{framework}" /></dependencies>
            """);
        using var package = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: nuspec);

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(result.Dependencies.Groups).TargetFramework);
    }

    [Theory]
    [InlineData("net10.00", "net10.0")]
    [InlineData("netstandard2.00", "netstandard2.0")]
    [InlineData("net10.0-windows01.02", "net10.0-windows1.2")]
    [InlineData(".NETCoreApp,Version=v10.0,Profile=Windows01.02", "net10.0-windows1.2")]
    public async Task ReadAsync_RejectsCanonicalEquivalentDependencyGroups(string first, string second)
    {
        var nuspec = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", $"""
            <dependencies><group targetFramework="{first}" /><group targetFramework="{second}" /></dependencies>
            """);
        using var package = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: nuspec);

        await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsDuplicateAssemblyIdentityAcrossCanonicalEquivalentFrameworkPaths()
    {
        var assembly = SyntheticNuGetPackage.CreateManagedAssembly("Duplicate", new Version(1, 0, 0, 0));
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/Duplicate.dll", assembly),
            ("lib/net10.00/Duplicate.dll", assembly),
        ]);

        await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(".NETCoreApp, Version=v10.0")]
    [InlineData("net1")]
    [InlineData("netcoreapp10.0")]
    [InlineData("netcoreapp2.9")]
    [InlineData("net41")]
    [InlineData("portable")]
    [InlineData("net10.0-unknown1.0")]
    [InlineData("net2147483648.0")]
    [InlineData("net10.0-windows2147483648.0")]
    [InlineData("garbage")]
    public async Task ReadAsync_RejectsUnknownOrInvalidFrameworkIdentities(string framework)
    {
        var nuspec = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", $"""
            <dependencies><group targetFramework="{framework}" /></dependencies>
            """);
        using var package = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: nuspec);

        await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd")]
    [InlineData("http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd")]
    public async Task ReadAsync_AcceptsNoNamespaceAndKnownOfficialNuspecNamespaces(string xmlNamespace)
    {
        var namespaceAttribute = xmlNamespace.Length == 0 ? string.Empty : $" xmlns=\"{xmlNamespace}\"";
        var nuspec = $"<package{namespaceAttribute}><metadata><id>Package</id><version>1.0.0</version></metadata></package>";
        using var package = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: nuspec);

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal("Package", result.Id);
    }

    [Fact]
    public async Task ReadAsync_RejectsUnknownOrMixedNuspecNamespaces()
    {
        using var unknown = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: """
            <package xmlns="urn:unknown"><metadata><id>Package</id><version>1.0.0</version></metadata></package>
            """);
        using var mixed = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: """
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd" xmlns:evil="urn:evil">
              <metadata><evil:id>Package</evil:id><version>1.0.0</version></metadata>
            </package>
            """);

        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(unknown.Path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(mixed.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsArchiveLimitOverflows()
    {
        using var package = SyntheticNuGetPackage.Create(entries: [("large.bin", new byte[64])]);
        var reader = new NuGetPackageReader(new NuGetPackageReaderOptions
        {
            MaxEntryCount = 1,
            MaxEntryUncompressedBytes = 32,
            MaxTotalUncompressedBytes = 48,
            MaxCompressionRatio = 2,
        });

        await Assert.ThrowsAsync<RepositoryCheckException>(() => reader.ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_PreflightsForgedCentralDirectoryCountBeforeZipEnumeration()
    {
        using var package = SyntheticNuGetPackage.Create();
        package.ForgeEndOfCentralDirectory(entryCount: 5000, centralDirectorySize: 1, centralDirectoryOffset: 1);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));

        Assert.Contains("preflight", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_PreflightsForgedCentralDirectoryOverflowBeforeZipEnumeration()
    {
        using var package = SyntheticNuGetPackage.Create();
        package.ForgeEndOfCentralDirectory(entryCount: 1, centralDirectorySize: uint.MaxValue - 1, centralDirectoryOffset: uint.MaxValue - 1);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));

        Assert.Contains("preflight", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_PreflightsZip64LocatorOutsideArchive()
    {
        using var package = SyntheticNuGetPackage.Create();
        package.ForgeZip64WithLocatorOutsideArchive();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));

        Assert.Contains("preflight", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_PreflightsMissingOrTruncatedZip64Locator()
    {
        using var package = SyntheticNuGetPackage.Create();
        package.ForgeEndOfCentralDirectory(
            entryCount: ushort.MaxValue,
            centralDirectorySize: uint.MaxValue,
            centralDirectoryOffset: uint.MaxValue);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));

        Assert.Contains("locator", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preflight", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_PreflightsTruncatedZip64Record()
    {
        using var package = SyntheticNuGetPackage.Create();
        package.ForgeZip64WithTruncatedRecord();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));

        Assert.Contains("preflight", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_AcceptsValidSingleDiskZip64Metadata()
    {
        using var package = SyntheticNuGetPackage.Create();
        package.ConvertToValidZip64();

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal("SmartPipe.Core", result.Id);
    }

    [Fact]
    public async Task ReadAsync_InspectsManagedAssembliesWithoutLoadingThem()
    {
        var testAssembly = await File.ReadAllBytesAsync(typeof(NuGetPackageReaderTests).Assembly.Location, TestContext.Current.CancellationToken);
        var facadePath = System.IO.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll");
        var facade = await File.ReadAllBytesAsync(facadePath, TestContext.Current.CancellationToken);
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/Tests.dll", testAssembly),
            ("ref/net10.0/System.Runtime.dll", facade),
        ]);

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Assets.Assemblies.Count);
        var tests = Assert.Single(result.Assets.Assemblies, static assembly => assembly.AssetPath == "lib/net10.0/Tests.dll");
        Assert.Contains("SmartPipe.RepositoryChecks.Tests.NuGet.NuGetPackageReaderTests", tests.ExportedTypes);
        Assert.Contains("SmartPipe.RepositoryChecks.Tests.NuGet.NuGetPackageReaderTests+PublicNestedMarker", tests.ExportedTypes);
        Assert.DoesNotContain("SmartPipe.RepositoryChecks.Tests.NuGet.InternalContainer+PublicNestedMarker", tests.ExportedTypes);
        var facadeAssembly = Assert.Single(result.Assets.Assemblies, static assembly => assembly.AssetPath.StartsWith("ref/", StringComparison.Ordinal));
        Assert.NotEmpty(facadeAssembly.TypeForwarders);
        Assert.All(result.Assets.Assemblies, static assembly => Assert.Equal("net10.0", assembly.TargetFramework));
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidManagedPeAndExactDuplicateWithinAssetFamilyAndTfm()
    {
        var testAssembly = await File.ReadAllBytesAsync(typeof(NuGetPackageReaderTests).Assembly.Location, TestContext.Current.CancellationToken);
        using var invalid = SyntheticNuGetPackage.Create(entries: [("lib/net10.0/Invalid.dll", [0x4d, 0x5a])]);
        using var duplicate = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/One.dll", testAssembly),
            ("lib/net10.0/Two.dll", testAssembly),
        ]);

        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(invalid.Path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RepositoryCheckException>(() => new NuGetPackageReader().ReadAsync(duplicate.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_AllowsRefAndLibCounterpartsAndPreservesAssetFamily()
    {
        var assembly = SyntheticNuGetPackage.CreateManagedAssembly("Counterpart", new Version(1, 2, 3, 4));
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("ref/net10.0/Counterpart.dll", assembly),
            ("lib/net10.0/Counterpart.dll", assembly),
        ]);

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal(["lib", "ref"], result.Assets.Assemblies.Select(static assembly => assembly.AssetFamily));
    }

    [Fact]
    public async Task ReadAsync_AllowsSameAssemblyNameWithDifferentFullIdentity()
    {
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/One.dll", SyntheticNuGetPackage.CreateManagedAssembly("SameName", new Version(1, 0, 0, 0))),
            ("lib/net10.0/Two.dll", SyntheticNuGetPackage.CreateManagedAssembly("SameName", new Version(2, 0, 0, 0))),
        ]);

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        Assert.Equal(["1.0.0.0", "2.0.0.0"], result.Assets.Assemblies.Select(static assembly => assembly.Version).Order());
    }

    [Fact]
    public async Task ReadAsync_DistinguishesFullPublicKeyFromStoredPublicKeyToken()
    {
        var fullKey = Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();
        var storedToken = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/FullKey.dll", SyntheticNuGetPackage.CreateManagedAssembly("FullKey", new Version(1, 0), fullKey, containsFullPublicKey: true)),
            ("lib/net10.0/StoredToken.dll", SyntheticNuGetPackage.CreateManagedAssembly("StoredToken", new Version(1, 0), storedToken)),
        ]);

        var result = await new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken);

        var hash = SHA1.HashData(fullKey);
        var expectedFullKeyToken = Convert.ToHexStringLower(hash.AsSpan(hash.Length - 8).ToArray().Reverse().ToArray());
        Assert.Equal(expectedFullKeyToken, Assert.Single(result.Assets.Assemblies, static assembly => assembly.Name == "FullKey").PublicKeyToken);
        Assert.Equal("0102030405060708", Assert.Single(result.Assets.Assemblies, static assembly => assembly.Name == "StoredToken").PublicKeyToken);
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidStoredPublicKeyTokenLength()
    {
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/BadToken.dll", SyntheticNuGetPackage.CreateManagedAssembly("BadToken", new Version(1, 0), [1, 2, 3, 4, 5, 6, 7])),
        ]);

        await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsEmptyFullPublicKeyBlob()
    {
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ("lib/net10.0/EmptyKey.dll", SyntheticNuGetPackage.CreateManagedAssembly("EmptyKey", new Version(1, 0), [], containsFullPublicKey: true)),
        ]);

        await Assert.ThrowsAsync<RepositoryCheckException>(
            () => new NuGetPackageReader().ReadAsync(package.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_IgnoresZipEntryOrderAndTimestamps()
    {
        var entries = new[] { ("README.md", Encoding.UTF8.GetBytes("readme")), ("icon.png", new byte[] { 1, 2 }) };
        using var first = SyntheticNuGetPackage.Create(entries: entries, timestamp: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var second = SyntheticNuGetPackage.Create(entries: entries.Reverse(), timestamp: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), nuspecLast: true);

        var reader = new NuGetPackageReader();
        var firstResult = await reader.ReadAsync(first.Path, TestContext.Current.CancellationToken);
        var secondResult = await reader.ReadAsync(second.Path, TestContext.Current.CancellationToken);

        Assert.Equal(JsonSerializer.Serialize(firstResult), JsonSerializer.Serialize(secondResult));
    }

    [Fact]
    public async Task ReadAsync_XmlFormattingPreservesDependencySemanticsButChangesEntryHash()
    {
        var compact = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", "<dependencies><group targetFramework=\"net10.0\"><dependency id=\"A\" version=\"1\" /></group></dependencies>");
        var formatted = SyntheticNuGetPackage.CreateNuspec("Package", "1.0.0", """
            <dependencies>
              <group targetFramework="net10.0">
                <dependency id="A" version="1" />
              </group>
            </dependencies>
            """);
        using var first = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: compact);
        using var second = SyntheticNuGetPackage.Create("Package", "1.0.0", nuspec: formatted);

        var reader = new NuGetPackageReader();
        var firstResult = await reader.ReadAsync(first.Path, TestContext.Current.CancellationToken);
        var secondResult = await reader.ReadAsync(second.Path, TestContext.Current.CancellationToken);

        Assert.Equal(JsonSerializer.Serialize(firstResult.Dependencies), JsonSerializer.Serialize(secondResult.Dependencies));
        Assert.NotEqual(
            Assert.Single(firstResult.Assets.Files, static file => file.Category == "nuspec").Sha256,
            Assert.Single(secondResult.Assets.Files, static file => file.Category == "nuspec").Sha256);
    }

    public sealed class PublicNestedMarker;
}

internal sealed class InternalContainer
{
    public sealed class PublicNestedMarker;
}
