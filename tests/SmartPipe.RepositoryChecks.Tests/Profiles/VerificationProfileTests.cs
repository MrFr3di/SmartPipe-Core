using SmartPipe.RepositoryChecks.Reporting;
using SmartPipe.RepositoryChecks.Profiles;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Profiles;

public sealed class VerificationProfileTests
{
    [Fact]
    public async Task Runner_PreservesManifestOrderContinuesAfterFailureAndReturnsFirstExitCode()
    {
        var calls = new List<string>();
        var delegates = new Dictionary<string, Func<CancellationToken, Task<CheckRun>>>(StringComparer.Ordinal)
        {
            ["first"] = _ => Task.FromResult(new CheckRun("first", null, false, 23, [new CheckDiagnostic("E1", "first failure")])),
            ["second"] = _ => Task.FromResult(new CheckRun("second", null, true, 0, [])),
            ["third"] = _ => Task.FromResult(new CheckRun("third", null, false, 22, [new CheckDiagnostic("E3", "third failure")])),
        };
        var runner = new VerificationProfileRunner(delegates.ToDictionary(
            pair => pair.Key,
            pair => new Func<CancellationToken, Task<CheckRun>>(async ct =>
            {
                calls.Add(pair.Key);
                return await pair.Value(ct);
            }),
            StringComparer.Ordinal));

        var result = await runner.RunAsync(new VerificationProfile("profile", ["first", "second", "third"]), CancellationToken.None);

        Assert.Equal(["first", "second", "third"], calls);
        Assert.Equal(23, result.ExitCode);
        Assert.Equal(["first", "second", "third"], result.CheckRuns.Select(run => run.Check));
        Assert.Equal([23, 0, 22], result.CheckRuns.Select(run => run.ExitCode));
        Assert.All(result.CheckRuns, run => Assert.Equal("profile", run.Profile));
    }

    [Fact]
    public async Task Runner_UnknownCheckFailsClosed()
    {
        var runner = new VerificationProfileRunner(new Dictionary<string, Func<CancellationToken, Task<CheckRun>>>(StringComparer.Ordinal));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new VerificationProfile("profile", ["unknown-check"]), TestContext.Current.CancellationToken));

        Assert.Contains("unknown-check", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockFileAdapter_MapsCodeSummaryAndRelativePath()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        fixture.Write("NuGet.Config", "<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
        fixture.Write("src/Fixture/Fixture.csproj", "<Project />");

        var run = await VerificationProfileChecks.Create(fixture.Path)["verify-lock-files"](
            TestContext.Current.CancellationToken);
        var diagnostic = Assert.Single(run.Diagnostics, item => item.Code == "SPLOCK001");

        Assert.Equal("src/Fixture/Fixture.csproj", diagnostic.Path);
        Assert.Equal("tracked lock file is missing", diagnostic.Summary);
    }

    [Fact]
    public async Task Runner_CancellationStopsBeforeNextCheck()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var runner = new VerificationProfileRunner(new Dictionary<string, Func<CancellationToken, Task<CheckRun>>>(StringComparer.Ordinal)
        {
            ["first"] = _ =>
            {
                calls.Add("first");
                cancellation.Cancel();
                return Task.FromResult(new CheckRun("first", null, true, 0, []));
            },
            ["second"] = _ =>
            {
                calls.Add("second");
                return Task.FromResult(new CheckRun("second", null, true, 0, []));
            },
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            new VerificationProfile("profile", ["first", "second"]), cancellation.Token));

        Assert.Equal(["first"], calls);
    }
}
