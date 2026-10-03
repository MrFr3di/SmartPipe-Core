using SmartPipe.RepositoryChecks.Documentation;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Documentation;

public sealed class DocumentationVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsConsistentDocumentation()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task VerifyAsync_RejectsLegacyDocumentationLayout()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write("docs/plans/legacy.md", "# Legacy\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC008" && violation.Path == "docs/plans");
    }

    [Fact]
    public async Task VerifyAsync_ReportsDocumentationIndexDrift()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write("docs/index.md", "# Documentation\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC009");
    }

    [Fact]
    public async Task VerifyAsync_RejectsVersionPinnedAndRelativePackageReadmeTargets()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "src/SmartPipe.Extensions.Json/README.md",
            "# SmartPipe.Extensions.Json\n\n" +
            "dotnet package add SmartPipe.Extensions.Json --version 2.2.0\n\n" +
            "[Migration](../../docs/migration/2.2.0-integration-packages.md)\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC010");
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC011");
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingSelfRepositoryBlobMainTarget()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "SUPPORT.md",
            "# Support\n\n" +
            "[Missing](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/missing.md)\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC012"
            && violation.Path == "SUPPORT.md"
            && violation.Rule.Contains("docs/missing.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_RejectsBrokenRelativeDocumentationTarget()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "docs/maintainers/2.2.0/README.md",
            "# 2.2 maintainer index\n\n[Missing](plans/missing.md)\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC013"
            && violation.Path == "docs/maintainers/2.2.0/README.md"
            && violation.Rule.Contains("plans/missing.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_ReportsPackageAndReferenceDrift()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "src/SmartPipe.Extensions.Json/README.md",
            "# Wrong.Title\n\n```bash\ndotnet add package SmartPipe.Extensions.Json\n```\n");
        repository.Write(
            "docs/reference/packages.md",
            "# Package reference\n\n| Package | Lifecycle | Publish order | AOT contract | Direct SmartPipe dependencies |\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC003");
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC004");
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC005");
        Assert.Contains(result.Violations, violation => violation.Code == "SPDOC007");
    }


    [Fact]
    public async Task VerifyAsync_RejectsMissingCurrentReleaseDocument()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        File.Delete(repository.Resolve("docs/releases/2.2.0.md"));

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC014"
            && violation.Path == "docs/releases/2.2.0.md");
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingCurrentReleaseChangelogHeading()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write("CHANGELOG.md", "# Changelog\\n\\n## [2.1.2] — 2026-07-15\\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC014"
            && violation.Path == "CHANGELOG.md");
    }

    [Fact]
    public async Task VerifyAsync_RejectsReleasePackageTableDrift()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "docs/releases/2.2.0.md",
            "# SmartPipe 2.2.0 release notes\\n\\n" +
            "## Package selection\\n\\n" +
            "| Package | Primary purpose |\\n" +
            "|---|---|\\n" +
            "| `SmartPipe.Core` | Runtime |\\n" +
            "| `SmartPipe.Extensions.Json` | JSON |\\n\\n" +
            "## Breaking changes and migration\\n\\n" +
            "[Migration](../migration/2.2.0-integration-packages.md)\\n" +
            "[Compatibility](../reference/compatibility/2.1.2-to-2.2.0.md)\\n\\n" +
            "## Detailed changes\\n\\n" +
            "[CHANGELOG](../../CHANGELOG.md)\\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC015"
            && violation.Path == "docs/releases/2.2.0.md");
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingFirstReleasePackageInChangelog()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "CHANGELOG.md",
            "# Changelog\\n\\n" +
            "## [2.2.0] — Development\\n\\n" +
            "`SmartPipe.Core` and `SmartPipe.Extensions.Json` changes only.\\n\\n" +
            "## [2.1.2] — 2026-07-15\\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC016"
            && violation.Path == "CHANGELOG.md"
            && violation.Rule.Contains("SmartPipe.Extensions.Csv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingReleaseCrossLinks()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "docs/releases/2.2.0.md",
            "# SmartPipe 2.2.0 release notes\\n\\n" +
            "## Package selection\\n\\n" +
            "| Package | Primary purpose |\\n" +
            "|---|---|\\n" +
            "| `SmartPipe.Core` | Runtime |\\n" +
            "| `SmartPipe.Extensions.Json` | JSON |\\n" +
            "| `SmartPipe.Extensions.Csv` | CSV |\\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC017"
            && violation.Path == "docs/releases/2.2.0.md");
    }

    [Fact]
    public async Task VerifyAsync_RejectsBrokenChangelogTarget()
    {
        using var repository = new RepositoryTestDirectory();
        var graph = Graph();
        WriteRequiredDocuments(repository, graph);
        repository.Write(
            "CHANGELOG.md",
            "# Changelog\\n\\n" +
            "## [2.2.0] — Development\\n\\n" +
            "`SmartPipe.Extensions.Csv` first release. [Missing](docs/missing.md)\\n\\n" +
            "## [2.1.2] — 2026-07-15\\n");

        var result = await DocumentationVerificationService.VerifyAsync(
            repository.Path,
            graph,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "SPDOC013"
            && violation.Path == "CHANGELOG.md"
            && violation.Rule.Contains("docs/missing.md", StringComparison.Ordinal));
    }

    private static PackageGraphDocument Graph() => new()
    {
        SchemaVersion = 1,
        ReleaseVersion = "2.2.0",
        Packages =
        [
            Package(
                "SmartPipe.Core",
                "src/SmartPipe.Core/SmartPipe.Core.csproj",
                PackageLifecycle.Active,
                1,
                PackageAotContract.Full,
                [],
                "2.1.2"),
            Package(
                "SmartPipe.Extensions.Json",
                "src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj",
                PackageLifecycle.Active,
                2,
                PackageAotContract.FullJsonTypeInfo,
                ["SmartPipe.Core"],
                "2.1.2"),
            Package(
                "SmartPipe.Extensions.Csv",
                "src/SmartPipe.Extensions.Csv/SmartPipe.Extensions.Csv.csproj",
                PackageLifecycle.Active,
                3,
                PackageAotContract.VerifiedNoBlanket,
                ["SmartPipe.Core"],
                null),
        ],
    };

    private static PackageNode Package(
        string id,
        string projectPath,
        PackageLifecycle lifecycle,
        int publishOrder,
        PackageAotContract aotContract,
        IReadOnlyList<string> dependencies,
        string? baselineVersion) => new()
        {
            Id = id,
            ProjectPath = projectPath,
            Lifecycle = lifecycle,
            ActivationEpic = "test",
            ScaffoldKind = null,
            PublishOrder = publishOrder,
            BaselineVersion = baselineVersion,
            AotContract = aotContract,
            CurrentDependencies = Policy(dependencies),
            ReleaseDependencies = Policy(dependencies),
            TemporaryAllowances = [],
            ConsumerScenarios = [],
        };

    private static DependencyPolicy Policy(IReadOnlyList<string> dependencies) => new()
    {
        RequiredSmartPipePackages = dependencies,
        AllowedSmartPipePackages = [],
        AllowedExternalPackages = [],
        ForbiddenPackagePatterns = [],
    };

    private static void WriteRequiredDocuments(RepositoryTestDirectory repository, PackageGraphDocument graph)
    {
        repository.Write(
            "README.md",
            "# SmartPipe.Core\n\n```bash\ndotnet package add SmartPipe.Core\n```\n\n" +
            "[Documentation](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/index.md) " +
            "[Support](https://github.com/MrFr3di/SmartPipe-Core/blob/main/SUPPORT.md) " +
            "[Versioning](https://github.com/MrFr3di/SmartPipe-Core/blob/main/VERSIONING.md) " +
            "[Security](https://github.com/MrFr3di/SmartPipe-Core/blob/main/SECURITY.md)\n");
        repository.Write("CONTRIBUTING.md", "# Contributing\n");
        repository.Write("SECURITY.md", "# Security\n\n[Support](SUPPORT.md)\n");
        repository.Write("SUPPORT.md", "# Support\n");
        repository.Write("VERSIONING.md", "# Versioning\n");
        repository.Write(".config/dotnet-tools.json", "{\"version\":1,\"isRoot\":true,\"tools\":{\"docfx\":{\"version\":\"2.81.0\",\"commands\":[\"docfx\"]}}}\n");
        repository.Write(".github/workflows/docs.yml", "name: Documentation\njobs:\n  build:\n    runs-on: ubuntu-latest\n");
        repository.Write("docs/docfx.json", "{\"metadata\":[],\"build\":{}}\n");
        repository.Write("docs/toc.yml", "items:\n- name: Home\n  href: index.md\n");
        repository.Write(
            "docs/index.md",
            "# Documentation\n\n" +
            "[API](reference/api-overview.md) " +
            "[Compatibility](reference/compatibility/2.1.2-to-2.2.0.md) " +
            "[Maintainers](maintainers/README.md) " +
            "[2.2](maintainers/2.2.0/README.md) " +
            "[ADR](adr/README.md)\n");
        repository.Write("docs/architecture.md", "# Architecture\n");
        repository.Write("docs/runtime-contracts.md", "# Runtime contracts\n");
        repository.Write(
            "CHANGELOG.md",
            "# Changelog\\n\\n" +
            "## [2.2.0] — Development\\n\\n" +
            "First release: `SmartPipe.Extensions.Csv`.\\n\\n" +
            "## [2.1.2] — 2026-07-15\\n");
        repository.Write(
            "docs/releases/2.2.0.md",
            "# SmartPipe 2.2.0 release notes\\n\\n" +
            "## Package selection\\n\\n" +
            "| Package | Primary purpose |\\n" +
            "|---|---|\\n" +
            "| `SmartPipe.Core` | Runtime |\\n" +
            "| `SmartPipe.Extensions.Json` | JSON |\\n" +
            "| `SmartPipe.Extensions.Csv` | CSV |\\n\\n" +
            "## Breaking changes and migration\\n\\n" +
            "[Migration](../migration/2.2.0-integration-packages.md)\\n" +
            "[Compatibility](../reference/compatibility/2.1.2-to-2.2.0.md)\\n\\n" +
            "## Detailed changes\\n\\n" +
            "[CHANGELOG](../../CHANGELOG.md)\\n");
        repository.Write("docs/migration/2.2.0-integration-packages.md", "# Migration\\n");
        repository.Write("docs/reference/api-overview.md", "# API overview\n");
        repository.Write("docs/reference/compatibility/README.md", "# Compatibility reference\n");
        repository.Write("docs/reference/compatibility/2.1.2-to-2.2.0.md", "# Compatibility matrix\n");
        repository.Write("docs/adr/README.md", "# ADR index\n");
        repository.Write("docs/maintainers/README.md", "# Maintainers\n");
        repository.Write("docs/maintainers/2.2.0/README.md", "# 2.2 maintainer index\n");
        repository.Write(
            "docs/maintainers/governance/2.2.0-branch-and-review-policy.md",
            "# 2.2 branch and review policy\n");
        repository.Write(
            "src/SmartPipe.Extensions.Json/README.md",
            "# SmartPipe.Extensions.Json\n\n```bash\ndotnet package add SmartPipe.Extensions.Json\n```\n");
        repository.Write(
            "src/SmartPipe.Extensions.Csv/README.md",
            "# SmartPipe.Extensions.Csv\n\n```bash\ndotnet package add SmartPipe.Extensions.Csv\n```\n");
        repository.Write(
            "docs/reference/packages.md",
            "# Package reference\n\n" +
            DocumentationVerificationService.BuildPackageReferenceRow(graph.Packages[0]) + "\n" +
            DocumentationVerificationService.BuildPackageReferenceRow(graph.Packages[1]) + "\n" +
            DocumentationVerificationService.BuildPackageReferenceRow(graph.Packages[2]) + "\n");
    }
}
