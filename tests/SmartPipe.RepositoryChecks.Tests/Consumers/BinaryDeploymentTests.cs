using System.Reflection;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Tests.NuGet;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Consumers;

[Collection(ExternalProcessCollection.Name)]
public sealed class BinaryDeploymentTests
{
    [Theory]
    [InlineData("runtime", "lib/net10.0/External.dll", "External.dll", "")]
    [InlineData("native", "runtimes/linux-x64/native/external.so", "external.so", "")]
    [InlineData("resources", "lib/net10.0/fr/External.resources.dll", "fr/External.resources.dll", "fr")]
    [InlineData("runtimeTargets", "runtimes/linux-x64/native/external.so", "runtimes/linux-x64/native/external.so", "")]
    public async Task BinaryRuntimeInventoryRejectsMissingDependency(string kind, string asset, string deployed, string locale)
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("Consumer.runtimeconfig.json", "{}");
        fixture.Write("Consumer.deps.json", JsonSerializer.Serialize(new
        {
            runtimeTarget = new { name = "net10.0" },
            targets = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object>
                {
                    ["External/2.0.0"] = new Dictionary<string, object>
                    {
                        [kind] = new Dictionary<string, object> { [asset] = new { locale, assetType = "native", rid = "linux-x64" } },
                    },
                },
            },
        }));
        var method = typeof(ConsumerScenarioRunner).GetMethod("InspectRuntimeArtifactsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        Task Inspect() => (Task)method.Invoke(null, [fixture.Path, ConsumerMode.BinaryCompatibility, TestContext.Current.CancellationToken])!;

        var exception = await Assert.ThrowsAsync<ConsumerScenarioException>(Inspect);
        Assert.Equal("SPCONS011", exception.Code);
        var normalizedDeployed = deployed.Replace('\\', '/');
        var normalizedMessage = exception.Message.Replace('\\', '/');
        Assert.Contains(normalizedDeployed, normalizedMessage, StringComparison.Ordinal);
        fixture.Write(deployed, "runtime asset");
        await Inspect();
    }

    [Theory]
    [InlineData("2.1.2", false)]
    [InlineData("2.2.0", true)]
    public async Task DeploymentRefreshRejectsStaleOrIncompleteSmartPipeClosure(string restoredVersion, bool missingLeaf)
    {
        using var fixture = new RepositoryTestDirectory();
        var project = fixture.Write("source/Consumer.csproj", "<Project />");
        var output = Path.Combine(fixture.Path, "source/bin/Release/net10.0");
        fixture.Write("source/bin/Release/net10.0/Consumer.dll", "baseline binary");
        var libraries = new Dictionary<string, object> { [$"SmartPipe.Core/{restoredVersion}"] = new { type = "package" } };
        if (!missingLeaf) libraries[$"SmartPipe.Extensions.Json/{restoredVersion}"] = new { type = "package" };
        fixture.Write("source/obj/project.assets.json", JsonSerializer.Serialize(new { libraries }));
        var stdout = fixture.Write("logs/stdout.log", "");
        var stderr = fixture.Write("logs/stderr.log", "");
        var process = new FakeProcessRunner(new ProcessResult(0, "", "", stdout, stderr), new ProcessResult(0, "", "", stdout, stderr));

        var exception = await Assert.ThrowsAsync<ConsumerScenarioException>(() =>
            new ConsumerScenarioRunner(new DotNetProcessRunner(process)).RefreshBinaryCompatibilityDeploymentMetadataAsync(
                fixture.Path, project, output, ["SmartPipe.Core", "SmartPipe.Extensions.Json"], Path.Combine(fixture.Path, "feed"),
                "2.2.0", new Dictionary<string, string>(), ["Fixture.*"], fixture.Path, TimeSpan.FromMinutes(1), [], TestContext.Current.CancellationToken));

        Assert.Equal("SPCONS015", exception.Code);
    }

    [Fact]
    public async Task DeploymentRefreshRejectsStaleDepsEvenWhenRestoreIsCurrent()
    {
        using var fixture = new RepositoryTestDirectory();
        var project = fixture.Write("source/Consumer.csproj", "<Project />");
        var output = Path.Combine(fixture.Path, "source/bin/Release/net10.0");
        fixture.Write("source/bin/Release/net10.0/Consumer.dll", "baseline binary");
        fixture.Write("source/obj/project.assets.json", """{"libraries":{"SmartPipe.Core/2.2.0":{}}}""");
        fixture.Write("source/bin/Release/net10.0/Consumer.deps.json", """{"libraries":{"SmartPipe.Core/2.1.2":{}}}""");
        var stdout = fixture.Write("logs/stdout.log", "");
        var stderr = fixture.Write("logs/stderr.log", "");
        var process = new FakeProcessRunner(new ProcessResult(0, "", "", stdout, stderr), new ProcessResult(0, "", "", stdout, stderr));

        var exception = await Assert.ThrowsAsync<ConsumerScenarioException>(() =>
            new ConsumerScenarioRunner(new DotNetProcessRunner(process)).RefreshBinaryCompatibilityDeploymentMetadataAsync(
                fixture.Path, project, output, ["SmartPipe.Core"], Path.Combine(fixture.Path, "feed"),
                "2.2.0", new Dictionary<string, string>(), ["Fixture.*"], fixture.Path, TimeSpan.FromMinutes(1), [], TestContext.Current.CancellationToken));

        Assert.Equal("SPCONS015", exception.Code);
    }

    [Fact]
    public async Task CurrentDeploymentCopiesTransitiveManagedNativeAndResourceAssetsWithoutRecompiling()
    {
        using var fixture = new RepositoryTestDirectory();
        var ct = TestContext.Current.CancellationToken;
        var feed = Path.Combine(fixture.Path, "feed");
        Directory.CreateDirectory(feed);
        var currentDependency = SyntheticNuGetPackage.CreateManagedAssembly("Fixture.External", new Version(2, 0, 0, 0));
        foreach (var version in new[] { "1.0.0", "2.2.0" })
        {
            using var dependency = SyntheticNuGetPackage.Create("SmartPipe.Fixture.Dependency", version,
            [
                ("lib/net10.0/Fixture.External.dll", version == "2.2.0" ? currentDependency : SyntheticNuGetPackage.CreateManagedAssembly("Fixture.External", new Version(1, 0, 0, 0))),
                ("lib/net10.0/fr/Fixture.External.resources.dll", SyntheticNuGetPackage.CreateManagedAssembly("Fixture.External.resources", new Version(1, 0, 0, 0))),
                ("runtimes/linux-x64/native/fixture-native.dat", new byte[] { 2, 4, 6 }),
                ("runtimes/win-x64/native/fixture-native.dat", new byte[] { 1, 3, 5 }),
            ]);
            File.Copy(dependency.Path, Path.Combine(feed, Path.GetFileName(dependency.Path)));
        }
        foreach (var version in new[] { "1.0.0", "2.2.0" })
        {
            using var package = SyntheticNuGetPackage.Create("SmartPipe.Core", version,
                [("lib/net10.0/SmartPipe.Core.dll", SyntheticNuGetPackage.CreateManagedAssembly("SmartPipe.Core", new Version(1, 0, 0, 0)))],
                SyntheticNuGetPackage.CreateNuspec("SmartPipe.Core", version,
                    $"<dependencies><dependency id='SmartPipe.Fixture.Dependency' version='{version}' /></dependencies>"));
            File.Copy(package.Path, Path.Combine(feed, Path.GetFileName(package.Path)));
        }
        var project = fixture.Write("source/Consumer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><NuGetAudit>false</NuGetAudit></PropertyGroup>
              <ItemGroup><PackageReference Include="SmartPipe.Core" /></ItemGroup>
              <Target Name="RejectRecompilation" BeforeTargets="CoreCompile" Condition="Exists('do-not-compile')">
                <Error Text="Old binary must never be recompiled." />
              </Target>
            </Project>
            """);
        fixture.Write("source/Program.cs", "System.Console.WriteLine(\"baseline binary\");");
        await new ConsumerCentralPackagesWriter().WriteAsync(fixture.Path, ["SmartPipe.Core"], "1.0.0", new Dictionary<string, string>(), ct);
        var config = await new LocalNuGetConfigWriter().WriteAsync(fixture.Path, feed, ct, fixture.Path, ["Fixture.*"]);
        var process = new ProcessRunner();
        foreach (var arguments in new IReadOnlyList<string>[]
        {
            ["restore", project, "--configfile", config, "--packages", Path.Combine(fixture.Path, "packages")],
            ["build", project, "-c", "Release", "--no-restore"],
        })
        {
            var result = await process.RunAsync(new("dotnet", arguments, TimeSpan.FromMinutes(1), Path.GetDirectoryName(project), Path.Combine(fixture.Path, "logs")), ct);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        }
        var output = Path.Combine(fixture.Path, "source", "bin", "Release", "net10.0");
        var original = await File.ReadAllBytesAsync(Path.Combine(output, "Consumer.dll"), ct);
        fixture.Write("source/do-not-compile", "");
        File.Delete(Path.Combine(output, "fr", "Fixture.External.resources.dll"));
        Directory.Delete(Path.Combine(output, "runtimes"), recursive: true);

        await new ConsumerScenarioRunner().RefreshBinaryCompatibilityDeploymentMetadataAsync(
            fixture.Path, project, output, ["SmartPipe.Core", "SmartPipe.Fixture.Dependency"], feed, "2.2.0", new Dictionary<string, string>(), ["Fixture.*"],
            fixture.Path, TimeSpan.FromMinutes(1), [], ct);

        Assert.Equal(currentDependency, await File.ReadAllBytesAsync(Path.Combine(output, "Fixture.External.dll"), ct));
        Assert.True(File.Exists(Path.Combine(output, "fr", "Fixture.External.resources.dll")));
        Assert.Equal(new byte[] { 2, 4, 6 }, await File.ReadAllBytesAsync(Path.Combine(output, "runtimes", "linux-x64", "native", "fixture-native.dat"), ct));
        Assert.Equal(new byte[] { 1, 3, 5 }, await File.ReadAllBytesAsync(Path.Combine(output, "runtimes", "win-x64", "native", "fixture-native.dat"), ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(output, "Consumer.dll"), ct));
    }
}
