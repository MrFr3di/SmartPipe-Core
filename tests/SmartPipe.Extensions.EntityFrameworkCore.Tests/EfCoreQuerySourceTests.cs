#nullable enable

using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore.Runtime;

namespace SmartPipe.Extensions.EntityFrameworkCore.Tests;

public sealed class EfCoreQuerySourceTests
{
    [Fact]
    public async Task InitializeAsync_CreatesOneContextAndInvokesTheQueryFactoryExactlyOnce()
    {
        var factory = new RecordingContextFactory();
        var queryFactoryCalls = 0;
        await using var source = CreateSource(factory, (context, activation) =>
        {
            queryFactoryCalls++;
            return new RecordingQueryable<TestRow>([], "initialize");
        });

        await source.InitializeAsync();
        await source.InitializeAsync();

        factory.CallCount.Should().Be(1);
        queryFactoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task Run_StreamsEveryRowAndReleasesEnumeratorThenContextExactlyOnce()
    {
        var factory = new RecordingContextFactory();
        var queryable = new RecordingQueryable<TestRow>(
            [new TestRow { Id = 1, Name = "Ada" }, new TestRow { Id = 2, Name = "Grace" }],
            "stream");
        await using var source = CreateSource(factory, (context, activation) => queryable);

        var items = await SourceReader.ReadAllAsync(source);

        items.Select(item => item.Name).Should().Equal("Ada", "Grace");
        queryable.EnumeratorCount.Should().Be(1);
        queryable.DisposeCount.Should().Be(1);
        factory.Contexts.Should().ContainSingle();
        factory.Contexts[0].DisposeCount.Should().Be(1);
        factory.Contexts[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task EarlyBreak_ReleasesEnumeratorAndContextExactlyOnce()
    {
        var factory = new RecordingContextFactory();
        var queryable = new RecordingQueryable<TestRow>(
            [new TestRow { Id = 1, Name = "Ada" }, new TestRow { Id = 2, Name = "Grace" }],
            "early-break");
        await using var source = CreateSource(factory, (context, activation) => queryable);

        await foreach (var envelope in source.ReadEnvelopesAsync())
        {
            envelope.Payload.Id.Should().Be(1);
            break;
        }

        queryable.DisposeCount.Should().Be(1);
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task SecondEnumeration_FailsDeterministically()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource(
            factory,
            (context, activation) => new RecordingQueryable<TestRow>([], "single-enumeration"));

        await SourceReader.ReadAllAsync(source);
        var act = async () => await SourceReader.ReadAllAsync(source);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UseAfterDisposal_FailsBeforeCreatingAnyContext()
    {
        var factory = new RecordingContextFactory();
        var source = CreateSource(
            factory,
            (context, activation) => new RecordingQueryable<TestRow>([], "after-disposal"));

        await source.DisposeAsync();
        var act = async () => await SourceReader.ReadAllAsync(source);

        await act.Should().ThrowAsync<ObjectDisposedException>();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CancellationBeforeActivation_CreatesNoContext()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource(
            factory,
            (context, activation) => new RecordingQueryable<TestRow>([], "cancel-before"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await source.InitializeAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CancellationDuringEnumeration_SurfacesAndReleasesTheContext()
    {
        var factory = new RecordingContextFactory();
        var queryable = new RecordingQueryable<TestRow>([], "cancel-during")
        {
            MoveNextFailure = new OperationCanceledException(),
        };
        await using var source = CreateSource(factory, (context, activation) => queryable);

        var act = async () => await SourceReader.ReadAllAsync(source);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task QueryFactoryFailure_StaysPrimaryAndStillDisposesTheContext()
    {
        var factory = new RecordingContextFactory();
        await using var source = CreateSource(
            factory,
            (context, activation) => throw new InvalidOperationException("query factory failed"));

        var act = async () => await source.InitializeAsync();

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("query factory failed");
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task NullContextFromTheFactory_FailsWithoutLeakingAResource()
    {
        var factory = new RecordingContextFactory { ReturnNull = true };
        await using var source = CreateSource(
            factory,
            (context, activation) => new RecordingQueryable<TestRow>([], "null-context"));

        var act = async () => await source.InitializeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        factory.Contexts.Should().BeEmpty();
    }

    [Fact]
    public async Task PrimaryFailureIsNeverReplacedByACleanupFailure()
    {
        var factory = new RecordingContextFactory(
            () => new TestDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestDbContext>().Options, failOnDispose: true));
        var queryable = new RecordingQueryable<TestRow>([], "primary-first")
        {
            MoveNextFailure = new InvalidOperationException("enumeration failed"),
        };
        await using var source = CreateSource(factory, (context, activation) => queryable);

        var act = async () => await SourceReader.ReadAllAsync(source);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions.Should().HaveCount(2);
        exception.Which.InnerExceptions[0].Message.Should().Be("enumeration failed");
        exception.Which.InnerExceptions[1].Message.Should().Be("The context double failed to dispose.");
    }

    [Fact]
    public async Task ConcurrentRuns_NeverShareAContext()
    {
        var factory = new RecordingContextFactory();
        var component = EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            (context, activation) => new RecordingQueryable<TestRow>([], "concurrent-runs"),
            new EfCoreQueryOptions { OperationName = "concurrent-runs" });
        var definition = PipelineDefinitionBuilder
            .From(new PipelineKey("sp220-11-concurrent"), component)
            .Build();

        await using var first = await definition.StartAsync();
        await using var second = await definition.StartAsync();
        await first.Completion;
        await second.Completion;

        factory.CallCount.Should().Be(2);
        factory.Contexts.Should().HaveCount(2);
        factory.Contexts[0].Should().NotBeSameAs(factory.Contexts[1]);
        factory.Contexts.Should().OnlyContain(context => context.DisposeCount == 1);
    }

    [Fact]
    public async Task Logs_ContainIdentityAndCountsButNeverQueryTextOrPayload()
    {
        var factory = new RecordingContextFactory();
        var loggerFactory = new RecordingLoggerFactory();
        await using var source = CreateSource(
            factory,
            (context, activation) => new RecordingQueryable<TestRow>(
                [new TestRow { Id = 1, Name = "SecretPayload" }],
                "SELECT SecretTable"),
            new EfCoreQueryOptions { OperationName = "safe-logging" },
            loggerFactory);

        await SourceReader.ReadAllAsync(source);

        loggerFactory.Messages.Should().NotBeEmpty();
        loggerFactory.Messages.Should().Contain(message => message.Contains("safe-logging", StringComparison.Ordinal));
        loggerFactory.Messages.Should().NotContain(message => message.Contains("SELECT", StringComparison.Ordinal));
        loggerFactory.Messages.Should().NotContain(message => message.Contains("SecretPayload", StringComparison.Ordinal));
        loggerFactory.Messages.Should().NotContain(message => message.Contains("SecretTable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndNeverEnumerates()
    {
        var factory = new RecordingContextFactory();
        var queryable = new RecordingQueryable<TestRow>([], "dispose");
        var source = CreateSource(factory, (context, activation) => queryable);
        await source.InitializeAsync();

        await source.DisposeAsync();
        await source.DisposeAsync();

        queryable.EnumeratorCount.Should().Be(0);
        factory.Contexts[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task EnumeratorCleanupFailure_IsReportedAfterThePrimaryFailure()
    {
        var factory = new RecordingContextFactory();
        var queryable = new RecordingQueryable<TestRow>([], "enumerator-cleanup")
        {
            MoveNextFailure = new InvalidOperationException("enumeration failed"),
            DisposeFailure = new InvalidOperationException("enumerator dispose failed"),
        };
        await using var source = CreateSource(factory, (context, activation) => queryable);

        var act = async () => await SourceReader.ReadAllAsync(source);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions.Should().HaveCount(2);
        exception.Which.InnerExceptions[0].Message.Should().Be("enumeration failed");
        exception.Which.InnerExceptions[1].Message.Should().Be("enumerator dispose failed");
    }

    private static EfCoreQuerySource<TestDbContext, TestRow> CreateSource(
        RecordingContextFactory factory,
        Func<TestDbContext, PipelineActivationContext, IQueryable<TestRow>> queryFactory,
        EfCoreQueryOptions? options = null,
        RecordingLoggerFactory? loggerFactory = null,
        CancellationToken activationToken = default) =>
        new(
            factory.CreateAsync,
            queryFactory,
            EfCoreOptionsSnapshot.Create(options ?? new EfCoreQueryOptions { OperationName = "query-source" }),
            TestActivation.Create(),
            loggerFactory,
            activationToken);
}
