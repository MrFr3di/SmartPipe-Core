using System.Globalization;
using System.Text;
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
        "docs/getting-started.md",
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

        ValidateRequiredRepositoryDocuments(root, violations);
        ValidateLegacyDocumentationDirectories(root, violations);
        await ValidateSelfRepositoryLinksAsync(root, violations, cancellationToken).ConfigureAwait(false);
        await ValidatePackageReadmesAsync(root, graph, violations, cancellationToken).ConfigureAwait(false);
        await ValidateRootReadmeAsync(root, violations, cancellationToken).ConfigureAwait(false);
        await ValidateDocumentationIndexAsync(root, violations, cancellationToken).ConfigureAwait(false);
        await ValidateCompatibilityIndexAsync(root, graph, violations, cancellationToken).ConfigureAwait(false);
        await ValidateGettingStartedAsync(root, graph, violations, cancellationToken).ConfigureAwait(false);
        await ValidateSecurityPolicyAsync(root, violations, cancellationToken).ConfigureAwait(false);
        await ValidatePackageReferenceAsync(root, graph, violations, cancellationToken).ConfigureAwait(false);
        await ValidateReleaseDocumentationAsync(root, graph, violations, cancellationToken).ConfigureAwait(false);

        return new(violations);
    }

    private static void ValidateRequiredRepositoryDocuments(
        string root,
        ICollection<DocumentationViolation> violations)
    {
        foreach (var relativePath in RequiredRepositoryDocuments)
        {
            if (!File.Exists(Resolve(root, relativePath)))
            {
                violations.Add(new(
                    "SPDOC001",
                    relativePath,
                    "required repository documentation is missing"));
            }
        }
    }

    private static void ValidateLegacyDocumentationDirectories(
        string root,
        ICollection<DocumentationViolation> violations)
    {
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
    }

    private static async Task ValidatePackageReadmesAsync(
        string root,
        PackageGraphDocument graph,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        foreach (var package in graph.Packages.Where(package => package.Lifecycle != PackageLifecycle.Planned))
        {
            var readmePath = PackageReadmePath(package);
            var fullReadmePath = Resolve(root, readmePath);
            if (!File.Exists(fullReadmePath))
            {
                violations.Add(new(
                    "SPDOC002",
                    readmePath,
                    $"package README is missing for {package.Id}"));
                continue;
            }

            var content = await File.ReadAllTextAsync(fullReadmePath, cancellationToken).ConfigureAwait(false);
            ValidatePackageReadme(package, readmePath, content, violations);
        }
    }

    private static void ValidatePackageReadme(
        PackageNode package,
        string readmePath,
        string content,
        ICollection<DocumentationViolation> violations)
    {
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
            violations.Add(new(
                "SPDOC004",
                readmePath,
                $"README must contain installation command '{installCommand}'"));
        }

        if (content.Contains("dotnet add package ", StringComparison.Ordinal))
        {
            violations.Add(new(
                "SPDOC005",
                readmePath,
                "use the .NET 10 noun-first 'dotnet package add' form"));
        }

        if (content.Contains($"{installCommand} --version ", StringComparison.Ordinal))
        {
            violations.Add(new(
                "SPDOC010",
                readmePath,
                "package README install commands must stay version-agnostic; release-specific versions belong in release/migration documentation"));
        }

        if (EnumerateRelativeMarkdownTargets(content).Any())
        {
            violations.Add(new(
                "SPDOC011",
                readmePath,
                "package README links/images must use absolute URLs or in-document anchors so NuGet.org rendering is independent of repository-relative paths"));
        }
    }

    private static async Task ValidateRootReadmeAsync(
        string root,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        const string relativePath = "README.md";
        var path = Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var target in new[] { "docs/index.md", "SUPPORT.md", "VERSIONING.md", "SECURITY.md" })
        {
            if (!content.Contains(target, StringComparison.Ordinal))
            {
                violations.Add(new(
                    "SPDOC006",
                    relativePath,
                    $"root README must link to {target}"));
            }
        }
    }

    private static async Task ValidateDocumentationIndexAsync(
        string root,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        const string relativePath = "docs/index.md";
        var path = Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var target in RequiredDocumentationIndexLinks)
        {
            if (!content.Contains(target, StringComparison.Ordinal))
            {
                violations.Add(new(
                    "SPDOC009",
                    relativePath,
                    $"documentation index must link to {target}"));
            }
        }
    }

    private static async Task ValidateCompatibilityIndexAsync(
        string root,
        PackageGraphDocument graph,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        const string relativePath = "docs/reference/compatibility/README.md";
        var path = Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var linkedTargets = EnumerateMarkdownTargets(StripNonProseMarkdown(content))
            .Select(NormalizeRelativeMarkdownTarget)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var baseline in graph.Packages
                     .Where(package => package.Lifecycle != PackageLifecycle.Planned)
                     .Select(package => package.BaselineVersion)
                     .Where(version => !string.IsNullOrWhiteSpace(version))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var target = $"{baseline}-to-{graph.ReleaseVersion}.md";
            if (!linkedTargets.Contains(target))
            {
                violations.Add(new(
                    "SPDOC019",
                    relativePath,
                    $"compatibility index must link the current transition {baseline} → {graph.ReleaseVersion}: {target}"));
            }
        }
    }

    private static string StripNonProseMarkdown(string content)
    {
        // Link references inside HTML comments and fenced code are not navigation links.
        var uncommented = new StringBuilder(content.Length);
        var cursor = 0;
        while (cursor < content.Length)
        {
            var start = content.IndexOf("<!--", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                uncommented.Append(content, cursor, content.Length - cursor);
                break;
            }

            uncommented.Append(content, cursor, start - cursor);
            var end = content.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
                break;

            cursor = end + 3;
        }

        var visible = new StringBuilder(uncommented.Length);
        char? fenceCharacter = null;
        foreach (var line in SplitLines(uncommented.ToString()))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fenceCharacter is null)
                    fenceCharacter = trimmed[0];
                else if (fenceCharacter == trimmed[0])
                    fenceCharacter = null;
                continue;
            }

            if (fenceCharacter is null)
                visible.Append(line).Append('\n');
        }

        return visible.ToString();
    }

    private static async Task ValidateGettingStartedAsync(
        string root,
        PackageGraphDocument graph,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        const string relativePath = "docs/getting-started.md";
        var path = Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var packageSelection = ExtractLevelTwoSection(content, "## Choose the integration package");
        if (packageSelection is null)
        {
            violations.Add(new(
                "SPDOC018",
                relativePath,
                "getting-started must contain the '## Choose the integration package' release-package selection section"));
            return;
        }

        foreach (var package in graph.Packages.Where(package =>
                     package.Lifecycle != PackageLifecycle.Planned
                     && !packageSelection.Contains($"`{package.Id}`", StringComparison.Ordinal)))
        {
            violations.Add(new(
                "SPDOC018",
                relativePath,
                $"getting-started package selection is stale; release package is not named: {package.Id}"));
        }
    }

    private static async Task ValidateSecurityPolicyAsync(
        string root,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        const string relativePath = "SECURITY.md";
        var path = Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!content.Contains("SUPPORT.md", StringComparison.Ordinal))
        {
            violations.Add(new(
                "SPDOC006",
                relativePath,
                "security policy must delegate support status to SUPPORT.md"));
        }
    }

    private static async Task ValidatePackageReferenceAsync(
        string root,
        PackageGraphDocument graph,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        const string relativePath = "docs/reference/packages.md";
        var path = Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in graph.Packages
                     .Where(package => package.Lifecycle != PackageLifecycle.Planned)
                     .Select(package => (Package: package, ExpectedRow: BuildPackageReferenceRow(package)))
                     .Where(candidate => !content.Contains(candidate.ExpectedRow, StringComparison.Ordinal)))
        {
            violations.Add(new(
                "SPDOC007",
                relativePath,
                $"package graph projection is stale for {candidate.Package.Id}; expected row: {candidate.ExpectedRow}"));
        }
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
        var currentReleaseSection = await ValidateCurrentReleaseChangelogAsync(
            root,
            releaseVersion,
            changelogRelativePath,
            violations,
            cancellationToken).ConfigureAwait(false);

        if (currentReleaseSection is not null)
        {
            ValidateFirstReleasePackages(
                graph,
                currentReleaseSection,
                changelogRelativePath,
                violations);
        }

        var releasePath = Resolve(root, releaseRelativePath);
        if (!File.Exists(releasePath))
        {
            violations.Add(new(
                "SPDOC014",
                releaseRelativePath,
                $"current release notes are missing for {releaseVersion}"));
            return;
        }

        var releaseNotes = await File.ReadAllTextAsync(releasePath, cancellationToken).ConfigureAwait(false);
        ValidateReleasePackageSelection(graph, releaseNotes, releaseRelativePath, violations);
        ValidateReleaseCrossLinks(graph, releaseNotes, releaseRelativePath, violations);
    }

    private static async Task<string?> ValidateCurrentReleaseChangelogAsync(
        string root,
        string releaseVersion,
        string changelogRelativePath,
        ICollection<DocumentationViolation> violations,
        CancellationToken cancellationToken)
    {
        var changelogPath = Resolve(root, changelogRelativePath);
        if (!File.Exists(changelogPath))
        {
            violations.Add(new(
                "SPDOC014",
                changelogRelativePath,
                $"current release changelog is missing for {releaseVersion}"));
            return null;
        }

        var changelog = await File.ReadAllTextAsync(changelogPath, cancellationToken).ConfigureAwait(false);
        var changelogLines = SplitLines(changelog);
        var versionHeadingPrefix = $"## [{releaseVersion}]";
        var developmentHeading = $"{versionHeadingPrefix} — Development";
        var datedHeadingPrefix = $"{versionHeadingPrefix} - ";
        var currentHeadingCandidates = changelogLines
            .Select((line, index) => (Line: line.TrimEnd(), Index: index))
            .Where(item => item.Line.StartsWith(versionHeadingPrefix, StringComparison.Ordinal))
            .ToArray();

        if (currentHeadingCandidates.Length != 1
            || !IsValidReleaseHeading(
                currentHeadingCandidates[0].Line,
                developmentHeading,
                datedHeadingPrefix))
        {
            violations.Add(new(
                "SPDOC014",
                changelogRelativePath,
                $"current release changelog must contain exactly one heading '## [{releaseVersion}] — Development' or '## [{releaseVersion}] - yyyy-MM-dd' with a valid ISO release date"));
            return null;
        }

        return ExtractSectionAfterLine(
            changelogLines,
            currentHeadingCandidates[0].Index,
            IsVersionHeadingLine);
    }

    private static bool IsValidReleaseHeading(
        string heading,
        string developmentHeading,
        string datedHeadingPrefix)
    {
        if (string.Equals(heading, developmentHeading, StringComparison.Ordinal))
        {
            return true;
        }

        return heading.StartsWith(datedHeadingPrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(
                heading[datedHeadingPrefix.Length..],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
    }

    private static void ValidateFirstReleasePackages(
        PackageGraphDocument graph,
        string currentReleaseSection,
        string changelogRelativePath,
        ICollection<DocumentationViolation> violations)
    {
        foreach (var package in graph.Packages.Where(package =>
                     package.Lifecycle != PackageLifecycle.Planned
                     && package.BaselineVersion is null
                     && !currentReleaseSection.Contains($"`{package.Id}`", StringComparison.Ordinal)))
        {
            violations.Add(new(
                "SPDOC016",
                changelogRelativePath,
                $"first-release package must be explicitly named in the current release section: {package.Id}"));
        }
    }

    private static void ValidateReleasePackageSelection(
        PackageGraphDocument graph,
        string releaseNotes,
        string releaseRelativePath,
        ICollection<DocumentationViolation> violations)
    {
        var packageSection = ExtractLevelTwoSection(releaseNotes, "## Package selection");
        if (packageSection is null)
        {
            violations.Add(new(
                "SPDOC015",
                releaseRelativePath,
                "release notes must contain a '## Package selection' section projected from the package graph"));
            return;
        }

        var expected = graph.Packages
            .Where(package => package.Lifecycle != PackageLifecycle.Planned)
            .Select(package => package.Id)
            .ToHashSet(StringComparer.Ordinal);
        var actual = EnumerateReleasePackageIds(packageSection).ToArray();
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expected
            .Except(actualSet, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var unknown = actualSet
            .Except(expected, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
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

    private static void ValidateReleaseCrossLinks(
        PackageGraphDocument graph,
        string releaseNotes,
        string releaseRelativePath,
        ICollection<DocumentationViolation> violations)
    {
        var releaseVersion = graph.ReleaseVersion;
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
            requiredCrossLinks.Add(
                $"../reference/compatibility/{baselineVersions[0]}-to-{releaseVersion}.md");
        }

        foreach (var target in requiredCrossLinks.Where(target =>
                     !releaseNotes.Contains($"({target})", StringComparison.Ordinal)))
        {
            violations.Add(new(
                "SPDOC017",
                releaseRelativePath,
                $"release notes must cross-link release detail/migration/compatibility target: {target}"));
        }
    }

    private static string? ExtractLevelTwoSection(string content, string heading)
    {
        var lines = SplitLines(content);
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].TrimEnd(), heading, StringComparison.Ordinal))
            {
                return ExtractSectionAfterLine(lines, index, IsLevelTwoHeadingLine);
            }
        }

        return null;
    }

    private static string ExtractSectionAfterLine(
        IReadOnlyList<string> lines,
        int headingIndex,
        Func<string, bool> isNextHeading)
    {
        var end = headingIndex + 1;
        while (end < lines.Count && !isNextHeading(lines[end]))
        {
            end++;
        }

        return string.Join('\n', lines.Skip(headingIndex + 1).Take(end - headingIndex - 1));
    }

    private static bool IsVersionHeadingLine(string line)
    {
        var candidate = line.TrimEnd();
        return candidate.StartsWith("## [", StringComparison.Ordinal)
            && (candidate.Contains("] — ", StringComparison.Ordinal)
                || candidate.Contains("] - ", StringComparison.Ordinal));
    }

    private static bool IsLevelTwoHeadingLine(string line) =>
        line.TrimEnd().StartsWith("## ", StringComparison.Ordinal);

    private static IEnumerable<string> EnumerateReleasePackageIds(string section)
    {
        foreach (var line in SplitLines(section))
        {
            if (line.Length == 0 || line[0] != '|')
            {
                continue;
            }

            var separator = line.IndexOf('|', 1);
            if (separator < 0)
            {
                continue;
            }

            var firstCell = line[1..separator].Trim();
            if (firstCell.Length >= 3 && firstCell[0] == '`' && firstCell[^1] == '`')
            {
                var id = firstCell[1..^1];
                if (id.Length != 0)
                {
                    yield return id;
                }
            }
        }
    }

    private static string[] SplitLines(string content) =>
        content.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();

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
            ValidateSelfRepositoryBlobMainTargets(root, sourcePath, content, violations);
            ValidateRelativeMarkdownTargets(root, fullPath, sourcePath, content, violations);
        }
    }

    private static void ValidateSelfRepositoryBlobMainTargets(
        string root,
        string sourcePath,
        string content,
        ICollection<DocumentationViolation> violations)
    {
        foreach (var rawTarget in EnumerateSelfRepositoryBlobMainTargets(content))
        {
            var target = Uri.UnescapeDataString(rawTarget);
            var targetPath = Resolve(root, target);
            if (IsRepositoryLocalTarget(root, targetPath) && File.Exists(targetPath))
            {
                continue;
            }

            violations.Add(new(
                "SPDOC012",
                sourcePath,
                $"self-repository blob/main link must resolve in the current checkout: {target}"));
        }
    }

    private static void ValidateRelativeMarkdownTargets(
        string root,
        string fullPath,
        string sourcePath,
        string content,
        ICollection<DocumentationViolation> violations)
    {
        var sourceDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Documentation path has no directory: {sourcePath}");

        foreach (var rawTarget in EnumerateRelativeMarkdownTargets(content))
        {
            var target = NormalizeRelativeMarkdownTarget(rawTarget);
            if (target.Length == 0)
            {
                continue;
            }

            var targetPath = Path.GetFullPath(
                target.Replace('/', Path.DirectorySeparatorChar),
                sourceDirectory);
            if (IsRepositoryLocalTarget(root, targetPath)
                && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                continue;
            }

            violations.Add(new(
                "SPDOC013",
                sourcePath,
                $"relative documentation link must resolve in the current checkout: {rawTarget}"));
        }
    }

    private const string SelfRepositoryBlobMainPrefix =
        "https://github.com/MrFr3di/SmartPipe-Core/blob/main/";

    private static IEnumerable<string> EnumerateRelativeMarkdownTargets(string content)
    {
        foreach (var target in EnumerateMarkdownTargets(content))
        {
            var candidate = target.Trim();
            if (candidate.Length == 0
                || candidate[0] == '#'
                || candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return target;
        }
    }

    private static IEnumerable<string> EnumerateMarkdownTargets(string content)
    {
        var index = 0;
        while (index < content.Length)
        {
            var openBracket = content.IndexOf('[', index);
            if (openBracket < 0)
            {
                yield break;
            }

            var closeBracket = content.IndexOf(']', openBracket + 1);
            if (closeBracket < 0)
            {
                yield break;
            }

            var openParenthesis = closeBracket + 1;
            if (closeBracket == openBracket + 1
                || openParenthesis >= content.Length
                || content[openParenthesis] != '(')
            {
                index = closeBracket + 1;
                continue;
            }

            var targetStart = openParenthesis + 1;
            var closeParenthesis = content.IndexOf(')', targetStart);
            if (closeParenthesis < 0)
            {
                yield break;
            }

            if (closeParenthesis > targetStart)
            {
                yield return content[targetStart..closeParenthesis];
            }

            index = closeParenthesis + 1;
        }
    }

    private static IEnumerable<string> EnumerateSelfRepositoryBlobMainTargets(string content)
    {
        var index = 0;
        while (index < content.Length)
        {
            var prefix = content.IndexOf(
                SelfRepositoryBlobMainPrefix,
                index,
                StringComparison.OrdinalIgnoreCase);
            if (prefix < 0)
            {
                yield break;
            }

            var targetStart = prefix + SelfRepositoryBlobMainPrefix.Length;
            var targetEnd = targetStart;
            while (targetEnd < content.Length && !IsSelfRepositoryTargetDelimiter(content[targetEnd]))
            {
                targetEnd++;
            }

            if (targetEnd > targetStart)
            {
                yield return content[targetStart..targetEnd];
            }

            index = targetEnd > prefix ? targetEnd : prefix + SelfRepositoryBlobMainPrefix.Length;
        }
    }

    private static bool IsSelfRepositoryTargetDelimiter(char value) =>
        value == ')' || value == '#' || value == '?' || char.IsWhiteSpace(value);

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
