using System.Runtime.ExceptionServices;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>
/// Direct coverage for the Core failure-precedence policy every provider component depends on: the primary operation
/// failure is surfaced first, cleanup failures never replace it, and an inverted argument order or a cleanup-first
/// surface is observable here rather than only through a combined failure a real server refuses to produce.
/// </summary>
public sealed class PostgreSqlFailuresTests
{
    private const string CombinedMessage = "operation failed and cleanup also failed";
    private const string CleanupOnlyMessage = "cleanup failed";

    [Fact]
    public void Throw_PrimaryAndCleanup_SurfacesAnAggregateWithThePrimaryFirst()
    {
        var primary = new InvalidOperationException("primary");
        var firstCleanup = new IOException("cleanup-1");
        var secondCleanup = new TimeoutException("cleanup-2");

        var thrown = Assert.Throws<AggregateException>(
            () => PostgreSqlFailures.Throw(primary, [firstCleanup, secondCleanup], CombinedMessage, CleanupOnlyMessage));

        // AggregateException.Message appends the inner exception messages, so only the prefix is the supplied text.
        Assert.StartsWith(CombinedMessage, thrown.Message, StringComparison.Ordinal);
        Assert.Collection(
            thrown.InnerExceptions,
            exception => Assert.Same(primary, exception),
            exception => Assert.Same(firstCleanup, exception),
            exception => Assert.Same(secondCleanup, exception));
    }

    [Fact]
    public void Throw_PrimaryWithoutCleanup_RethrowsTheOriginalInstanceWithItsStackPreserved()
    {
        var primary = CaptureThrownException();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => PostgreSqlFailures.Throw(primary, [], CombinedMessage, CleanupOnlyMessage));

        Assert.Same(primary, thrown);
        Assert.Contains(nameof(CaptureThrownException), thrown.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void Throw_CleanupOnlySingleFailure_RethrowsThatInstanceInsteadOfAggregating()
    {
        var cleanup = new IOException("cleanup");

        var thrown = Assert.Throws<IOException>(
            () => PostgreSqlFailures.Throw(null, [cleanup], CombinedMessage, CleanupOnlyMessage));

        Assert.Same(cleanup, thrown);
    }

    [Fact]
    public void Throw_CleanupOnlyMultipleFailures_AggregatesThemUnderTheCleanupMessage()
    {
        var first = new IOException("cleanup-1");
        var second = new TimeoutException("cleanup-2");

        var thrown = Assert.Throws<AggregateException>(
            () => PostgreSqlFailures.Throw(null, [first, second], CombinedMessage, CleanupOnlyMessage));

        // AggregateException.Message appends the inner exception messages, so only the prefix is the supplied text.
        Assert.StartsWith(CleanupOnlyMessage, thrown.Message, StringComparison.Ordinal);
        Assert.Collection(
            thrown.InnerExceptions,
            exception => Assert.Same(first, exception),
            exception => Assert.Same(second, exception));
    }

    [Fact]
    public void Throw_NoPrimaryAndNoCleanup_ReturnsWithoutThrowing()
    {
        PostgreSqlFailures.Throw(null, [], CombinedMessage, CleanupOnlyMessage);
    }

    private static Exception CaptureThrownException()
    {
        try
        {
            ExceptionDispatchInfo.Capture(new InvalidOperationException("primary")).Throw();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
