using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

public sealed class EvaluatedProjectReaderTests
{
    [Fact]
    public async Task ReadAsync_ResolvesProjectReferenceIdentityWhenFullPathIsRedacted()
    {
        var projectPath = Path.Combine(
            Path.GetTempPath(),
            "sp220-evaluated-project",
            "src",
            "SmartPipe.Extensions",
            "SmartPipe.Extensions.csproj");
        var expectedProjectReference = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "..",
            "SmartPipe.Core",
            "SmartPipe.Core.csproj"));

        var project = await new EvaluatedProjectReader(new StubProcessRunner())
            .ReadAsync(projectPath, TestContext.Current.CancellationToken);

        Assert.Equal(expectedProjectReference, Assert.Single(project.ProjectReferences));
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(
                0,
                """
                {
                  "Properties": {
                    "PackageId": "SmartPipe.Extensions",
                    "Version": "2.2.0",
                    "PackageVersion": "2.2.0",
                    "TargetFramework": "net10.0",
                    "TargetFrameworks": "",
                    "SmartPipePackage": "true",
                    "IsPackable": "true",
                    "PackageValidationBaselineVersion": "",
                    "IsAotCompatible": "true",
                    "PackageReadmeFile": "",
                    "SmartPipePackageReadmeSource": "",
                    "PackageIcon": ""
                  },
                  "Items": {
                    "PackageReference": [],
                    "ProjectReference": [
                      {
                        "Identity": "../SmartPipe.Core/SmartPipe.Core.csproj",
                        "FullPath": "<home>/work/SmartPipe.Core/src/SmartPipe.Core/SmartPipe.Core.csproj"
                      }
                    ]
                  }
                }
                """,
                string.Empty));
    }
}
