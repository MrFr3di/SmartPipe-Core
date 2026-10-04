using SmartPipe.RepositoryChecks.Release;

namespace SmartPipe.RepositoryChecks.Tests.Release;

public sealed class ReleaseNotesExtractorTests
{
    [Fact]
    public void Extract_ReturnsOnlyRequestedDatedSection()
    {
        const string changelog = """
            # Changelog

            ## [2.2.0] - 2026-10-04

            ### Added
            - New package graph.

            ### Fixed
            - Release flow.

            ## [2.1.2] - 2026-07-01

            - Older release.
            """;

        var notes = ReleaseNotesExtractor.Extract(changelog, "2.2.0");

        Assert.Equal("""
            ### Added
            - New package graph.

            ### Fixed
            - Release flow.
            """ + "
", notes);
    }

    [Fact]
    public void Extract_RejectsDevelopmentHeading()
    {
        const string changelog = """
            # Changelog

            ## [2.2.0] — Development

            - Not final.
            """;

        var error = Assert.Throws<ReleaseNotesException>(
            () => ReleaseNotesExtractor.Extract(changelog, "2.2.0"));

        Assert.Equal("SPRELNOTES002", error.Code);
    }

    [Fact]
    public void Extract_RejectsMissingVersion()
    {
        const string changelog = """
            # Changelog

            ## [2.1.2] - 2026-07-01

            - Older release.
            """;

        var error = Assert.Throws<ReleaseNotesException>(
            () => ReleaseNotesExtractor.Extract(changelog, "2.2.0"));

        Assert.Equal("SPRELNOTES001", error.Code);
    }

    [Fact]
    public void Extract_RejectsDuplicateVersionSection()
    {
        const string changelog = """
            # Changelog

            ## [2.2.0] - 2026-10-04
            - First.

            ## [2.2.0] - 2026-10-05
            - Second.
            """;

        var error = Assert.Throws<ReleaseNotesException>(
            () => ReleaseNotesExtractor.Extract(changelog, "2.2.0"));

        Assert.Equal("SPRELNOTES003", error.Code);
    }

    [Fact]
    public void Extract_RejectsEmptySection()
    {
        const string changelog = """
            # Changelog

            ## [2.2.0] - 2026-10-04

            ## [2.1.2] - 2026-07-01
            - Older release.
            """;

        var error = Assert.Throws<ReleaseNotesException>(
            () => ReleaseNotesExtractor.Extract(changelog, "2.2.0"));

        Assert.Equal("SPRELNOTES004", error.Code);
    }

    [Fact]
    public void Extract_StopsBeforeNextVersionHeading()
    {
        const string changelog = """
            # Changelog

            ## [2.2.0] - 2026-10-04
            - Current.

            ## [2.1.2] - 2026-07-01
            - Historical.
            """;

        var notes = ReleaseNotesExtractor.Extract(changelog, "2.2.0");

        Assert.Equal("- Current.
", notes);
        Assert.DoesNotContain("Historical", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_RemovesLinkReferenceDefinitions()
    {
        const string changelog = """
            # Changelog

            ## [2.2.0] - 2026-10-04

            - Current.

            [2.2.0]: https://github.com/MrFr3di/SmartPipe-Core/releases/tag/v2.2.0
            [Unreleased]: https://github.com/MrFr3di/SmartPipe-Core/compare/v2.2.0...HEAD

            ## [2.1.2] - 2026-07-01
            - Historical.
            """;

        var notes = ReleaseNotesExtractor.Extract(changelog, "2.2.0");

        Assert.Equal("- Current.
", notes);
    }

    [Theory]
    [InlineData("02.2.0")]
    [InlineData("2.02.0")]
    [InlineData("2.2.00")]
    [InlineData("2.2.0+build.1")]
    public void Extract_RejectsNonCanonicalVersion(string version)
    {
        const string changelog = "## [2.2.0] - 2026-10-04\n- Current.\n";

        Assert.Throws<ReleaseVersionException>(
            () => ReleaseNotesExtractor.Extract(changelog, version));
    }
}
