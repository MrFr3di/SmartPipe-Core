using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Validates descriptor ownership, per-run activation, build purity and borrowed data source lifetime.</summary>
public sealed class PostgreSqlDescriptorContractTests
{
    [Fact]
    public void EveryFactory_ReturnsARuntimeOwnedInitializedDescriptor()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var source = PostgreSqlPipelineComponents.BinaryCopySource<int>(
            dataSource,
            PostgreSqlUnitTestSupport.CopyToCommand,
            PostgreSqlUnitTestSupport.RowReader,
            new PostgreSqlBinaryCopySourceOptions());
        var sink = PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
            dataSource,
            PostgreSqlUnitTestSupport.CopyFromCommand,
            PostgreSqlUnitTestSupport.RowWriter,
            new PostgreSqlBinaryCopySinkOptions());
        var notifications = PostgreSqlPipelineComponents.NotificationSource(
            dataSource,
            new[] { "orders" },
            new PostgreSqlNotificationSourceOptions());

        AssertRuntimeOwned(source);
        AssertRuntimeOwned(sink);
        AssertRuntimeOwned(notifications);
    }

    [Fact]
    public async Task ActivatingTheSameDescriptorTwice_YieldsTwoDistinctInstances()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        using var firstActivation = new CancellationTokenSource();
        using var secondActivation = new CancellationTokenSource();

        await AssertDistinctInstancesAsync(
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()),
            firstActivation.Token,
            secondActivation.Token);

        await AssertDistinctInstancesAsync(
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()),
            firstActivation.Token,
            secondActivation.Token);

        await AssertDistinctInstancesAsync(
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions()),
            firstActivation.Token,
            secondActivation.Token);
    }

    [Fact]
    public async Task DisposedDataSource_TrapFailsEveryOpenAttempt()
    {
        var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        await dataSource.DisposeAsync();

        // Calibration for the build-purity trap below: once disposed, every open attempt against this data source
        // fails immediately, so a connection opened while a descriptor or a definition is created cannot pass
        // unnoticed.
        var failure = await Record.ExceptionAsync(
            async () => await dataSource.OpenConnectionAsync(PostgreSqlUnitTestSupport.TestCancellation));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task BuildingDefinitionsFromEveryEntryPoint_PerformsNoConnectionIo()
    {
        // Armed trap: a disposed data source rejects every open attempt at once, so if creating the descriptors or
        // calling Build() opened a connection, this test would fail here instead of passing vacuously. The public
        // factories expose no seam and NpgsqlDataSource cannot be subclassed (every abstract member it declares is
        // internal and NpgsqlConnection is sealed), so an armed trap plus the calibration above is the observable
        // form of "zero connection I/O" that exists without a server.
        //
        // Recorded boundary: a synchronous or awaited open inside Build() cannot escape this test, because the trap
        // fails it immediately. A detached fire-and-forget open that swallows its own failure is the only escape, and
        // no in-process instrument could see it either: an unobserved task cannot be awaited by the caller, and it
        // must swallow its own exception to stay detached. Build purity forbids starting such a task at all.
        var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        await dataSource.DisposeAsync();

        var batchDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<IReadOnlyList<int>>(
                new PipelineKey("postgresql-purity-batch"),
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                static (_, _, _) => ValueTask.FromResult<IReadOnlyList<int>>(Array.Empty<int>()),
                new PostgreSqlBinaryCopySourceOptions())
            .ToPostgreSqlBinaryCopy(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions());

        var stagedDefinition = PostgreSqlUnitTestBuilders
            .CreateStagedBatchBuilder(dataSource, "postgresql-purity-staged")
            .ToPostgreSqlBinaryCopy(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions());

        var notificationDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromNotifications(
                new PipelineKey("postgresql-purity-listen"),
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions())
            .Build();

        Assert.Equal("postgresql-purity-batch", batchDefinition.Key.Value);
        Assert.True(batchDefinition.HasSink);
        Assert.Empty(batchDefinition.Stages);

        Assert.Equal("postgresql-purity-staged", stagedDefinition.Key.Value);
        Assert.True(stagedDefinition.HasSink);
        Assert.Single(stagedDefinition.Stages);

        Assert.Equal("postgresql-purity-listen", notificationDefinition.Key.Value);
        Assert.False(notificationDefinition.HasSink);

        // The trap is still armed: reaching this line required creating all three definitions, and every open
        // attempt against this data source fails, so nothing opened a connection while building.
        var failure = await Record.ExceptionAsync(
            async () => await dataSource.OpenConnectionAsync(PostgreSqlUnitTestSupport.TestCancellation));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task DisposingActivatedComponents_LeavesTheBorrowedDataSourceUsable()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        var batchDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromBinaryCopy<IReadOnlyList<int>>(
                new PipelineKey("postgresql-lifetime-batch"),
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                static (_, _, _) => ValueTask.FromResult<IReadOnlyList<int>>(Array.Empty<int>()),
                new PostgreSqlBinaryCopySourceOptions())
            .ToPostgreSqlBinaryCopy(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions());

        var notificationDefinition = PostgreSqlPipelineDefinitionBuilder
            .FromNotifications(
                new PipelineKey("postgresql-lifetime-listen"),
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions())
            .Build();

        var source = PipelineComponentProbe.Activate(
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));
        var sink = PipelineComponentProbe.Activate(
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));
        var notifications = PipelineComponentProbe.Activate(
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions()));

        await source.DisposeAsync();
        await sink.DisposeAsync();
        await notifications.DisposeAsync();

        Assert.True(batchDefinition.HasSink);
        Assert.False(notificationDefinition.HasSink);

        // The data source is still usable: the failure is the provider's refused connection, never disposal.
        await PostgreSqlUnitTestSupport.AssertProviderFailureAsync(dataSource);
    }

    private static void AssertRuntimeOwned<TComponent>(PipelineComponent<TComponent> descriptor)
        where TComponent : class
    {
        Assert.Equal(PipelineComponentOwnership.RuntimeOwned, descriptor.Ownership);
        Assert.True(descriptor.Initialize);
    }

    private static async Task AssertDistinctInstancesAsync<TComponent>(
        PipelineComponent<TComponent> descriptor,
        CancellationToken firstActivation,
        CancellationToken secondActivation)
        where TComponent : class, IAsyncDisposable
    {
        var first = PipelineComponentProbe.Activate(descriptor, firstActivation);
        var second = PipelineComponentProbe.Activate(descriptor, secondActivation);

        try
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.Equal(first.GetType(), second.GetType());
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }
}
