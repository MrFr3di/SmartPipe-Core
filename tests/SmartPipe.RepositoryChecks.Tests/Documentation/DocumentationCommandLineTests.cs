using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Documentation;

public sealed class DocumentationCommandLineTests
{
    [Fact]
    public void Parse_ReturnsDocumentationOptions()
    {
        using var repository = new RepositoryTestDirectory();

        var command = Assert.IsType<VerifyDocumentationOptions>(CommandLineParser.Parse(
            ["verify-docs", "--repo-root", repository.Path]));

        Assert.Equal(repository.Path, command.RepositoryRoot);
    }

    [Fact]
    public void Parse_RejectsUnknownDocumentationOption()
    {
        using var repository = new RepositoryTestDirectory();

        var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            ["verify-docs", "--repo-root", repository.Path, "--rewrite", "true"]));

        Assert.Equal("Unknown option '--rewrite'.", exception.Message);
    }
}
