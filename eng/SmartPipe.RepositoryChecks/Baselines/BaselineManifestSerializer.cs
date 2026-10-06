using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Baselines;

internal static class BaselineManifestSerializer
{
    public static string Serialize(BaselineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        BaselineManifestValidator.Validate(manifest);

        var canonical = manifest with
        {
            Repository = manifest.Repository with
            {
                RequiredWorkflows = manifest.Repository.RequiredWorkflows
                    .OrderBy(workflow => workflow.Name, StringComparer.Ordinal)
                    .ToArray(),
            },
            Packages = manifest.Packages
                .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Id, StringComparer.Ordinal)
                .ToArray(),
        };

        return CanonicalJson.Serialize(canonical, BaselineJsonContext.Default.BaselineManifest);
    }

    public static byte[] SerializeToUtf8Bytes(BaselineManifest manifest) =>
        CanonicalText.ToUtf8Bytes(Serialize(manifest));

    public static Task WriteAsync(
        string path,
        BaselineManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.WriteAllBytesAsync(path, SerializeToUtf8Bytes(manifest), cancellationToken);
    }

    public static BaselineManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var manifest = JsonSerializer.Deserialize(json, BaselineJsonContext.Default.BaselineManifest)
            ?? throw new JsonException("Baseline manifest cannot be null.");
        BaselineManifestValidator.Validate(manifest);

        return manifest;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BaselineManifest))]
internal partial class BaselineJsonContext : JsonSerializerContext;
