using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineActivationExceptionTests
{
    [Fact]
    public void Constructor_CopiesIdentityPrimaryAndOrderedCleanup()
    {
        var key = new PipelineKey("orders");
        var runId = Guid.NewGuid();
        var primary = new InvalidOperationException("primary-secret");
        var firstCleanup = new IOException("first-cleanup");
        var cleanup = new List<Exception> { firstCleanup };

        var error = new PipelineActivationException(key, runId, primary, cleanup);
        cleanup.Add(new TimeoutException("later-cleanup"));

        error.PipelineKey.Should().Be(key);
        error.RunId.Should().Be(runId);
        error.InnerException.Should().BeSameAs(primary);
        error.CleanupExceptions.Should().ContainSingle().Which.Should().BeSameAs(firstCleanup);

        var act = () => ((IList<Exception>)error.CleanupExceptions).Add(new Exception());
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Constructor_RejectsInvalidInputsAndNullCleanupEntries()
    {
        var key = new PipelineKey("orders");
        var runId = Guid.NewGuid();
        var primary = new InvalidOperationException();

        var nullPrimary = () => new PipelineActivationException(key, runId, null!, []);
        var nullCleanup = () => new PipelineActivationException(key, runId, primary, null!);
        var nullEntry = () => new PipelineActivationException(key, runId, primary, new Exception[] { null! });
        var defaultKey = () => new PipelineActivationException(default, runId, primary, []);
        var emptyRun = () => new PipelineActivationException(key, Guid.Empty, primary, []);

        nullPrimary.Should().Throw<ArgumentNullException>();
        nullCleanup.Should().Throw<ArgumentNullException>();
        nullEntry.Should().Throw<ArgumentException>();
        defaultKey.Should().Throw<ArgumentException>();
        emptyRun.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Message_ContainsOnlySafePipelineIdentity()
    {
        var key = new PipelineKey("orders");
        var runId = Guid.NewGuid();
        var primary = new InvalidOperationException("primary-secret");
        var cleanup = new IOException("cleanup-secret");

        var error = new PipelineActivationException(key, runId, primary, [cleanup]);

        error.Message.Should().Contain(key.Value).And.Contain(runId.ToString());
        error.Message.Should().NotContain(primary.Message);
        error.Message.Should().NotContain(cleanup.Message);
        error.Message.Should().NotContain(nameof(InvalidOperationException));
        error.Message.Should().NotContain(nameof(IOException));
    }
}
