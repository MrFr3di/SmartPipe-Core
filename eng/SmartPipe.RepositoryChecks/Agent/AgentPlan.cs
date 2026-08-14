using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Agent;

internal sealed class AgentPlanException : Exception
{
    public AgentPlanException(string message)
        : base(message)
    {
    }

    public AgentPlanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record AgentPrerequisiteDefinition
{
    public AgentPrerequisiteDefinition()
    {
    }

    [SetsRequiredMembers]
    public AgentPrerequisiteDefinition(string epic, string commit)
    {
        Epic = epic;
        Commit = commit;
    }

    [JsonPropertyOrder(0)]
    public required string Epic { get; init; }

    [JsonPropertyOrder(1)]
    public required string Commit { get; init; }
}

internal sealed record AgentTrackedPlanDefinition
{
    public AgentTrackedPlanDefinition()
    {
    }

    [SetsRequiredMembers]
    public AgentTrackedPlanDefinition(string path, string section)
    {
        Path = path;
        Section = section;
    }

    [JsonPropertyOrder(0)]
    public required string Path { get; init; }

    [JsonPropertyOrder(1)]
    public required string Section { get; init; }
}

internal sealed record AgentTaskDefinition
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1)]
    public required string Title { get; init; }

    [JsonPropertyOrder(2)]
    public required IReadOnlyList<string> AllowedPaths { get; init; }

    [JsonPropertyOrder(3)]
    public required IReadOnlyList<string> Contracts { get; init; }

    [JsonPropertyOrder(4)]
    public required string VerificationProfile { get; init; }
}

internal sealed record AgentPlanDocument
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string Epic { get; init; }

    [JsonPropertyOrder(2)]
    public required string BaseRef { get; init; }

    [JsonPropertyOrder(3)]
    public required string BaseCommit { get; init; }

    [JsonPropertyOrder(4)]
    public required IReadOnlyList<AgentPrerequisiteDefinition> Prerequisites { get; init; }

    [JsonPropertyOrder(5)]
    public required AgentTrackedPlanDefinition TrackedPlan { get; init; }

    [JsonPropertyOrder(6)]
    public required IReadOnlyList<AgentTaskDefinition> Tasks { get; init; }
}

internal sealed record ActiveExecPlan(
    AgentPlanDocument Document,
    string ActivePlanPath,
    string PlanSha256)
{
    public string Epic => Document.Epic;
    public string BaseRef => Document.BaseRef;
    public string BaseCommit => Document.BaseCommit;
    public IReadOnlyList<AgentPrerequisiteDefinition> Prerequisites => Document.Prerequisites;
    public AgentTrackedPlanDefinition TrackedPlan => Document.TrackedPlan;
    public IReadOnlyList<AgentTaskDefinition> Tasks => Document.Tasks;

    public AgentTaskDefinition FindTask(string task) =>
        Tasks.FirstOrDefault(item => string.Equals(item.Id, task, StringComparison.Ordinal))
        ?? throw new AgentPlanException($"Task '{task}' is not defined in the active plan.");
}

internal sealed class ActiveExecPlanLoader
{
    private const int SchemaVersion = 1;
    private const int MaximumPrerequisites = 16;
    private const int MaximumTasks = 64;
    private const int MaximumTaskPaths = 32;
    private const int MaximumTaskContracts = 16;
    private const int MaximumTaskIdLength = 32;
    private const int MaximumTaskTitleLength = 256;
    private const int MaximumPathLength = 256;
    private const int MaximumContractLength = 256;
    private const int MaximumProfileLength = 128;
    private const int MaximumSectionLength = 256;
    private const string StartMarker = "<!-- smartpipe-agent-context:v1:start -->";
    private const string EndMarker = "<!-- smartpipe-agent-context:v1:end -->";
    private const string ExpectedEpic = "SP220-05";
    private const string ExpectedBaseRef = "origin/sp220/checkpoint-c";
    private static readonly Regex JsonFence = new(
        @"\A\s*```json\r?\n(?<json>.*?)\r?\n```\s*\z",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public async Task<ActiveExecPlan> LoadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or InvalidDataException)
        {
            throw new AgentPlanException("Repository root is invalid.", exception);
        }

        var activeDirectory = Path.Combine(root, ".agent", "exec-plans", "active");
        if (!Directory.Exists(activeDirectory))
        {
            throw new AgentPlanException("The active ExecPlan directory is missing.");
        }

        RejectLink(activeDirectory, "active plan directory");

        string[] planPaths;
        try
        {
            planPaths = Directory.EnumerateFiles(activeDirectory, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, RepositoryPaths.FileSystemPathComparer)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentPlanException("The active ExecPlan directory could not be read.", exception);
        }

        if (planPaths.Length != 1)
        {
            throw new AgentPlanException("Exactly one active ExecPlan markdown file is required.");
        }

        var activePlanPath = planPaths[0];
        try
        {
            RepositoryPaths.RequireExistingRegularFile(root, activePlanPath, "active ExecPlan");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new AgentPlanException("The active ExecPlan must be a regular repository file.", exception);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(activePlanPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentPlanException("The active ExecPlan could not be read.", exception);
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentPlanException("The active ExecPlan must be valid UTF-8.", exception);
        }

        var document = ParseDocument(text);
        Validate(document);
        ValidateTrackedPlan(root, document.TrackedPlan);
        return new(document, activePlanPath, Hashing.Sha256Hex(bytes));
    }

    private static AgentPlanDocument ParseDocument(string text)
    {
        if (Count(text, StartMarker) != 1 || Count(text, EndMarker) != 1)
        {
            throw new AgentPlanException("The active ExecPlan must contain exactly one context marker pair.");
        }

        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (end <= start)
        {
            throw new AgentPlanException("The active ExecPlan context markers are out of order.");
        }

        var block = text[(start + StartMarker.Length)..end];
        var match = JsonFence.Match(block);
        if (!match.Success)
        {
            throw new AgentPlanException("The active ExecPlan context must contain one fenced JSON document.");
        }

        try
        {
            return JsonSerializer.Deserialize(match.Groups["json"].Value, AgentJsonContext.Default.AgentPlanDocument)
                ?? throw new AgentPlanException("The active ExecPlan context cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new AgentPlanException("The active ExecPlan context JSON is invalid.", exception);
        }
    }

    private static void Validate(AgentPlanDocument document)
    {
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new AgentPlanException("The active ExecPlan context schema version is unsupported.");
        }

        if (!string.Equals(document.Epic, ExpectedEpic, StringComparison.Ordinal))
        {
            throw new AgentPlanException("The active ExecPlan context epic is unsupported.");
        }

        if (!string.Equals(document.BaseRef, ExpectedBaseRef, StringComparison.Ordinal))
        {
            throw new AgentPlanException("The active ExecPlan context base ref is not canonical.");
        }

        RequireCommit(document.BaseCommit, "base commit");
        if (document.Prerequisites is null || document.Prerequisites.Count == 0 || document.Prerequisites.Count > MaximumPrerequisites)
        {
            throw new AgentPlanException("The active ExecPlan context must declare prerequisites.");
        }

        var prerequisiteEpics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prerequisite in document.Prerequisites)
        {
            if (prerequisite is null || !IsCanonicalEpic(prerequisite.Epic) || !prerequisiteEpics.Add(prerequisite.Epic))
            {
                throw new AgentPlanException("The active ExecPlan context prerequisites must use unique canonical epics.");
            }

            RequireCommit(prerequisite.Commit, "prerequisite commit");
        }

        if (document.TrackedPlan is null
            || !IsSafeRelativePath(document.TrackedPlan.Path, allowGlob: false)
            || document.TrackedPlan.Path.Length > MaximumPathLength
            || string.IsNullOrWhiteSpace(document.TrackedPlan.Section)
            || document.TrackedPlan.Section.Length > MaximumSectionLength
            || document.TrackedPlan.Section.Contains('\r')
            || document.TrackedPlan.Section.Contains('\n'))
        {
            throw new AgentPlanException("The active ExecPlan tracked plan reference is invalid.");
        }

        if (document.Tasks is null || document.Tasks.Count == 0 || document.Tasks.Count > MaximumTasks)
        {
            throw new AgentPlanException("The active ExecPlan context must declare tasks.");
        }

        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in document.Tasks)
        {
            if (task is null || !IsCanonicalTask(task.Id) || task.Id.Length > MaximumTaskIdLength || !taskIds.Add(task.Id)
                || string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > MaximumTaskTitleLength
                || task.Title.Contains('\r') || task.Title.Contains('\n')
                || task.AllowedPaths is null || task.AllowedPaths.Count == 0 || task.AllowedPaths.Count > MaximumTaskPaths
                || task.Contracts is null || task.Contracts.Count == 0 || task.Contracts.Count > MaximumTaskContracts
                || !IsCanonicalProfile(task.VerificationProfile) || task.VerificationProfile.Length > MaximumProfileLength)
            {
                throw new AgentPlanException("The active ExecPlan task definition is invalid.");
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in task.AllowedPaths)
            {
                if (!IsSafeRelativePath(path, allowGlob: true) || path.Length > MaximumPathLength || !paths.Add(path))
                {
                    throw new AgentPlanException("The active ExecPlan task scope contains an unsafe or duplicate path.");
                }
            }

            var contracts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contract in task.Contracts)
            {
                if (string.IsNullOrWhiteSpace(contract)
                    || contract.Length > MaximumContractLength
                    || contract.Contains('\r')
                    || contract.Contains('\n')
                    || !contracts.Add(contract))
                {
                    throw new AgentPlanException("The active ExecPlan task contracts must be nonempty single-line values.");
                }
            }
        }
    }

    private static void ValidateTrackedPlan(string root, AgentTrackedPlanDefinition trackedPlan)
    {
        string fullPath;
        try
        {
            fullPath = RepositoryPaths.ResolveWithinRoot(root, trackedPlan.Path, "tracked plan");
            RepositoryPaths.RequireExistingRegularFile(root, fullPath, "tracked plan");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new AgentPlanException("The tracked plan reference is not a contained regular file.", exception);
        }

        string text;
        try
        {
            text = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new AgentPlanException("The tracked plan reference could not be read.", exception);
        }

        var found = text.Split('\n').Any(line =>
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                return false;
            }

            var heading = trimmed.Trim().TrimStart('#').Trim();
            return heading.Equals(trackedPlan.Section, StringComparison.Ordinal)
                || heading.StartsWith(trackedPlan.Section + " ", StringComparison.Ordinal)
                || heading.StartsWith(trackedPlan.Section + "—", StringComparison.Ordinal);
        });
        if (!found)
        {
            throw new AgentPlanException("The tracked plan section heading is missing.");
        }
    }

    private static bool IsCanonicalEpic(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value == value.Trim()
        && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');

    private static bool IsCanonicalTask(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length > 1
        && value[0] == 'T'
        && value[1] is >= '1' and <= '9'
        && value[2..].All(char.IsAsciiDigit);

    private static bool IsCanonicalProfile(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value == value.Trim()
        && value.Split('-').All(segment => segment.Length > 0 && segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'))
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsSafeRelativePath(string value, bool allowGlob)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Contains('\\')
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.Contains(':'))
        {
            return false;
        }

        var path = value;
        if (allowGlob && path.EndsWith("/**", StringComparison.Ordinal))
        {
            path = path[..^3];
        }
        else if (path.Contains('*'))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.Length > 0
            && segments.All(segment => segment.Length > 0 && segment is not "." and not "..")
            && !path.Contains('*')
            && (allowGlob || !value.Contains('*'));
    }

    private static void RequireCommit(string value, string description)
    {
        if (value.Length != 40 || value.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new AgentPlanException($"The active ExecPlan {description} is not a lowercase SHA-1.");
        }
    }

    private static int Count(string value, string token)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length)
        {
            count++;
        }

        return count;
    }

    private static void RejectLink(string path, string description)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AgentPlanException($"The {description} must not be a symbolic link or reparse point.");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentPlanDocument))]
[JsonSerializable(typeof(AgentPrerequisiteDefinition))]
[JsonSerializable(typeof(AgentTrackedPlanDefinition))]
[JsonSerializable(typeof(AgentTaskDefinition))]
internal partial class AgentJsonContext : JsonSerializerContext;
