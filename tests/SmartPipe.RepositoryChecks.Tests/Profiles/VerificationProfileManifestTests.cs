using System.Text.Json;
using SmartPipe.RepositoryChecks.Profiles;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Profiles;

public sealed class VerificationProfileManifestTests
{
    [Fact]
    public async Task LoadAsync_ReadsCanonicalManifestAndPreservesDeclaredOrder()
    {
        var manifest = await VerificationProfileManifestLoader.LoadAsync(
            RepositoryRoot(), TestContext.Current.CancellationToken);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(["repository-checks-fast", "sp220-05"], manifest.Profiles.Select(profile => profile.Name));
        Assert.Equal(
            ["verify-package-projects", "verify-central-packages-current", "verify-package-graph-current-source"],
            manifest.Profiles[0].Checks);
        Assert.Equal(
            ["verify-package-projects", "verify-central-packages-current", "verify-package-graph-current-source", "verify-lock-files"],
            manifest.Profiles[1].Checks);
    }

    [Fact]
    public void Deserialize_RejectsUnknownProperty()
    {
        var json = VerificationProfileManifestLoader.Serialize(CreateManifest())
            .Replace("{\n", "{\n  \"unexpected\": true,\n", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsNonCanonicalFormatting()
    {
        var json = VerificationProfileManifestLoader.Serialize(CreateManifest()).Replace("\n", "", StringComparison.Ordinal);

        Assert.Contains("canonical", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(json)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsDuplicateProfileAndCheck()
    {
        var canonical = VerificationProfileManifestLoader.Serialize(CreateManifest());

        var duplicateProfile = canonical.Replace("\"name\": \"sp220-05\"", "\"name\": \"repository-checks-fast\"", StringComparison.Ordinal);
        Assert.Contains("unique", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(duplicateProfile)).Message, StringComparison.OrdinalIgnoreCase);

        var duplicateCheck = canonical.Replace("\"verify-lock-files\"", "\"verify-package-projects\"", StringComparison.Ordinal);
        Assert.Contains("duplicate", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(duplicateCheck)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedCheck()
    {
        var canonical = VerificationProfileManifestLoader.Serialize(CreateManifest())
            .Replace("\"verify-lock-files\"", "\"verify-unsupported\"", StringComparison.Ordinal);

        Assert.Contains("unsupported", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(canonical)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("capture-baseline")]
    [InlineData("verify-baseline")]
    [InlineData("provision-baseline")]
    [InlineData("pack-packages")]
    [InlineData("run-consumers")]
    public void Deserialize_RejectsAcquisitionAndMutatingCheckIds(string checkId)
    {
        var canonical = VerificationProfileManifestLoader.Serialize(CreateManifest())
            .Replace("\"verify-lock-files\"", $"\"{checkId}\"", StringComparison.Ordinal);

        Assert.Contains("unsupported", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(canonical)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("UPPERCASE")]
    [InlineData("with space")]
    [InlineData("path/escape")]
    public void Deserialize_RejectsNonCanonicalProfileIdentity(string name)
    {
        var canonical = VerificationProfileManifestLoader.Serialize(CreateManifest())
            .Replace("\"repository-checks-fast\"", $"\"{name}\"", StringComparison.Ordinal);

        Assert.Contains("canonical", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(canonical)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsSchemaVersionAndEmptyCollections()
    {
        var unsupportedSchema = VerificationProfileManifestLoader.Serialize(CreateManifest())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        Assert.Contains("schema", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Deserialize(unsupportedSchema)).Message, StringComparison.OrdinalIgnoreCase);

        var emptyProfiles = new VerificationProfileManifest
        {
            SchemaVersion = 1,
            Profiles = [],
        };
        Assert.Contains("at least one", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Serialize(emptyProfiles)).Message, StringComparison.OrdinalIgnoreCase);

        var emptyChecks = new VerificationProfileManifest
        {
            SchemaVersion = 1,
            Profiles = [new VerificationProfile("profile", [])],
        };
        Assert.Contains("at least one", Assert.Throws<JsonException>(() => VerificationProfileManifestLoader.Serialize(emptyChecks)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsBomAndCrLf()
    {
        var canonical = VerificationProfileManifestLoader.Serialize(CreateManifest());
        using (var bomFixture = new RepositoryTestDirectory())
        {
            bomFixture.WriteBytes(
                VerificationProfileManifestLoader.RelativeManifestPath,
                [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(canonical)]);
            await Assert.ThrowsAsync<JsonException>(() => VerificationProfileManifestLoader.LoadAsync(
                bomFixture.Path, TestContext.Current.CancellationToken));
        }

        using var crlfFixture = new RepositoryTestDirectory();
        crlfFixture.Write(
            VerificationProfileManifestLoader.RelativeManifestPath,
            canonical.Replace("\n", "\r\n", StringComparison.Ordinal));
        await Assert.ThrowsAsync<JsonException>(() => VerificationProfileManifestLoader.LoadAsync(
            crlfFixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_RejectsManifestSymlink()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write(
            "eng/verification-profiles-target.json",
            VerificationProfileManifestLoader.Serialize(CreateManifest()));
        if (!fixture.TryCreateFileLink(
                VerificationProfileManifestLoader.RelativeManifestPath,
                "eng/verification-profiles-target.json"))
        {
            return;
        }

        await Assert.ThrowsAsync<JsonException>(() => VerificationProfileManifestLoader.LoadAsync(
            fixture.Path, TestContext.Current.CancellationToken));
    }

    private static VerificationProfileManifest CreateManifest() => new()
    {
        SchemaVersion = 1,
        Profiles =
        [
            new VerificationProfile("repository-checks-fast", [
                "verify-package-projects",
                "verify-central-packages-current",
                "verify-package-graph-current-source",
            ]),
            new VerificationProfile("sp220-05", [
                "verify-package-projects",
                "verify-central-packages-current",
                "verify-package-graph-current-source",
                "verify-lock-files",
            ]),
        ],
    };

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
