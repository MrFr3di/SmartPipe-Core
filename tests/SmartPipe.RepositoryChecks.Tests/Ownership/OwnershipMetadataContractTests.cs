using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.NuGet;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Ownership;

public sealed class OwnershipMetadataContractTests
{
    private const string TypeName = "SmartPipe.Extensions.Moved`1";
    private const string Facade = "SmartPipe.Extensions";
    private const string Leaf = "SmartPipe.Extensions.Json";

    [Theory]
    [InlineData(Leaf, true)]
    [InlineData("SmartPipe.Extensions.Wrong", false)]
    public async Task ForwarderMustPointToTheDeclaredImplementation(string destination, bool accepted)
    {
        using var fixture = new RepositoryTestDirectory();
        WritePackage(fixture, Facade, [("lib/net10.0/Facade.dll", Assembly(Facade, forwardTo: destination))]);
        WritePackage(fixture, Leaf, [("lib/net10.0/Leaf.dll", Assembly(Leaf, implement: true))]);

        var result = await ValidateAsync(fixture);

        Assert.Equal(accepted, result.Success);
        if (!accepted) Assert.Contains(result.Violations, violation => violation.Code == "SPOWN026" && violation.Type == TypeName);
    }

    [Fact]
    public async Task TargetInAnotherTfmCannotSatisfyTheForwarder()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePackage(fixture, Facade, [("lib/net10.0/Facade.dll", Assembly(Facade, forwardTo: Leaf))]);
        WritePackage(fixture, Leaf, [("lib/net9.0/Leaf.dll", Assembly(Leaf, implement: true))]);

        var result = await ValidateAsync(fixture);

        Assert.Contains(result.Violations, violation => violation.Code == "SPOWN029");
    }

    [Fact]
    public async Task SecondFacadeAssetCannotHideMissingForwarder()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePackage(fixture, Facade,
        [
            ("lib/net10.0/Facade.dll", Assembly(Facade, forwardTo: Leaf)),
            ("ref/net10.0/Facade.dll", Assembly(Facade)),
        ]);
        WritePackage(fixture, Leaf, [("lib/net10.0/Leaf.dll", Assembly(Leaf, implement: true))]);

        var result = await ValidateAsync(fixture);

        Assert.Contains(result.Violations, violation => violation.Code == "SPOWN029" && violation.Rule.Contains("ref/net10.0/Facade.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedGenericExportFollowsParentForwarder()
    {
        var snapshot = ManagedAssemblyInspector.Inspect("lib/net10.0/Facade.dll", "lib", "net10.0",
            Assembly(Facade, forwardTo: Leaf, nested: true));

        Assert.Equal([TypeName, TypeName + "+Nested`1"], snapshot.TypeForwarders);
    }

    private static async Task<OwnershipResult> ValidateAsync(RepositoryTestDirectory fixture)
    {
        var current = await new TypeForwarderReader().ReadPackagesAsync(fixture.Path, [Facade, Leaf], "2.2.0", TestContext.Current.CancellationToken);
        var baseline = FacadeSurfaceContractTests.Snapshot([TypeName], []);
        return new OwnershipValidator().Validate(FacadeSurfaceContractTests.Document(TypeName, Leaf),
            FacadeSurfaceContractTests.Graph(), baseline, current, PackageGraphMode.Release);
    }

    private static void WritePackage(RepositoryTestDirectory fixture, string id, (string Path, byte[] Content)[] entries)
    {
        using var package = SyntheticNuGetPackage.Create(id, "2.2.0", entries);
        File.Copy(package.Path, Path.Combine(fixture.Path, id + ".2.2.0.nupkg"));
    }

    private static byte[] Assembly(string name, bool implement = false, string? forwardTo = null, bool nested = false)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(2, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        if (implement)
            metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("SmartPipe.Extensions"), metadata.GetOrAddString("Moved`1"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        if (forwardTo is not null)
        {
            var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(forwardTo), new Version(2, 0, 0, 0), default, default, 0, default);
            var parent = metadata.AddExportedType(TypeAttributes.Public | (TypeAttributes)0x00200000,
                metadata.GetOrAddString("SmartPipe.Extensions"), metadata.GetOrAddString("Moved`1"), reference, 0);
            if (nested)
                metadata.AddExportedType(TypeAttributes.NestedPublic, default, metadata.GetOrAddString("Nested`1"), parent, 0);
        }
        var builder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        builder.Serialize(image);
        return image.ToArray();
    }
}
