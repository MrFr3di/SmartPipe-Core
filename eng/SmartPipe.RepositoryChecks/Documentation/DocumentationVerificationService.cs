using System.Text.RegularExpressions;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Documentation;

internal sealed record DocumentationViolation(string Code, string Path, string Rule);

internal sealed record DocumentationVerificationResult(IReadOnlyList<DocumentationViolation> Violations)
{
    public bool Success => Violations.Count == 0;
}

internal sealed class DocumentationVerificationService
{
    private static readonly string[] RequiredRepositoryDocuments =
    [
        "README.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "SUPPORT.md",
        "VERSIONING.md",
        "docs/index.md",
        "docs/architecture.md",
        "docs/runtime-contracts.md",
        "docs/reference/packages.md",
        "docs/reference/api-overview.md",
        "docs/reference/compatibility/README.md",
        "docs/reference/compatibility/2.1.2-to-2.2.0.md",
        "docs/adr/README.md",
        "docs/maintainers/README.md",
        "docs/maintainers/2.2.0/README.md",
        "docs/maintainers/governance/2.2.0-branch-and-review-policy.md",
    ];

    private static readonly string[] LegacyDocumentationDirectories =
    [
        "docs/plans",
        "docs/implementation",
        "docs/governance",
    ];

    private static readonly Regex RelativeMarkdownTargetRegex = new(
        @"!?\[[^\]]+\]\((?!https?://|mailto:|#)([^)]+)\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] RequiredDocumentationIndexLinks =
    [
        "(reference/api-overview.md)",
        "(reference/compatibility/2.1.2-to-2.2.0.md)",
        "(maintainers/README.md)",
        "(maintainers/2.2.0/README.md)",
        "(adr/README.md)",
    ];

    private readonly PackageGraphLoader _graphLoader;

    public DocumentationVerificationService(PackageGraphLoader? graphLoader = null) =>
        _graphLoader = graphLoader ?? new PackageGraphLoader();

    public async Task<DocumentationVerificationResult> VerifyAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var graph = await _graphLoader
            .LoadAsync(root, "eng/package-graph.json", cancellationToken)
            .ConfigureAwait(false);

        return await VerifyAsync(root, graph, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<DocumentationVerificationResult> VerifyAsync(
        string repositoryRoot,
        PackageGraphDocument graph,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var violations = new List<DocumentationViolation>();

        foreach (var relativePath in RequiredRepositoryDocuments)
        {
            if (!File.Exists(Resolve(root, relativePath)))
            {
                violations.Add(new("SPDOC001", relativePath, "required repository documentation is missing"));
            }
        }

        foreach (var legacyDirectory in LegacyDocumentationDirectories)
        {
            if (Directory.Exists(Resolve(root, legacyDirectory)))
            {
                violations.Add(new(
                    "SPDOC008",
                    legacyDirectory,
                    "legacy documentation layout is forbidden; use docs/maintainers or docs/reference"));
            }
        }

        foreach (var package in graph.Packages.Where(package => package.Lifecycle != PackageLifecycle.Planned))
        {
            var readmePath = PackageReadmePath(package);
            var fullReadmePath = Resolve(root, readmePath);
            if (!File.Exists(fullReadmePath))
            {
                violations.Add(new("SPDOC002", readmePath, $"package README is missing for {package.Id}"));
                continue;
            }

            var content = await File.ReadAllTextAsync(fullReadmePath, cancellationToken).ConfigureAwait(false);
            var normalized = content.TrimStart('﻿', '\r', '\n', ' ', '\t');
            var lineBreak = normalized.IndexOf('\n');
            var firstLine = (lineBreak >= 0 ? normalized[..lineBreak] : normalized).TrimEnd('\r');
            if (!string.Equals(firstLine, $"# {package.Id}", StringComparison.Ordinal))
            {
                violations.Add(new("SPDOC003", readmePath, $"first heading must be '# {package.Id}'"));
            }

            var installCommand = $"dotnet package add {package.Id}";
            if (!content.Contains(installCommand, StringComparison.Ordinal))
            {
                violations.Add(new("SPDOC004", readmePath, $"README must contain installation command '{installCommand}'"));
            }

            if (content.Contains("dotnet add package ", StringComparison.Ordinal))
            {
                violations.Add(new("SPDOC005", readmePath, "use the .NET 10 noun-first 'dotnet package add' form"));
            }

            if (content.Contains($"{installCommand} --version ", StringComparison.Ordinal))
            {
                violations.Add(new(
                    "SPDOC010",
                    readmePath,
                    "package README install commands must stay version-agnostic; release-specific versions belong in release/migration documentation"));
            }

            if (RelativeMarkdownTargetRegex.IsMatch(content))
            {
                violations.Add(new(
                    "SPDOC011",
                    readmePath,
                    "package README links/images must use absolute URLs or in-document anchors so NuGet.org rendering is independent of repository-relative paths"));
            }
        }

        var rootReadmePath = Resolve(root, "README.md");
        if (File.Exists(rootReadmePath))
        {
            var rootReadme = await File.ReadAllTextAsync(rootReadmePath, cancellationToken).ConfigureAwait(false);
            foreach (var target in new[] { "docs/index.md", "SUPPORT.md", "VERSIONING.md", "SECURITY.md" })
            {
                if (!rootReadme.Contains(target, StringComparison.Ordinal))
                {
                    violations.Add(new("SPDOC006", "README.md", $"root README must link to {target}"));
                }
            }
        }

        var documentationIndexPath = Resolve(root, "docs/index.md");
        if (File.Exists(documentationIndexPath))
        {
            var documentationIndex = await File.ReadAllTextAsync(documentationIndexPath, cancellationToken).ConfigureAwait(false);
            foreach (var target in RequiredDocumentationIndexLinks)
            {
                if (!documentationIndex.Contains(target, StringComparison.Ordinal))
                {
                    violations.Add(new("SPDOC009", "docs/index.md", $"documentation index must link to {target[1..^1]}"));
                }
            }
        }

        var securityPath = Resolve(root, "SECURITY.md");
        if (File.Exists(securityPath))
        {
            var security = await File.ReadAllTextAsync(securityPath, cancellationToken).ConfigureAwait(false);
            if (!security.Contains("(SUPPORT.md)", StringComparison.Ordinal))
            {
                violations.Add(new("SPDOC006", "SECURITY.md", "security policy must delegate support status to SUPPORT.md"));
            }
        }

        var packageReferencePath = Resolve(root, "docs/reference/packages.md");
        if (File.Exists(packageReferencePath))
        {
            var packageReference = await File.ReadAllTextAsync(packageReferencePath, cancellationToken).ConfigureAwait(false);
            foreach (var package in graph.Packages.Where(package => package.Lifecycle != PackageLifecycle.Planned))
            {
                var expectedRow = BuildPackageReferenceRow(package);
                if (!packageReference.Contains(expectedRow, StringComparison.Ordinal))
                {
                    violations.Add(new(
                        "SPDOC007",
                        "docs/reference/packages.md",
                        $"package graph projection is stale for {package.Id}; expected row: {expectedRow}"));
                }
            }
        }

        return new(violations);
    }

    internal static string BuildPackageReferenceRow(PackageNode package)
    {
        var dependencies = package.CurrentDependencies.RequiredSmartPipePackages
            .Concat(package.CurrentDependencies.AllowedSmartPipePackages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dependency => $"`{dependency}`")
            .ToArray();

        var dependencyText = dependencies.Length == 0 ? "—" : string.Join(", ", dependencies);
        return $"| `{package.Id}` | {Lifecycle(package.Lifecycle)} | {package.PublishOrder} | `{AotContract(package.AotContract)}` | {dependencyText} |";
    }

    private static string PackageReadmePath(PackageNode package)
    {
        if (package.Id.Equals("SmartPipe.Core", StringComparison.Ordinal))
        {
            return "README.md";
        }

        var projectDirectory = Path.GetDirectoryName(package.ProjectPath)
            ?? throw new InvalidOperationException($"Package project path has no directory: {package.ProjectPath}");
        return Path.Combine(projectDirectory, "README.md").Replace('\\', '/');
    }

    private static string Lifecycle(PackageLifecycle lifecycle) => lifecycle switch
    {
        PackageLifecycle.Active => "active",
        PackageLifecycle.Planned => "planned",
        PackageLifecycle.CompatibilityFacade => "compatibility-facade",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, null),
    };

    private static string AotContract(PackageAotContract contract) => contract switch
    {
        PackageAotContract.Full => "full",
        PackageAotContract.FullJsonTypeInfo => "full-json-type-info",
        PackageAotContract.TransportFull => "transport-full",
        PackageAotContract.ExplicitSql => "explicit-sql",
        PackageAotContract.Verified => "verified",
        PackageAotContract.VerifiedNoBlanket => "verified-no-blanket",
        PackageAotContract.AnnotatedReflection => "annotated-reflection",
        PackageAotContract.UnsupportedBlanket => "unsupported-blanket",
        PackageAotContract.NotRuntime => "not-runtime",
        PackageAotContract.NoBlanket => "no-blanket",
        _ => throw new ArgumentOutOfRangeException(nameof(contract), contract, null),
    };

    private static string Resolve(string root, string relativePath) =>
        Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
}
