using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Consumers;

internal sealed record RunConsumersOptions(
    string RepositoryRoot,
    string Set,
    string PackageDirectory,
    string PackageVersion,
    string ManifestPath,
    string? Category = null);

internal sealed class ConsumerScenarioRunner(DotNetProcessRunner? processRunner = null)
{
    private static readonly TimeSpan ExpectedDiagnosticRegexTimeout = TimeSpan.FromSeconds(1);
    private readonly DotNetProcessRunner _processRunner = processRunner ?? new();

    public async Task<IReadOnlyList<ConsumerScenarioResult>> RunAsync(RunConsumersOptions options, CancellationToken ct)
    {
        var graph = await new PackageGraphLoader().LoadAsync(options.RepositoryRoot, "eng/package-graph.json", ct).ConfigureAwait(false);
        var document = await new ConsumerScenarioLoader().LoadAsync(options.RepositoryRoot, options.ManifestPath, graph, ct).ConfigureAwait(false);
        var scenarios = document.Scenarios
            .Where(scenario => scenario.Set == options.Set
                && (options.Category is null || scenario.Category == options.Category))
            .ToArray();
        if (scenarios.Length == 0) throw new ConsumerScenarioException("SPCONS010", $"Consumer set '{options.Set}' is empty.");
        var centralPackages = await new CentralPackageVersionReader().VerifyAsync(
            options.RepositoryRoot,
            CentralPackageValidationMode.Current,
            ct).ConfigureAwait(false);
        if (!centralPackages.Success)
        {
            throw new ConsumerScenarioException("SPCONS019", "Repository central package versions are invalid.");
        }

        var externalPackageVersions = centralPackages.Versions
            .Where(static pair => !pair.Key.StartsWith("SmartPipe.", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var externalPackageIds = await ReadExternalPackageIdsAsync(options.RepositoryRoot, ct).ConfigureAwait(false);
        var results = new List<ConsumerScenarioResult>();
        foreach (var scenario in scenarios) results.Add(await RunScenarioAsync(options, scenario, graph, externalPackageVersions, externalPackageIds, ct).ConfigureAwait(false));
        return results;
    }

    private async Task<ConsumerScenarioResult> RunScenarioAsync(
        RunConsumersOptions options,
        ConsumerScenario scenario,
        PackageGraphDocument graph,
        IReadOnlyDictionary<string, string> externalPackageVersions,
        IReadOnlyList<string> externalPackageIds,
        CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var relativeWorkspace = $"artifacts/consumers/{scenario.Id}/{runId}";
        var workspace = Path.GetFullPath(relativeWorkspace, options.RepositoryRoot);
        EnsureContained(Path.GetFullPath(options.RepositoryRoot), workspace);
        var source = Path.Combine(workspace, "source");
        var logs = Path.Combine(workspace, "logs");
        Directory.CreateDirectory(source); Directory.CreateDirectory(logs);
        CopyTemplateDirectory(options.RepositoryRoot, scenario.TemplatePath, source);
        var project = Directory.EnumerateFiles(source, "*.csproj", SearchOption.TopDirectoryOnly).Single();
        var events = new List<ConsumerCommandEvent>();
        var feed = options.PackageDirectory;
        var version = options.PackageVersion;
        if (scenario.Mode == ConsumerMode.BinaryCompatibility)
        {
            feed = await ProvisionVerifiedBaselineFeedAsync(options.RepositoryRoot, workspace, scenario.BaselineVersion!, logs, scenario.Timeout, events, ct).ConfigureAwait(false);
            version = scenario.BaselineVersion!;
        }
        _ = await new ConsumerCentralPackagesWriter().WriteAsync(
            workspace,
            scenario.PackageIds,
            version,
            externalPackageVersions,
            ct).ConfigureAwait(false);
        var config = await new LocalNuGetConfigWriter().WriteAsync(
            workspace,
            feed,
            ct,
            workspace,
            externalPackageIds).ConfigureAwait(false);
        var packages = Path.Combine(workspace, "packages");
        var rid = RuntimeIdentifier();
        var restore = new List<string> { "restore", project, "--configfile", config, "--packages", packages, "--use-lock-file" };
        if (scenario.Mode is ConsumerMode.PublishTrimmed or ConsumerMode.PublishNativeAot) { restore.Add("-r"); restore.Add(rid); }
        if (scenario.Mode == ConsumerMode.PublishNativeAot) restore.Add("-p:PublishAot=true");
        if (scenario.ExpectedPublishDiagnostic is { } restoreExpectation)
            restore.AddRange(restoreExpectation.MsBuildProperties.Select(static property => "-p:" + property));
        await RunRequiredAsync("dotnet", restore, source, logs, options.RepositoryRoot, scenario.Timeout, events, ct).ConfigureAwait(false);
        if (scenario.RunSecondLockedRestore)
        {
            var locked = restore.ToList(); locked.Remove("--use-lock-file"); locked.Add("--locked-mode");
            await RunRequiredAsync("dotnet", locked, source, logs, options.RepositoryRoot, scenario.Timeout, events, ct).ConfigureAwait(false);
        }

        string outputDirectory;
        IReadOnlyList<string>? publishArguments = null;
        if (scenario.Mode is ConsumerMode.PublishTrimmed or ConsumerMode.PublishNativeAot)
        {
            outputDirectory = Path.Combine(workspace, "publish");
            var publish = new List<string> { "publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "--no-restore", "-o", outputDirectory, "-p:PublishTrimmed=true", "-p:TrimMode=link" };
            if (scenario.Mode == ConsumerMode.PublishNativeAot) publish.Add("-p:PublishAot=true");
            publishArguments = publish;
            await RunRequiredAsync("dotnet", publish, source, logs, options.RepositoryRoot, scenario.Timeout, events, ct).ConfigureAwait(false);
        }
        else
        {
            await RunRequiredAsync("dotnet", ["build", project, "-c", "Release", "--no-restore"], source, logs, options.RepositoryRoot, scenario.Timeout, events, ct).ConfigureAwait(false);
            outputDirectory = Path.Combine(source, "bin", "Release", "net10.0");
        }

        var assets = Path.Combine(source, "obj", "project.assets.json");
        if (!File.Exists(assets)) throw new ConsumerScenarioException("SPCONS011", $"Scenario '{scenario.Id}' did not produce project.assets.json.");
        var observed = await InspectDependenciesAsync(assets, scenario, ct).ConfigureAwait(false);
        IReadOnlyList<string> replacementPackageIds = scenario.PackageIds;
        if (scenario.Mode == ConsumerMode.BinaryCompatibility)
        {
            replacementPackageIds = CurrentSmartPipeClosure(graph, scenario.PackageIds);
            await RefreshBinaryCompatibilityDeploymentMetadataAsync(
                workspace,
                project,
                outputDirectory,
                replacementPackageIds,
                options.PackageDirectory,
                options.PackageVersion,
                externalPackageVersions,
                externalPackageIds,
                options.RepositoryRoot,
                scenario.Timeout,
                events,
                ct).ConfigureAwait(false);
            foreach (var replacement in await ReplaceRuntimeAssembliesWithoutBuildAsync(outputDirectory, options.PackageDirectory, options.PackageVersion, replacementPackageIds, ct).ConfigureAwait(false)) events.Add(replacement);
        }
        await InspectRuntimeArtifactsAsync(outputDirectory, scenario.Mode, ct).ConfigureAwait(false);
        await ExecuteAsync(outputDirectory, project, scenario, logs, options.RepositoryRoot, events, ct).ConfigureAwait(false);
        if (scenario.ExpectedPublishDiagnostic is { } expectation)
        {
            var diagnosticRestore = restore.ToList();
            diagnosticRestore.Remove("--use-lock-file");
            diagnosticRestore.Add("--locked-mode");
            var diagnosticPublish = publishArguments!.ToList();
            diagnosticPublish[diagnosticPublish.IndexOf("-o") + 1] = Path.Combine(workspace, "expected-diagnostic-publish");
            await RunExpectedPublishDiagnosticAsync(
                diagnosticRestore,
                diagnosticPublish,
                expectation,
                source,
                logs,
                options.RepositoryRoot,
                Path.GetFullPath(expectation.SourcePath, Path.GetDirectoryName(project)!),
                scenario.Timeout,
                events,
                ct).ConfigureAwait(false);
        }
        if (scenario.Mode == ConsumerMode.BinaryCompatibility) ValidateBinaryCompatibilityPhases(events, replacementPackageIds.Count);

        var result = new ConsumerScenarioResult(1, scenario.Id, "passed", options.PackageVersion, scenario.RunSecondLockedRestore,
            started.ElapsedMilliseconds, observed, events);
        var report = JsonSerializer.Serialize(result, RepositoryChecksJsonContext.Default.ConsumerScenarioResult).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        await File.WriteAllTextAsync(Path.Combine(workspace, "result.json"), report, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return result;
    }

    private async Task<string> ProvisionVerifiedBaselineFeedAsync(string root, string workspace, string baselineVersion, string logs, TimeSpan timeout, List<ConsumerCommandEvent> events, CancellationToken ct)
    {
        var manifest = BaselineManifestSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(root, "eng", "baselines", baselineVersion, "manifest.json"), ct).ConfigureAwait(false));
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        var globalPackages = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
            : Path.GetFullPath(configured);
        if (!Path.IsPathFullyQualified(globalPackages) || !Directory.Exists(globalPackages)) throw new ConsumerScenarioException("SPCONS012", "NuGet global packages folder is unavailable.");
        var feed = Path.Combine(workspace, "baseline-feed"); Directory.CreateDirectory(feed);
        foreach (var package in manifest.Packages.Where(x => x.Version == baselineVersion))
        {
            var candidates = new[]
            {
                Path.Combine(root, "artifacts", "baselines", baselineVersion, package.FileName),
                Path.Combine(globalPackages, package.Id.ToLowerInvariant(), baselineVersion, package.FileName.ToLowerInvariant()),
            };
            string? source = null;
            foreach (var candidate in candidates.Where(File.Exists))
            {
                var hash = await Hashing.Sha256FileAsync(candidate, ct).ConfigureAwait(false);
                if (hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) { source = candidate; break; }
            }
            if (source is null) throw new ConsumerScenarioException("SPCONS013", $"No available baseline package matches the verified hash: {package.FileName}.");
            File.Copy(source, Path.Combine(feed, package.FileName));
        }
        return feed;
    }

    private async Task<DotNetProcessResult> RunRequiredAsync(string fileName, IReadOnlyList<string> args, string cwd, string logs, string repositoryRoot, TimeSpan timeout, List<ConsumerCommandEvent> events, CancellationToken ct)
    {
        var result = await _processRunner.RunAsync(new(fileName, args, cwd, logs, timeout), ct).ConfigureAwait(false);
        events.Add(new("process", result.Command, result.ExitCode, result.StartedUtc, result.DurationMs, Normalize(result.StandardOutputLog, cwd), Normalize(result.StandardErrorLog, cwd)));
        if (result.ExitCode != 0) throw BuildProcessFailure(result, repositoryRoot);
        return result;
    }

    internal static IReadOnlyList<string> BuildExpectedDiagnosticPublishArguments(
        IReadOnlyList<string> publishArguments,
        ExpectedPublishDiagnostic expectation)
    {
        var arguments = publishArguments.ToList();
        arguments.Add("-warnaserror");
        arguments.AddRange(expectation.MsBuildProperties.Select(static property => "-p:" + property));
        return arguments;
    }

    internal async Task RunExpectedPublishDiagnosticAsync(
        IReadOnlyList<string> restoreArguments,
        IReadOnlyList<string> publishArguments,
        ExpectedPublishDiagnostic expectation,
        string cwd,
        string logs,
        string repositoryRoot,
        string expectedSource,
        TimeSpan timeout,
        List<ConsumerCommandEvent> events,
        CancellationToken ct)
    {
        var restore = restoreArguments.ToList();
        foreach (var property in expectation.MsBuildProperties.Select(static property => "-p:" + property))
            if (!restore.Contains(property, StringComparer.Ordinal)) restore.Add(property);
        await RunRequiredAsync("dotnet", restore, cwd, logs, repositoryRoot, timeout, events, ct).ConfigureAwait(false);
        var arguments = BuildExpectedDiagnosticPublishArguments(publishArguments, expectation);
        var result = await _processRunner.RunAsync(new("dotnet", arguments, cwd, logs, timeout), ct).ConfigureAwait(false);
        events.Add(new(
            "expected-publish-diagnostic",
            result.Command,
            result.ExitCode,
            result.StartedUtc,
            result.DurationMs,
            Normalize(result.StandardOutputLog, cwd),
            Normalize(result.StandardErrorLog, cwd)));
        await ValidateExpectedPublishDiagnosticAsync(result, expectation, expectedSource, repositoryRoot, ct).ConfigureAwait(false);
    }

    internal static async Task ValidateExpectedPublishDiagnosticAsync(
        DotNetProcessResult result,
        ExpectedPublishDiagnostic expectation,
        string expectedSource,
        string repositoryRoot,
        CancellationToken ct)
    {
        if (result.ExitCode == 0)
            throw new ConsumerScenarioException("SPCONS024", $"Expected publish diagnostic {expectation.Code} was not emitted.");

        var root = Path.GetFullPath(repositoryRoot);
        var stdoutLog = Path.GetFullPath(result.StandardOutputLog);
        var stderrLog = Path.GetFullPath(result.StandardErrorLog);
        EnsureContained(root, stdoutLog);
        EnsureContained(root, stderrLog);
        if (!File.Exists(stdoutLog) || !File.Exists(stderrLog)
            || (File.GetAttributes(stdoutLog) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(stderrLog) & FileAttributes.ReparsePoint) != 0)
            throw new ConsumerScenarioException("SPCONS009", "Expected diagnostic evidence logs are invalid.");

        var output = await File.ReadAllTextAsync(stdoutLog, ct).ConfigureAwait(false)
            + "\n"
            + await File.ReadAllTextAsync(stderrLog, ct).ConfigureAwait(false);
        var diagnostics = Regex.Matches(
            output,
            @"^(?<path>[^\r\n]+?\.cs)\((?<line>[1-9][0-9]*),(?<column>[1-9][0-9]*)\):[^\r\n]*?\b(?<code>[A-Z]{2}[0-9]{4}):",
            RegexOptions.Multiline | RegexOptions.CultureInvariant,
            ExpectedDiagnosticRegexTimeout);
        var errors = Regex.Matches(
            output,
            @"\berror\s+(?<code>[A-Z][A-Z0-9]*[0-9]{4}):",
            RegexOptions.CultureInvariant,
            ExpectedDiagnosticRegexTimeout);
        var expectedFullPath = Path.GetFullPath(expectedSource);
        var expectedRedactedPath = DiagnosticRedactor.Redact(expectedFullPath).Replace('\\', '/');
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matchingCodeAndLine = diagnostics.Cast<Match>().Count(match =>
            match.Groups["code"].Value == expectation.Code
            && int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) == expectation.Line);
        var matching = diagnostics.Cast<Match>().Count(match =>
        {
            var reported = match.Groups["path"].Value.Trim();
            var sourceMatches = reported.StartsWith("<home>/", StringComparison.Ordinal)
                || reported.StartsWith("<home>\\", StringComparison.Ordinal)
                    ? string.Equals(reported.Replace('\\', '/'), expectedRedactedPath, pathComparison)
                    : string.Equals(
                        Path.IsPathFullyQualified(reported)
                            ? Path.GetFullPath(reported)
                            : Path.GetFullPath(reported, Path.GetDirectoryName(expectedFullPath)!),
                        expectedFullPath,
                        pathComparison);
            return sourceMatches
                && match.Groups["code"].Value == expectation.Code
                && int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) == expectation.Line;
        });
        if (matching == 1
            && diagnostics.Count == 1
            && errors.Count == 1
            && errors[0].Groups["code"].Value == expectation.Code) return;
        if (matching > 1)
            throw new ConsumerScenarioException("SPCONS024", $"Expected publish diagnostic {expectation.Code} was emitted more than once.");
        if (matchingCodeAndLine == 1
            && diagnostics.Count == 1
            && errors.Count == 1
            && errors[0].Groups["code"].Value == expectation.Code)
            throw new ConsumerScenarioException("SPCONS024", $"Expected publish diagnostic {expectation.Code} was emitted from an unexpected source.");
        throw BuildProcessFailure(result, repositoryRoot);
    }

    internal static ConsumerScenarioException BuildProcessFailure(DotNetProcessResult result, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        var root = Path.GetFullPath(repositoryRoot);
        var stderrLog = Path.GetFullPath(result.StandardErrorLog);
        EnsureContained(root, stderrLog);
        var evidence = Path.GetRelativePath(root, stderrLog).Replace('\\', '/');
        if (evidence.Length > 768 || evidence.IndexOfAny(['\r', '\n']) >= 0)
            throw new ConsumerScenarioException("SPCONS009", "Consumer stderr evidence path is invalid.");
        var prefix = $"Consumer command failed ({result.ExitCode}); stderr evidence: {evidence}";
        var diagnostic = DotNetProcessRunner.Redact(
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        const string separator = "; diagnostic: ";
        var available = 1024 - prefix.Length - separator.Length;
        var suffix = available > 0 && diagnostic.Length > 0
            ? separator + diagnostic[^Math.Min(available, diagnostic.Length)..]
            : string.Empty;
        return new ConsumerScenarioException(
            "SPCONS014",
            prefix + suffix);
    }

    private static async Task<IReadOnlyList<string>> InspectDependenciesAsync(string assetsPath, ConsumerScenario scenario, CancellationToken ct)
    {
        await using var stream = File.OpenRead(assetsPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().Select(x => x.Name.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSmartPipe = libraries.Where(x => x.StartsWith("SmartPipe.", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var expectedSmartPipe = scenario.ExpectedSmartPipeDependencies.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!actualSmartPipe.SequenceEqual(expectedSmartPipe, StringComparer.OrdinalIgnoreCase)) throw new ConsumerScenarioException("SPCONS015", $"Scenario '{scenario.Id}' SmartPipe dependency set differs. Expected=[{string.Join(',', expectedSmartPipe)}] Actual=[{string.Join(',', actualSmartPipe)}].");
        foreach (var forbidden in scenario.ForbiddenDependencies) if (libraries.Contains(forbidden)) throw new ConsumerScenarioException("SPCONS016", $"Scenario '{scenario.Id}' contains forbidden dependency '{forbidden}'.");
        return actualSmartPipe;
    }

    private static async Task<IReadOnlyList<string>> ReadExternalPackageIdsAsync(string repositoryRoot, CancellationToken ct)
    {
        var path = Path.Combine(repositoryRoot, "Directory.Packages.props");
        await using var stream = File.OpenRead(path);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        var ids = document.Root?.Elements("ItemGroup").Elements("PackageVersion")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(id => !string.IsNullOrWhiteSpace(id) && !id.StartsWith("SmartPipe.", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        var allIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var lockPath in Directory.EnumerateFiles(repositoryRoot, "packages.lock.json", SearchOption.AllDirectories))
        {
            var normalized = lockPath.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/Fixtures/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var lockStream = File.OpenRead(lockPath);
            using var lockDocument = await JsonDocument.ParseAsync(lockStream, cancellationToken: ct).ConfigureAwait(false);
            if (!lockDocument.RootElement.TryGetProperty("dependencies", out var frameworks)) continue;
            foreach (var framework in frameworks.EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    if (!package.Name.StartsWith("SmartPipe.", StringComparison.OrdinalIgnoreCase))
                        allIds.Add(package.Name);
                }
            }
        }

        var rid = RuntimeIdentifier();
        allIds.Add($"Microsoft.NETCore.App.Runtime.{rid}");
        allIds.Add($"Microsoft.WindowsDesktop.App.Runtime.{rid}");
        allIds.Add($"Microsoft.AspNetCore.App.Runtime.{rid}");
        allIds.Add($"Microsoft.NETCore.App.Crossgen2.{rid}");
        allIds.Add($"Microsoft.NETCore.App.Host.{rid}");
        allIds.Add("Microsoft.DotNet.ILCompiler");
        allIds.Add($"Microsoft.NETCore.App.Runtime.NativeAOT.{rid}");
        allIds.Add($"runtime.{rid}.Microsoft.DotNet.ILCompiler");

        return allIds.Count == 0
            ? throw new ConsumerScenarioException("SPCONS021", "Directory.Packages.props contains no external package IDs for source mapping.")
            : allIds.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task InspectRuntimeArtifactsAsync(string output, ConsumerMode mode, CancellationToken ct)
    {
        if (!Directory.Exists(output)) throw new ConsumerScenarioException("SPCONS011", "Consumer output directory is missing.");
        if (mode != ConsumerMode.PublishNativeAot)
        {
            var deps = Directory.EnumerateFiles(output, "*.deps.json").SingleOrDefault() ?? throw new ConsumerScenarioException("SPCONS011", "Consumer deps.json is missing.");
            var runtime = Directory.EnumerateFiles(output, "*.runtimeconfig.json").SingleOrDefault() ?? throw new ConsumerScenarioException("SPCONS011", "Consumer runtimeconfig.json is missing.");
            _ = await File.ReadAllTextAsync(deps, ct).ConfigureAwait(false);
            _ = await File.ReadAllTextAsync(runtime, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(string output, string project, ConsumerScenario scenario, string logs, string repositoryRoot, List<ConsumerCommandEvent> events, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        if (scenario.Mode == ConsumerMode.PublishNativeAot)
        {
            var executable = Path.Combine(output, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            await RunRequiredAsync(executable, [], output, logs, repositoryRoot, scenario.Timeout, events, ct).ConfigureAwait(false);
        }
        else await RunRequiredAsync("dotnet", [Path.Combine(output, name + ".dll")], output, logs, repositoryRoot, scenario.Timeout, events, ct).ConfigureAwait(false);
    }

    internal async Task RefreshBinaryCompatibilityDeploymentMetadataAsync(
        string workspace,
        string project,
        string outputDirectory,
        IReadOnlyList<string> currentPackageIds,
        string currentPackageDirectory,
        string currentPackageVersion,
        IReadOnlyDictionary<string, string> externalPackageVersions,
        IReadOnlyList<string> externalPackageIds,
        string repositoryRoot,
        TimeSpan timeout,
        List<ConsumerCommandEvent> events,
        CancellationToken ct)
    {
        var consumerAssembly = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(project) + ".dll");
        if (!File.Exists(consumerAssembly))
            throw new ConsumerScenarioException("SPCONS011", "Binary compatibility consumer assembly is missing.");
        var beforeHash = await Hashing.Sha256FileAsync(consumerAssembly, ct).ConfigureAwait(false);

        _ = await new ConsumerCentralPackagesWriter().WriteAsync(
            workspace,
            currentPackageIds,
            currentPackageVersion,
            externalPackageVersions,
            ct).ConfigureAwait(false);
        var config = await new LocalNuGetConfigWriter().WriteAsync(
            workspace,
            currentPackageDirectory,
            ct,
            workspace,
            externalPackageIds).ConfigureAwait(false);
        var source = Path.GetDirectoryName(project)!;
        var logs = Path.Combine(workspace, "logs");
        await RunRequiredAsync(
            "dotnet",
            ["restore", project, "--configfile", config, "--packages", Path.Combine(workspace, "packages"), "--use-lock-file", "--force-evaluate"],
            source,
            logs,
            repositoryRoot,
            timeout,
            events,
            ct).ConfigureAwait(false);
        await RunRequiredAsync(
            "dotnet",
            ["msbuild", project, "-t:GenerateBuildDependencyFile", "-p:Configuration=Release"],
            source,
            logs,
            repositoryRoot,
            timeout,
            events,
            ct).ConfigureAwait(false);

        var afterHash = await Hashing.Sha256FileAsync(consumerAssembly, ct).ConfigureAwait(false);
        if (!string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility deployment metadata changed the consumer assembly.");
        events.Add(new(
            "binary-deployment-metadata",
            $"refresh-deps consumer-before-sha256={beforeHash} consumer-after-sha256={afterHash}",
            0,
            DateTimeOffset.UtcNow,
            0,
            "",
            ""));
    }

    private static async Task<IReadOnlyList<ConsumerCommandEvent>> ReplaceRuntimeAssembliesWithoutBuildAsync(string output, string feed, string version, IReadOnlyList<string> packageIds, CancellationToken ct)
    {
        var events = new List<ConsumerCommandEvent>();
        foreach (var id in packageIds)
        {
            var archivePath = Path.Combine(feed, $"{id}.{version}.nupkg");
            var target = Path.Combine(output, id + ".dll");
            await ExtractValidatedEntryAsync(archivePath, $"lib/net10.0/{id}.dll", target, ct).ConfigureAwait(false);
            var hash = await Hashing.Sha256FileAsync(target, ct).ConfigureAwait(false);
            events.Add(new("binary-runtime-replacement", $"replace-runtime package={id} sha256={hash}", 0, DateTimeOffset.UtcNow, 0, "", ""));
        }
        return events;
    }

    internal static IReadOnlyList<string> CurrentSmartPipeClosure(
        PackageGraphDocument graph,
        IReadOnlyList<string> packageIds)
    {
        var nodes = graph.Packages.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string packageId)
        {
            if (!closure.Add(packageId))
                return;

            if (nodes.TryGetValue(packageId, out var package))
                foreach (var dependency in package.CurrentDependencies.RequiredSmartPipePackages)
                    Visit(dependency);
        }

        foreach (var packageId in packageIds)
            Visit(packageId);

        return TopologicalPackageSorter.Sort(closure.ToDictionary(
            packageId => packageId,
            packageId => (IReadOnlyList<string>)(nodes.TryGetValue(packageId, out var package)
                ? package.CurrentDependencies.RequiredSmartPipePackages.Where(closure.Contains).ToArray()
                : []),
            StringComparer.OrdinalIgnoreCase));
    }

    internal static async Task ExtractValidatedEntryAsync(
        string archivePath,
        string entryPath,
        string targetPath,
        CancellationToken ct,
        NuGetPackageReaderOptions? options = null)
    {
        var safety = options ?? new NuGetPackageReaderOptions();
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await NuGetArchiveSafetyReader.PreflightAsync(stream, safety, ct).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = NuGetArchiveSafetyReader.ValidateEntries(archive, safety)
            .SingleOrDefault(item => item.Path.Equals(entryPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConsumerScenarioException("SPCONS017", $"Runtime assembly is missing from {Path.GetFileName(archivePath)}.");
        var bytes = await NuGetArchiveSafetyReader.ReadEntryAsync(entry, ct).ConfigureAwait(false);
        var temp = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static void CopyTemplateDirectory(string root, string templatePath, string destination)
    {
        var template = ConsumerScenarioLoader.ResolveContained(root, templatePath, "templatePath");
        var directory = Path.GetDirectoryName(template)!;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ConsumerScenarioException("SPCONS005", "Scenario template contains a directory link.");
            foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ConsumerScenarioException("SPCONS005", "Scenario template contains a file link.");
                var relative = Path.GetRelativePath(directory, file);
                var target = Path.GetFullPath(relative, destination); EnsureContained(Path.GetFullPath(destination), target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
        }
    }

    private static string RuntimeIdentifier() => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : OperatingSystem.IsMacOS() ? "osx-x64" : throw new ConsumerScenarioException("SPCONS018", "NativeAOT/trim scenario is unsupported on this OS.");
    internal static void ValidateBinaryCompatibilityPhases(IReadOnlyList<ConsumerCommandEvent> events, int expectedReplacements)
    {
        var builds = events.Select((item, index) => (item, index)).Where(x => x.item.Phase == "process" && x.item.Command.Contains(" build ", StringComparison.Ordinal)).ToArray();
        if (builds.Length != 1) throw new ConsumerScenarioException("SPCONS020", "Binary compatibility must contain exactly one build phase.");
        var buildIndex = builds[0].index;
        var metadata = events.Select((item, index) => (item, index)).Where(x => x.item.Phase == "binary-deployment-metadata").ToArray();
        if (metadata.Length != 1)
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility deployment metadata evidence is incomplete.");
        var metadataIndex = metadata[0].index;
        var metadataFields = metadata[0].item.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var beforeHashes = metadataFields.Where(field => field.StartsWith("consumer-before-sha256=", StringComparison.Ordinal)).ToArray();
        var afterHashes = metadataFields.Where(field => field.StartsWith("consumer-after-sha256=", StringComparison.Ordinal)).ToArray();
        var beforeHash = beforeHashes.Length == 1 ? beforeHashes[0]["consumer-before-sha256=".Length..] : "";
        var afterHash = afterHashes.Length == 1 ? afterHashes[0]["consumer-after-sha256=".Length..] : "";
        if (beforeHash.Length != 64
            || afterHash.Length != 64
            || !beforeHash.All(Uri.IsHexDigit)
            || !afterHash.All(Uri.IsHexDigit)
            || !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility consumer hash evidence is invalid.");
        var currentRestores = events.Select((item, index) => (item, index)).Where(x =>
            x.index > buildIndex
            && x.index < metadataIndex
            && x.item.Phase == "process"
            && x.item.Command.Contains(" restore ", StringComparison.Ordinal)).ToArray();
        var dependencyFiles = events.Select((item, index) => (item, index)).Where(x =>
            x.index > buildIndex
            && x.index < metadataIndex
            && x.item.Phase == "process"
            && x.item.Command.Contains(" msbuild ", StringComparison.Ordinal)
            && x.item.Command.Contains("-t:GenerateBuildDependencyFile", StringComparison.Ordinal)
            && !x.item.Command.Contains("Compile", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (currentRestores.Length != 1
            || dependencyFiles.Length != 1
            || currentRestores[0].index >= dependencyFiles[0].index)
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility deployment metadata commands are incomplete or unordered.");
        var replacements = events.Select((item, index) => (item, index)).Where(x => x.item.Phase == "binary-runtime-replacement").ToArray();
        if (replacements.Length != expectedReplacements || replacements.Any(x => x.index <= metadataIndex || !x.item.Command.Contains("sha256=", StringComparison.Ordinal)))
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility runtime replacement evidence is incomplete or unordered.");
        var firstReplacement = replacements[0].index;
        if (events.Skip(firstReplacement).Any(x => x.Phase == "process" && (x.Command.Contains(" build ", StringComparison.Ordinal) || x.Command.Contains(" restore ", StringComparison.Ordinal))))
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility phase 2 must not build or restore.");
        if (events.Zip(events.Skip(1), (left, right) => left.StartedUtc <= right.StartedUtc).Any(x => !x))
            throw new ConsumerScenarioException("SPCONS020", "Binary compatibility event timestamps are not monotonic.");
    }
    private static string Normalize(string path, string root) => "logs/" + Path.GetFileName(path);
    private static void EnsureContained(string root, string path) { var relative = Path.GetRelativePath(root, path); if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ConsumerScenarioException("SPCONS009", "Consumer workspace escapes repository root."); }
}
