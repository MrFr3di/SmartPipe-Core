using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Release;

internal sealed class ReleaseVersionException(string code, string message) : Exception(message) { public string Code { get; } = code; }
internal sealed record ReleaseVersionViolation(string Code, string PackageId, string Rule, string? Path = null);
internal sealed record ReleaseVersionResult(string PackageVersion, IReadOnlyList<ReleaseVersionViolation> Violations) { public bool Success => Violations.Count == 0; }

internal sealed class ReleaseVersionValidator
{
    private readonly IEvaluatedProjectReader _projects;
    private readonly IPackedNuspecReader _packages;
    internal ReleaseVersionValidator(IEvaluatedProjectReader? projects = null, IPackedNuspecReader? packages = null)
    { _projects = projects ?? new EvaluatedProjectReader(); _packages = packages ?? new PackedNuspecReader(); }

    internal static string ParseTag(string tag)
    {
        if (tag.Length < 2 || tag[0] != 'v' || tag.Trim() != tag || tag.Contains('+')) throw new ReleaseVersionException("SPVER001", "Tag must be canonical v-prefixed SemVer without build metadata.");
        var value = tag[1..]; var dash = value.IndexOf('-'); var core = dash < 0 ? value : value[..dash]; var prerelease = dash < 0 ? null : value[(dash + 1)..];
        var parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(x => !CanonicalNumber(x))) throw new ReleaseVersionException("SPVER001", "Tag core must contain three canonical numeric components.");
        if (prerelease is not null && (prerelease.Length == 0 || prerelease.Split('.').Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || x.All(char.IsAsciiDigit) && !CanonicalNumber(x))))
            throw new ReleaseVersionException("SPVER001", "Tag prerelease identifiers are not canonical SemVer.");
        return value;
        static bool CanonicalNumber(string x) => x.Length > 0 && x.All(char.IsAsciiDigit) && (x.Length == 1 || x[0] != '0');
    }

    public async Task<ReleaseVersionResult> ValidateAsync(PackageGraphDocument graph, string tag, PackageGraphMode mode, string root, string packageDirectory, CancellationToken ct)
    {
        var packageVersion = ParseTag(tag); var baseVersion = packageVersion.Split('-')[0];
        var errors = new List<ReleaseVersionViolation>();
        void Add(string code, string id, string rule, string? path = null) => errors.Add(new(code, id, rule, path));
        if (baseVersion != graph.ReleaseVersion) Add("SPVER002", "graph", $"tag base {baseVersion} must equal graph releaseVersion {graph.ReleaseVersion}");
        var expected = graph.Packages.Where(x => mode == PackageGraphMode.Release || x.Lifecycle != PackageLifecycle.Planned).ToArray();
        if (mode == PackageGraphMode.Release)
            foreach (var planned in graph.Packages.Where(x => x.Lifecycle == PackageLifecycle.Planned)) Add("SPVER003", planned.Id, "planned package must be activated before release");
        foreach (var node in expected)
        {
            var projectPath = Path.Combine(root, node.ProjectPath);
            if (!File.Exists(projectPath)) { Add("SPVER004", node.Id, "graph project is missing", node.ProjectPath); continue; }
            var project = await _projects.ReadAsync(projectPath, ct).ConfigureAwait(false);
            if (project.Version != graph.ReleaseVersion || project.PackageVersion != packageVersion) Add("SPVER005", node.Id, $"evaluated Version must equal {graph.ReleaseVersion} and PackageVersion must equal tag {packageVersion}", node.ProjectPath);
        }
        var artifacts = new List<(string Path, PackedPackageModel Package)>();
        foreach (var path in Directory.EnumerateFiles(packageDirectory, "*.nupkg", SearchOption.TopDirectoryOnly))
            artifacts.Add((path, await _packages.ReadAsync(path, ct).ConfigureAwait(false)));
        foreach (var duplicate in artifacts.GroupBy(x => x.Package.Id, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            Add("SPVER006", duplicate.Key, "duplicate package artifacts", string.Join(";", duplicate.Select(x => Path.GetFileName(x.Path))));
        var expectedIds = expected.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts.Where(x => !graph.Packages.Any(n => n.Id.Equals(x.Package.Id, StringComparison.OrdinalIgnoreCase))))
            Add("SPVER011", artifact.Package.Id, "package artifact is not registered in graph", artifact.Path);
        foreach (var node in expected)
        {
            var matches = artifacts.Where(x => x.Package.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) { Add("SPVER007", node.Id, "active package artifact is missing"); continue; }
            foreach (var artifact in matches)
            {
                if (artifact.Package.Id != node.Id) Add("SPVER008", node.Id, "package ID casing must exactly match graph", artifact.Path);
                if (artifact.Package.Version != packageVersion) Add("SPVER009", node.Id, $"packed version must equal tag version {packageVersion}", artifact.Path);
            }
        }
        if (mode == PackageGraphMode.Current)
            foreach (var artifact in artifacts.Where(x => graph.Packages.Any(n => n.Lifecycle == PackageLifecycle.Planned && n.Id.Equals(x.Package.Id, StringComparison.OrdinalIgnoreCase))))
                Add("SPVER010", artifact.Package.Id, "planned package artifact is unexpected in current mode", artifact.Path);
        return new(packageVersion, errors.OrderBy(x => x.PackageId, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray());
    }
}
