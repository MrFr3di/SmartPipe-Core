using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Profiles;

internal sealed record VerificationProfileManifest
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required IReadOnlyList<VerificationProfile> Profiles { get; init; }
}

internal sealed record VerificationProfile
{
    public VerificationProfile(string name, IReadOnlyList<string> checks)
    {
        Name = name;
        Checks = checks;
    }

    [JsonPropertyOrder(0)]
    public string Name { get; init; }

    [JsonPropertyOrder(1)]
    public IReadOnlyList<string> Checks { get; init; }
}

internal static class VerificationProfileManifestLoader
{
    public const string RelativeManifestPath = "eng/verification-profiles.json";
    private const int SchemaVersion = 1;

    public static readonly IReadOnlySet<string> SupportedCheckIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "verify-package-projects",
        "verify-central-packages-current",
        "verify-package-graph-current-source",
        "verify-lock-files",
    };

    public static async Task<VerificationProfileManifest> LoadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.Combine(root, RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar));
        RejectLinkedManifest(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) || bytes.Contains((byte)'\r'))
        {
            throw new JsonException("Verification profile manifest must be UTF-8 without BOM and use LF line endings.");
        }

        try
        {
            return Deserialize(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            throw new JsonException("Verification profile manifest must be valid UTF-8.");
        }
    }

    private static void RejectLinkedManifest(string path)
    {
        var file = new FileInfo(path);
        try
        {
            file.Refresh();
            FileSystemInfo? resolvedLink = null;
            try
            {
                resolvedLink = file.ResolveLinkTarget(returnFinalTarget: false);
            }
            catch (PlatformNotSupportedException)
            {
                // FileAttributes.ReparsePoint and LinkTarget still provide the guard.
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0
                || file.LinkTarget is not null
                || resolvedLink is not null)
            {
                throw new JsonException("Verification profile manifest must be a regular repository file.");
            }
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new JsonException("Verification profile manifest could not be inspected.", exception);
        }
    }

    public static VerificationProfileManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var manifest = JsonSerializer.Deserialize(json, VerificationProfileJsonContext.Default.VerificationProfileManifest)
            ?? throw new JsonException("Verification profile manifest cannot be null.");

        Validate(manifest);
        if (!string.Equals(json, Serialize(manifest), StringComparison.Ordinal))
        {
            throw new JsonException("Verification profile manifest must use canonical JSON formatting and ordering.");
        }

        return manifest;
    }

    public static string Serialize(VerificationProfileManifest manifest)
    {
        Validate(manifest);
        return CanonicalJson.Serialize(manifest, VerificationProfileJsonContext.Default.VerificationProfileManifest);
    }

    private static void Validate(VerificationProfileManifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new JsonException($"Unsupported verification profile schema version '{manifest.SchemaVersion}'.");
        }

        if (manifest.Profiles is null || manifest.Profiles.Count == 0)
        {
            throw new JsonException("profiles must contain at least one profile.");
        }

        var profileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in manifest.Profiles)
        {
            if (profile is null || !IsCanonicalIdentity(profile.Name) || !profileNames.Add(profile.Name))
            {
                throw new JsonException("profiles must contain unique canonical names.");
            }

            if (profile.Checks is null || profile.Checks.Count == 0)
            {
                throw new JsonException($"Profile '{profile.Name}' must contain at least one check.");
            }

            var checkIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var checkId in profile.Checks)
            {
                if (!IsCanonicalIdentity(checkId) || !SupportedCheckIds.Contains(checkId))
                {
                    throw new JsonException($"Profile '{profile.Name}' contains unsupported check '{checkId}'.");
                }

                if (!checkIds.Add(checkId))
                {
                    throw new JsonException($"Profile '{profile.Name}' contains duplicate check '{checkId}'.");
                }
            }
        }
    }

    private static bool IsCanonicalIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Split('-').All(segment => segment.Length > 0 && segment.All(char.IsAsciiLetterOrDigit))
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VerificationProfileManifest))]
[JsonSerializable(typeof(VerificationProfile))]
internal partial class VerificationProfileJsonContext : JsonSerializerContext;
