using System.Globalization;
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
        ".config/dotnet-tools.json",
        ".github/workflows/docs.yml",
        "docs/docfx.json",
        "docs/toc.yml",
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

    private static readonly Regex SelfRepositoryBlobMainTargetRegex = new(
        @"https://github\.com/MrFr3di/SmartPipe-Core/blob/main/(?<path>[^)\s#?]+)(?:[?#][^)\s]+)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionHeadingRegex = new(
        @"^## \[[^\]]+\] — .+$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex LevelTwoHeadingRegex = new(
        @"^##\s+.+$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ReleasePackageRowRegex = new(
        @"^\|\s*`(?<id>SmartPipe(?:\.[A-Za-z0-9]+)*)`\s*\|",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly string[] LinkCheckedRootDocuments =
    [
        "README.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "SUPPORT.md",
        "VERSIONING.md",
        "CHANGELOG.md",
    ];

    private static readonly string[] RequiredDocumentationIndexLinks =
    [
        "reference/api-overview.md",
        "reference/compatibility/2.1.2-to-2.2.0.md",
        "maintainers/README.md",
        "maintainers/2.2.0/README.md",
        "adr/README.md",
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

        await ValidateSelfRepositoryLinksAsync(root, violations, cancellationToken).ConfigureAwait(false);

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
                    violations.Add(new("SPDOC009", "docs/index.md", $"documentation index must link to {target}"));
                }
            }
        }

        var securityPath = Resolve(root, "SECURITY.md");
        if (File.Exists(securityPath))
        {
            var security = await File.ReadAllTextAsync(securityPath, cancellationToken).ConfigureAwait(false);
            if (!security.Contains("SUPPORT.md", StringComparison.Ordinal))
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

        await ValidateReleaseDocumentationAsync(root, graph, violations, cancellationToken).ConfigureAwait(false);

        return new(violations);
    }

    private static async Task ValidateReleaseDocumentationAsync(
        string root,
        PackageGraphDocument graph,
        List<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        var releaseVersion = graph.ReleaseVersion;
        var changelogRelativePath = "CHANGELOG.md";
        var releaseRelativePath = $"docs/releases/{releaseVersion}.md";
        var changelogPath = Resolve(root, changelogRelativePath);
        var releasePath = Resolve(root, releaseRelativePath);

        string? currentReleaseSection = null;
        if (!File.Exists(changelogPath))
        {
            violations.Add(new(
                "SPDOC014",
                changelogRelativePath,
                $"current release changelog is missing for {releaseVersion}"));
        }
        else
        {
            var changelog = await File.ReadAllTextAsync(changelogPath, cancellationToken).ConfigureAwait(false);
            var currentHeadingRegex = new Regex(
                $@"^## \[{Regex.Escape(releaseVersion)}\] — (?<state>Development|\d{{4}}-\d{{2}}-\d{{2}})\s*$",
                RegexOptions.CultureInvariant | RegexOptions.Multiline);
            var currentHeadingCandidates = Regex.Matches(
                changelog,
                $@"^## \[{Regex.Escape(releaseVersion)}\] — [^\r\n]+\s*$",
                RegexOptions.CultureInvariant | RegexOptions.Multiline);
            var currentHeading = currentHeadingRegex.Match(changelog);

            var releaseState = currentHeading.Success
                ? currentHeading.Groups["state"].Value
                : string.Empty;
            var hasValidReleaseState = string.Equals(releaseState, "Development", StringComparison.Ordinal)
                || DateOnly.TryParseExact(
                    releaseState,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _);

            if (currentHeadingCandidates.Count != 1 || !currentHeading.Success || !hasValidReleaseState)
            {
                violations.Add(new(
                    "SPDOC014",
                    changelogRelativePath,
                    $"current release changelog must contain exactly one heading '## [{releaseVersion}] — Development' or a valid ISO yyyy-MM-dd release date"));
            }
            else
            {
                currentReleaseSection = ExtractSectionAfterHeading(changelog, currentHeading, VersionHeadingRegex);

                foreach (var package in graph.Packages.Where(package =>
                             package.Lifecycle != PackageLifecycle.Planned
                             && package.BaselineVersion is null))
                {
                    if (!currentReleaseSection.Contains($"`{package.Id}`", StringComparison.Ordinal))
                    {
                        violations.Add(new(
                            "SPDOC016",
                            changelogRelativePath,
                            $"first-release package must be explicitly named in the current release section: {package.Id}"));
                    }
                }
            }
        }

        if (!File.Exists(releasePath))
        {
            violations.Add(new(
                "SPDOC014",
                releaseRelativePath,
                $"current release notes are missing for {releaseVersion}"));
            return;
        }

        var releaseNotes = await File.ReadAllTextAsync(releasePath, cancellationToken).ConfigureAwait(false);
        var packageSection = ExtractLevelTwoSection(releaseNotes, "## Package selection");
        if (packageSection is null)
        {
            violations.Add(new(
                "SPDOC015",
                releaseRelativePath,
                "release notes must contain a '## Package selection' section projected from the package graph"));
        }
        else
        {
            var expected = graph.Packages
                .Where(package => package.Lifecycle != PackageLifecycle.Planned)
                .Select(package => package.Id)
                .ToHashSet(StringComparer.Ordinal);

            var actual = ReleasePackageRowRegex.Matches(packageSection)
                .Select(match => match.Groups["id"].Value)
                .ToArray();
            var actualSet = actual.ToHashSet(StringComparer.Ordinal);
            var missing = expected.Except(actualSet, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var unknown = actualSet.Except(expected, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var duplicates = actual
                .GroupBy(id => id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            if (missing.Length != 0 || unknown.Length != 0 || duplicates.Length != 0)
            {
                violations.Add(new(
                    "SPDOC015",
                    releaseRelativePath,
                    $"release package table must match the non-planned package graph exactly; missing=[{string.Join(",", missing)}] unknown=[{string.Join(",", unknown)}] duplicates=[{string.Join(",", duplicates)}]"));
            }
        }

        var requiredCrossLinks = new List<string>
        {
            "../../CHANGELOG.md",
            $"../migration/{releaseVersion}-integration-packages.md",
        };
        var baselineVersions = graph.Packages
            .Select(package => package.BaselineVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (baselineVersions.Length == 1)
        {
            requiredCrossLinks.Add($"../reference/compatibility/{baselineVersions[0]}-to-{releaseVersion}.md");
        }

        foreach (var target in requiredCrossLinks)
        {
            if (!releaseNotes.Contains($"({target})", StringComparison.Ordinal))
            {
                violations.Add(new(
                    "SPDOC017",
                    releaseRelativePath,
                    $"release notes must cross-link release detail/migration/compatibility target: {target}"));
            }
        }
    }

    private static string? ExtractLevelTwoSection(string content, string heading)
    {
        var headingRegex = new Regex(
            $"^{Regex.Escape(heading)}\\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Multiline);
        var match = headingRegex.Match(content);
        return match.Success ? ExtractSectionAfterHeading(content, match, LevelTwoHeadingRegex) : null;
    }

    private static string ExtractSectionAfterHeading(string content, Match heading, Regex nextHeadingRegex)
    {
        var start = heading.Index + heading.Length;
        var remainder = content[start..];
        var nextHeading = nextHeadingRegex.Match(remainder);
        return nextHeading.Success ? remainder[..nextHeading.Index] : remainder;
    }

    private static async Task ValidateSelfRepositoryLinksAsync(
        string root,
        List<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        foreach (var fullPath in EnumerateLinkCheckedMarkdownFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var sourcePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');

            foreach (Match match in SelfRepositoryBlobMainTargetRegex.Matches(content))
            {
                var target = Uri.UnescapeDataString(match.Groups["path"].Value);
                var targetPath = Resolve(root, target);

                if (!IsRepositoryLocalTarget(root, targetPath) || !File.Exists(targetPath))
                {
                    violations.Add(new(
                        "SPDOC012",
                        sourcePath,
                        $"self-repository blob/main link must resolve in the current checkout: {target}"));
                }
            }

            foreach (Match match in RelativeMarkdownTargetRegex.Matches(content))
            {
                var target = NormalizeRelativeMarkdownTarget(match.Groups[1].Value);
                if (target.Length == 0)
                {
                    continue;
                }

                var sourceDirectory = Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException($"Documentation path has no directory: {sourcePath}");
                var targetPath = Path.GetFullPath(
                    target.Replace('/', Path.DirectorySeparatorChar),
                    sourceDirectory);

                if (!IsRepositoryLocalTarget(root, targetPath)
                    || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
                {
                    violations.Add(new(
                        "SPDOC013",
                        sourcePath,
                        $"relative documentation link must resolve in the current checkout: {match.Groups[1].Value}"));
                }
            }
        }
    }

    private static string NormalizeRelativeMarkdownTarget(string target)
    {
        var normalized = target.Trim();
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        var queryIndex = normalized.IndexOf('?');
        if (queryIndex >= 0)
        {
            normalized = normalized[..queryIndex];
        }

        return Uri.UnescapeDataString(normalized.Trim());
    }

    private static bool IsRepositoryLocalTarget(string root, string targetPath)
    {
        var relativeToRoot = Path.GetRelativePath(root, targetPath);
        return !relativeToRoot.Equals("..", StringComparison.Ordinal)
            && !relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativeToRoot);
    }

    private static IEnumerable<string> EnumerateLinkCheckedMarkdownFiles(string root)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in LinkCheckedRootDocuments)
        {
            var fullPath = Resolve(root, relativePath);
            if (File.Exists(fullPath))
            {
                files.Add(fullPath);
            }
        }

        var docsRoot = Resolve(root, "docs");
        if (Directory.Exists(docsRoot))
        {
            foreach (var fullPath in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories))
            {
                files.Add(fullPath);
            }
        }

        var sourceRoot = Resolve(root, "src");
        if (Directory.Exists(sourceRoot))
        {
            foreach (var fullPath in Directory.EnumerateFiles(sourceRoot, "README.md", SearchOption.AllDirectories))
            {
                files.Add(fullPath);
            }
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
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
