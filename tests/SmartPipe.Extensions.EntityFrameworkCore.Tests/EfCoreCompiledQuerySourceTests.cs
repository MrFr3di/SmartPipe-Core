#nullable enable

using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore.Runtime;

namespace SmartPipe.Extensions.EntityFrameworkCore.Tests;

public sealed class EfCoreCompiledQuerySourceTests
{
    [Fact]
    public async Task CompiledPath_StreamsScalarStructResults()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource(
            factory,
            (context, activation, ct) => Counts(1, 2, 3));

        var items = await SourceReader.ReadAllAsync(source);

        items.Should().Equal(1, 2, 3);
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CompiledDelegate_IsInvokedOncePerRunAndOnlyWhenEnumerationStarts()
    {
        var factory = new RecordingContextFactory();
        var invocations = 0;
        await using var source = CreateSource(factory, (context, activation, ct) =>
        {
            invocations++;
            return Counts(1);
        });

        await source.InitializeAsync();
        invocations.Should().Be(0);
        factory.CallCount.Should().Be(1);

        await SourceReader.ReadAllAsync(source);
        invocations.Should().Be(1);
    }

    [Fact]
    public async Task CompiledPath_RejectsANullSequenceAndStillDisposesTheContext()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource<int>(factory, (context, activation, ct) => null!);

        var act = async () => await SourceReader.ReadAllAsync(source);

        await act.Should().ThrowAsync<InvalidOperationException>();
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CompiledDelegateFailure_StaysPrimaryAndStillDisposesTheContext()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource<int>(
            factory,
            (context, activation, ct) => throw new InvalidOperationException("compiled delegate failed"));

        var act = async () => await SourceReader.ReadAllAsync(source);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("compiled delegate failed");
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CompiledPath_SupportsExactlyOneEnumerationPerRun()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource(factory, (context, activation, ct) => Counts(1));

        await SourceReader.ReadAllAsync(source);
        var act = async () => await SourceReader.ReadAllAsync(source);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompiledPath_EarlyBreakReleasesTheContextExactlyOnce()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource(factory, (context, activation, ct) => Counts(1, 2, 3));

        await foreach (var envelope in source.ReadEnvelopesAsync())
        {
            envelope.Payload.Should().Be(1);
            break;
        }

        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CompiledPath_FailureInsideTheSequenceKeepsThePrimaryFailureFirst()
    {
        var factory = new RecordingContextFactory(
            () => new TestDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestDbContext>().Options, failOnDispose: true));
        await using var source = CreateSource(factory, (context, activation, ct) => Failing());

        var act = async () => await SourceReader.ReadAllAsync(source);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions[0].Message.Should().Be("sequence failed");
        exception.Which.InnerExceptions[1].Message.Should().Be("The context double failed to dispose.");
    }

    [Fact]
    public async Task CompiledOptions_AcceptTheDefaultOperationNameAndRejectAnInvalidOne()
    {
        var factory = new RecordingContextFactory();
        var valid = () => EfCorePipelineComponents.CompiledQuerySource<TestDbContext, int>(
            factory.CreateAsync,
            (context, activation, ct) => Counts(1),
            new EfCoreCompiledQueryOptions());
        var invalid = () => EfCorePipelineComponents.CompiledQuerySource<TestDbContext, int>(
            factory.CreateAsync,
            (context, activation, ct) => Counts(1),
            new EfCoreCompiledQueryOptions { OperationName = "bad\u0002name" });

        valid.Should().NotThrow();
        invalid.Should().Throw<ArgumentException>();
        factory.CallCount.Should().Be(0);
    }

    private static EfCoreCompiledQuerySource<TestDbContext, TResult> CreateSource<TResult>(
        RecordingContextFactory factory,
        Func<TestDbContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery) =>
        new(
            factory.CreateAsync,
            compiledQuery,
            EfCoreOptionsSnapshot.Create(new EfCoreCompiledQueryOptions { OperationName = "compiled-source" }),
            TestActivation.Create(),
            loggerFactory: null,
            activationCancellationToken: default);

    private static async IAsyncEnumerable<int> Counts(params int[] values)
    {
        await Task.CompletedTask;
        foreach (var value in values)
            yield return value;
    }

    private static async IAsyncEnumerable<int> Failing()
    {
        await Task.CompletedTask;
        yield return 1;
        throw new InvalidOperationException("sequence failed");
    }
}
