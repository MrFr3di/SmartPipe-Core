using System.Transactions;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>
/// Validates that no PostgreSQL component participates in an ambient <see cref="System.Transactions"/> transaction.
/// </summary>
/// <remarks>
/// Npgsql enlists connections automatically when <see cref="Transaction.Current"/> is not null, which would break the
/// "one batch envelope equals one completed COPY" contract, so both the connection acquisition and the protocol start
/// must reject an ambient transaction.
/// </remarks>
public sealed class PostgreSqlAmbientTransactionTests
{
    [Fact]
    public void Reject_OutsideAScope_DoesNotThrow() => PostgreSqlAmbientTransaction.Reject();

    [Fact]
    public void Reject_InsideAScope_ThrowsTheFrozenMessage()
    {
        using var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled);

        var exception = Assert.Throws<InvalidOperationException>(PostgreSqlAmbientTransaction.Reject);

        Assert.Equal(PostgreSqlErrorMessages.AmbientTransactionRejected, exception.Message);
    }

    [Fact]
    public async Task AmbientTransaction_FlowsAcrossAwaitPoints_WhenAsyncFlowIsEnabled()
    {
        using var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled);

        await Task.Yield();

        // Harness precondition: the rejection tests below await inside the scope, so the ambient transaction
        // must still be observable after an await point.
        Assert.NotNull(Transaction.Current);
    }

    [Fact]
    public async Task BinaryCopySource_InitializeInsideAmbientScope_Rejects()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        await AssertInitializeRejectedInsideAmbientScopeAsync(
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));
    }

    [Fact]
    public async Task BinaryCopyBatchSink_InitializeInsideAmbientScope_Rejects()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        await AssertInitializeRejectedInsideAmbientScopeAsync(
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));
    }

    [Fact]
    public async Task NotificationSource_InitializeInsideAmbientScope_Rejects()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        await AssertInitializeRejectedInsideAmbientScopeAsync(
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions()));
    }

    [Fact]
    public async Task BinaryCopySource_InitializeOutsideAScope_ReachesTheProvider()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        await AssertInitializeReachesTheProviderAsync(
            PostgreSqlPipelineComponents.BinaryCopySource<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyToCommand,
                PostgreSqlUnitTestSupport.RowReader,
                new PostgreSqlBinaryCopySourceOptions()));
    }

    [Fact]
    public async Task BinaryCopyBatchSink_InitializeOutsideAScope_ReachesTheProvider()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        await AssertInitializeReachesTheProviderAsync(
            PostgreSqlPipelineComponents.BinaryCopyBatchSink<int>(
                dataSource,
                PostgreSqlUnitTestSupport.CopyFromCommand,
                PostgreSqlUnitTestSupport.RowWriter,
                new PostgreSqlBinaryCopySinkOptions()));
    }

    [Fact]
    public async Task NotificationSource_InitializeOutsideAScope_ReachesTheProvider()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();

        await AssertInitializeReachesTheProviderAsync(
            PostgreSqlPipelineComponents.NotificationSource(
                dataSource,
                new[] { "orders" },
                new PostgreSqlNotificationSourceOptions()));
    }

    private static Task AssertInitializeRejectedInsideAmbientScopeAsync<T>(
        PipelineComponent<IPipelineSource<T>> descriptor) =>
        AssertInitializeRejectedInsideAmbientScopeCoreAsync(
            descriptor,
            static (source, token) => source.InitializeAsync(token));

    private static Task AssertInitializeRejectedInsideAmbientScopeAsync<T>(
        PipelineComponent<IPipelineSink<T>> descriptor) =>
        AssertInitializeRejectedInsideAmbientScopeCoreAsync(
            descriptor,
            static (sink, token) => sink.InitializeAsync(token));

    private static Task AssertInitializeReachesTheProviderAsync<T>(
        PipelineComponent<IPipelineSource<T>> descriptor) =>
        AssertInitializeReachesTheProviderCoreAsync(
            descriptor,
            static (source, token) => source.InitializeAsync(token));

    private static Task AssertInitializeReachesTheProviderAsync<T>(
        PipelineComponent<IPipelineSink<T>> descriptor) =>
        AssertInitializeReachesTheProviderCoreAsync(
            descriptor,
            static (sink, token) => sink.InitializeAsync(token));

    /// <summary>Asserts that initialization inside an ambient scope fails with the provider's own rejection.</summary>
    private static async Task AssertInitializeRejectedInsideAmbientScopeCoreAsync<TComponent>(
        PipelineComponent<TComponent> descriptor,
        Func<TComponent, CancellationToken, ValueTask> initialize)
        where TComponent : class, IAsyncDisposable
    {
        var component = PipelineComponentProbe.Activate(descriptor);
        try
        {
            using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await initialize(component, PostgreSqlUnitTestSupport.TestCancellation));

                Assert.Equal(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message);
            }
        }
        finally
        {
            await component.DisposeAsync();
        }
    }

    /// <summary>
    /// Reachability control: without an ambient transaction the very same initialization call reaches the provider.
    /// Because a real open attempt against this endpoint always fails with <see cref="NpgsqlException"/>, the
    /// rejection observed by the tests above can only mean that no provider call was attempted.
    /// </summary>
    private static async Task AssertInitializeReachesTheProviderCoreAsync<TComponent>(
        PipelineComponent<TComponent> descriptor,
        Func<TComponent, CancellationToken, ValueTask> initialize)
        where TComponent : class, IAsyncDisposable
    {
        var component = PipelineComponentProbe.Activate(descriptor);
        try
        {
            var failure = await Record.ExceptionAsync(
                async () => await initialize(component, PostgreSqlUnitTestSupport.TestCancellation));

            Assert.NotNull(failure);
            Assert.IsAssignableFrom<NpgsqlException>(failure);
            Assert.NotEqual(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message);
        }
        finally
        {
            await component.DisposeAsync();
        }
    }
}
