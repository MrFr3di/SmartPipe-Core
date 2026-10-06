using System.Transactions;
using Npgsql;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>
/// Server-free contract tests for the binary COPY OUT source, driven through its connection and exporter seams.
/// </summary>
/// <remarks>
/// The exporter seam is where the COPY protocol would start, so a counted seam that was never invoked proves that the
/// rejection happened before any server work. The connection the seam returns is a real, never-opened
/// <see cref="NpgsqlConnection"/>, so no server and no socket is involved, and every ordering assertion is forced by
/// completion-signalled gates rather than by elapsed time.
/// </remarks>
public sealed class PostgreSqlBinaryCopySourceUnitTests
{
    private const string ExportStartedMarker = "the export was started";

    [Fact]
    public async Task InitializeAsync_InsideAmbientScope_IsRejectedBeforeOpeningAConnection()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new ExportProbe();
        await using var source = CreateSource(dataSource, probe);

        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await source.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation));

            Assert.Equal(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message);
        }

        Assert.Equal(0, probe.Connections);
        Assert.Equal(0, probe.Exports);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_InsideAmbientScopeEnteredAfterInitialization_IsRejectedBeforeTheExportStarts()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new ExportProbe();
        await using var source = CreateSource(dataSource, probe);

        // Activation itself performs no COPY work, so it succeeds without a server.
        await source.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);
        Assert.Equal(1, probe.Connections);

        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await ConsumeAsync(source));

            Assert.Equal(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message);
        }

        Assert.Equal(0, probe.Exports);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_WithoutExplicitInitialization_InitializesTheSourceFirst()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new ExportProbe();
        await using var source = CreateSource(dataSource, probe);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsumeAsync(source));

        Assert.Equal(ExportStartedMarker, failure.Message);
        Assert.Equal(1, probe.Connections);
        Assert.Equal(1, probe.Exports);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ASecondEnumeration_IsRejectedAfterTheFirstOneRan()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new ExportProbe();
        await using var source = CreateSource(dataSource, probe);
        await source.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);

        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsumeAsync(source));
        Assert.Equal(ExportStartedMarker, firstFailure.Message);
        Assert.Equal(1, probe.Exports);

        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsumeAsync(source));

        Assert.Equal(PostgreSqlErrorMessages.SourceEnumeratedTwice, secondFailure.Message);
        Assert.Equal(1, probe.Connections);
        Assert.Equal(1, probe.Exports);
    }

    [Fact]
    public async Task ReadEnvelopesAsync_AConcurrentEnumeration_IsRejectedWhileTheFirstOneIsInFlight()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var exportReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exportGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ExportProbe(exportReached, exportGate.Task);
        await using var source = CreateSource(dataSource, probe);
        await source.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);

        var inFlight = ConsumeAsync(source);

        // The first enumeration is parked exactly at the point where the COPY protocol would start.
        await exportReached.Task.WaitAsync(PostgreSqlUnitTestSupport.TestCancellation);

        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsumeAsync(source));
        Assert.Equal(PostgreSqlErrorMessages.SourceEnumeratedTwice, secondFailure.Message);

        // Releasing the parked enumeration proves the first enumeration had really reached the export start.
        exportGate.SetResult();
        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await inFlight);
        Assert.Equal(ExportStartedMarker, firstFailure.Message);

        Assert.Equal(1, probe.Connections);
        Assert.Equal(1, probe.Exports);
    }

    private static PostgreSqlBinaryCopySource<int> CreateSource(NpgsqlDataSource dataSource, ExportProbe probe) =>
        new(
            dataSource,
            PostgreSqlUnitTestSupport.CopyToCommand,
            PostgreSqlUnitTestSupport.RowReader,
            PostgreSqlBinaryCopySourceOptionsSnapshot.Create(new PostgreSqlBinaryCopySourceOptions()),
            null,
            CancellationToken.None,
            probe.ConnectionFactory,
            probe.ExporterFactory);

    private static async Task ConsumeAsync(PostgreSqlBinaryCopySource<int> source)
    {
        await foreach (var _ in source.ReadEnvelopesAsync(PostgreSqlUnitTestSupport.TestCancellation))
        {
        }
    }

    /// <summary>Counts the source's connection and COPY export acquisition attempts without performing any I/O.</summary>
    private sealed class ExportProbe
    {
        private readonly TaskCompletionSource? _exportReached;
        private readonly Task _exportGate;

        private int _connections;
        private int _exports;

        internal ExportProbe(TaskCompletionSource? exportReached = null, Task? exportGate = null)
        {
            _exportReached = exportReached;
            _exportGate = exportGate ?? Task.CompletedTask;
        }

        internal int Connections => Volatile.Read(ref _connections);

        internal int Exports => Volatile.Read(ref _exports);

        internal Func<CancellationToken, ValueTask<NpgsqlConnection>> ConnectionFactory =>
            _ =>
            {
                Interlocked.Increment(ref _connections);
                return ValueTask.FromResult(PostgreSqlUnitTestSupport.CreateUnopenedConnection());
            };

        internal Func<NpgsqlConnection, string, CancellationToken, Task<NpgsqlBinaryExporter>> ExporterFactory =>
            async (_, _, _) =>
            {
                Interlocked.Increment(ref _exports);
                _exportReached?.TrySetResult();
                await _exportGate.ConfigureAwait(false);
                throw new InvalidOperationException(ExportStartedMarker);
            };
    }
}
