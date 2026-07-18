using SmartPipe.RepositoryChecks.Baselines;

namespace SmartPipe.RepositoryChecks.Tests.Baselines;

internal static class BaselineFixtures
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static BaselineManifest CreateManifest() => new()
    {
        SchemaVersion = 1,
        BaselineName = "smartpipe-core-2.1.2",
        TargetRelease = "2.2.0",
        Repository = new RepositoryBaseline
        {
            FullName = "MrFr3di/SmartPipe-Core",
            DefaultBranch = "main",
            CaptureCommitSha = "8e79902d22de714f493582946f7c260462b0895e",
            SdkVersion = "10.0.302",
            SolutionPath = "SmartPipe.Core.slnx",
            RequiredWorkflows =
            [
                new WorkflowBaseline { Name = "release", RunId = 2, HeadSha = "8e79902d22de714f493582946f7c260462b0895e", Url = new Uri("https://github.com/MrFr3di/SmartPipe-Core/actions/runs/2"), Conclusion = "success" },
                new WorkflowBaseline { Name = "ci", RunId = 1, HeadSha = "8e79902d22de714f493582946f7c260462b0895e", Url = new Uri("https://github.com/MrFr3di/SmartPipe-Core/actions/runs/1"), Conclusion = "success" },
            ],
        },
        Packages =
        [
            new PackageBaseline { Id = "SmartPipe.Extensions.Json", Version = "2.1.2", Source = new Uri("https://api.nuget.org/v3/index.json"), FileName = "SmartPipe.Extensions.Json.2.1.2.nupkg", Sha256 = Hash, RequireRepositorySignature = true },
            new PackageBaseline { Id = "SmartPipe.Core", Version = "2.1.2", Source = new Uri("https://api.nuget.org/v3/index.json"), FileName = "SmartPipe.Core.2.1.2.nupkg", Sha256 = Hash, RequireRepositorySignature = true },
            new PackageBaseline { Id = "SmartPipe.Extensions", Version = "2.1.2", Source = new Uri("https://api.nuget.org/v3/index.json"), FileName = "SmartPipe.Extensions.2.1.2.nupkg", Sha256 = Hash, RequireRepositorySignature = true },
        ],
        PublicApi = Snapshot("eng/baselines/public-api.json"),
        PackageAssets = Snapshot("eng/baselines/package-assets.json"),
        PackageDependencies = Snapshot("eng/baselines/package-dependencies.json"),
        RepositoryDependencies = Snapshot("eng/baselines/repository-dependencies.json"),
    };

    private static SnapshotReference Snapshot(string path) => new() { Path = path, Sha256 = Hash };
}
