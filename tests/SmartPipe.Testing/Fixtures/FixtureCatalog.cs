using System.Security.Cryptography;
namespace SmartPipe.Testing.Fixtures;

public static class FixtureEnvironment
{
    public const string FixturesRoot = "SMARTPIPE_FIXTURES_ROOT";
    public const string EnableRealFixtures = "SMARTPIPE_ENABLE_REAL_FIXTURES";
    public const string EnableLargeFixtures = "SMARTPIPE_ENABLE_LARGE_FIXTURES";
    public const string EnableHugeFixtures = "SMARTPIPE_ENABLE_HUGE_FIXTURES";
    public const string EnableStressTests = "SMARTPIPE_ENABLE_STRESS_TESTS";
    public const string EnableSlowTests = "SMARTPIPE_ENABLE_SLOW_TESTS";
    public const string SocPokecPath = "SMARTPIPE_SOC_POKEC_PATH";

    public static bool RealFixturesEnabled => IsEnabled(EnableRealFixtures);
    public static bool LargeFixturesEnabled => IsEnabled(EnableLargeFixtures);
    public static bool HugeFixturesEnabled => IsEnabled(EnableHugeFixtures);
    public static bool StressTestsEnabled => IsEnabled(EnableStressTests);
    public static bool SlowTestsEnabled => IsEnabled(EnableSlowTests);

    public static string? ConfiguredRoot => Environment.GetEnvironmentVariable(FixturesRoot);

    public static string? ConfiguredSocPokecPath => Environment.GetEnvironmentVariable(SocPokecPath);

    public static bool IsEnabled(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}

public static class FixtureCategories
{
    public const string Golden = "Golden";
    public const string RealFixture = "RealFixture";
    public const string LargeFixture = "LargeFixture";
    public const string HugeFixture = "HugeFixture";
    public const string Stress = "Stress";
    public const string Aot = "Aot";
    public const string Slow = "Slow";
}

public enum FixtureSizeClass
{
    Small,
    Medium,
    Large,
    Huge,
}

public enum FixtureNewlineStyle
{
    None,
    Lf,
    Crlf,
    Cr,
    Mixed,
}

public sealed record FixtureInfo(
    string Path,
    string RelativePath,
    string Extension,
    long SizeBytes,
    FixtureSizeClass SizeClass,
    string? Bom,
    FixtureNewlineStyle NewlineStyle,
    string? Sha256);

public static class FixtureCatalog
{
    public const long SmallMaxBytes = 1L * 1024 * 1024;
    public const long MediumMaxBytes = 50L * 1024 * 1024;
    public const long LargeMaxBytes = 250L * 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".txt",
        ".json",
        ".jsonl",
        ".ndjson",
    };

    public static IReadOnlyList<FixtureInfo> Discover(
        string root,
        bool includeLargeAndHugeHashes = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
            return [];

        var rootFullPath = Path.GetFullPath(root);
        var fixtures = new List<FixtureInfo>();

        foreach (var path in Directory.EnumerateFiles(rootFullPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(path);
            if (!SupportedExtensions.Contains(extension))
                continue;

            var fileInfo = new FileInfo(path);
            var sizeClass = Classify(fileInfo.Length);
            var shouldHash = includeLargeAndHugeHashes || sizeClass is FixtureSizeClass.Small or FixtureSizeClass.Medium;

            fixtures.Add(new FixtureInfo(
                path,
                Path.GetRelativePath(rootFullPath, path).Replace(Path.DirectorySeparatorChar, '/'),
                extension.TrimStart('.').ToLowerInvariant(),
                fileInfo.Length,
                sizeClass,
                DetectBom(path),
                DetectNewlineStyle(path),
                shouldHash ? ComputeSha256(path, ct) : null));
        }

        fixtures.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return fixtures;
    }

    public static FixtureSizeClass Classify(long sizeBytes)
    {
        if (sizeBytes <= SmallMaxBytes)
            return FixtureSizeClass.Small;
        if (sizeBytes <= MediumMaxBytes)
            return FixtureSizeClass.Medium;
        if (sizeBytes <= LargeMaxBytes)
            return FixtureSizeClass.Large;

        return FixtureSizeClass.Huge;
    }

    public static string? DetectBom(string path)
    {
        Span<byte> bytes = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        var read = stream.Read(bytes);
        bytes = bytes[..read];

        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        ReadOnlySpan<byte> utf32LeBom = [0xFF, 0xFE, 0x00, 0x00];
        ReadOnlySpan<byte> utf32BeBom = [0x00, 0x00, 0xFE, 0xFF];
        ReadOnlySpan<byte> utf16LeBom = [0xFF, 0xFE];
        ReadOnlySpan<byte> utf16BeBom = [0xFE, 0xFF];

        if (bytes.StartsWith(utf8Bom))
            return "utf-8";
        if (bytes.StartsWith(utf32LeBom))
            return "utf-32-le";
        if (bytes.StartsWith(utf32BeBom))
            return "utf-32-be";
        if (bytes.StartsWith(utf16LeBom))
            return "utf-16-le";
        if (bytes.StartsWith(utf16BeBom))
            return "utf-16-be";

        return null;
    }

    public static FixtureNewlineStyle DetectNewlineStyle(string path)
    {
        Span<byte> buffer = stackalloc byte[64 * 1024];
        using var stream = File.OpenRead(path);
        var read = stream.Read(buffer);
        buffer = buffer[..read];

        var hasLf = false;
        var hasCrlf = false;
        var hasCr = false;

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == (byte)'\r')
            {
                if (i + 1 < buffer.Length && buffer[i + 1] == (byte)'\n')
                {
                    hasCrlf = true;
                    i++;
                }
                else
                {
                    hasCr = true;
                }
            }
            else if (buffer[i] == (byte)'\n')
            {
                hasLf = true;
            }
        }

        var styles = (hasLf ? 1 : 0) + (hasCrlf ? 1 : 0) + (hasCr ? 1 : 0);
        if (styles == 0)
            return FixtureNewlineStyle.None;
        if (styles > 1)
            return FixtureNewlineStyle.Mixed;
        if (hasCrlf)
            return FixtureNewlineStyle.Crlf;
        if (hasLf)
            return FixtureNewlineStyle.Lf;

        return FixtureNewlineStyle.Cr;
    }

    private static string ComputeSha256(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class FixtureSkip
{
    public const string RealFixturesDisabled =
        "Set SMARTPIPE_ENABLE_REAL_FIXTURES=1 and SMARTPIPE_FIXTURES_ROOT to run real fixture tests.";

    public const string LargeFixturesDisabled =
        "Set SMARTPIPE_ENABLE_LARGE_FIXTURES=1 to run large fixture tests.";

    public const string HugeFixturesDisabled =
        "Set SMARTPIPE_ENABLE_HUGE_FIXTURES=1 and SMARTPIPE_SOC_POKEC_PATH to run huge fixture tests.";

    public const string StressTestsDisabled =
        "Set SMARTPIPE_ENABLE_STRESS_TESTS=1 to run stress tests.";

    public const string SlowTestsDisabled =
        "Set SMARTPIPE_ENABLE_SLOW_TESTS=1 to run slow tests.";
}
