using System.Transactions;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>
/// Server-free contract tests for the binary COPY batch sink, driven through its connection and importer seams.
/// </summary>
/// <remarks>
/// The seams replace only the provider cursor acquisition, so a test can prove exactly where a rejection happens:
/// a counted seam that was never invoked proves that the guard ran before any server work. The connection the seam
/// returns is a real, never-opened <see cref="NpgsqlConnection"/>, so no server and no socket is involved.
/// </remarks>
public sealed class PostgreSqlBinaryCopyBatchSinkUnitTests
{
    private const int MaxRowsPerBatch = 4;
    private const string CopyStartedMarker = "the COPY was started";

    [Fact]
    public async Task WriteAsync_BeforeInitialize_IsRejectedWithoutOpeningAConnection()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WriteAsync(Envelope(1, 2), PostgreSqlUnitTestSupport.TestCancellation));

        Assert.Equal(PostgreSqlErrorMessages.SinkNotInitialized, failure.Message);
        Assert.Equal(0, probe.Connections);
        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task InitializeAsync_InsideAmbientScope_IsRejectedBeforeOpeningAConnection()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);

        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation));

            Assert.Equal(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message);
        }

        Assert.Equal(0, probe.Connections);
        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task WriteAsync_WithAmbientScopeEnteredAfterInitialization_IsRejectedBeforeTheCopyStarts()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);

        await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);
        Assert.Equal(1, probe.Connections);

        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await sink.WriteAsync(Envelope(1, 2), PostgreSqlUnitTestSupport.TestCancellation));

            Assert.Equal(PostgreSqlErrorMessages.AmbientTransactionRejected, failure.Message);
        }

        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task InitializeAsync_WithAnAlreadyCancelledActivationToken_IsRejectedBeforeAnyCopyWork()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var sink = CreateSink(dataSource, probe, cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation));

        // Whatever the check order relative to connection acquisition, an already cancelled run performs no COPY work
        // and is never left in an initialized state.
        Assert.Equal(0, probe.Imports);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WriteAsync(Envelope(1), PostgreSqlUnitTestSupport.TestCancellation));

        Assert.Equal(PostgreSqlErrorMessages.SinkNotInitialized, failure.Message);
        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task WriteAsync_WithAnAlreadyCancelledCallToken_DoesNotStartTheCopy()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);
        await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sink.WriteAsync(Envelope(1), cancelled.Token));

        Assert.Equal(1, probe.Connections);
        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task WriteAsync_WithAnEmptyBatch_DoesNotStartTheCopy()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);
        await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);

        await sink.WriteAsync(Envelope(), PostgreSqlUnitTestSupport.TestCancellation);

        Assert.Equal(1, probe.Connections);
        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task WriteAsync_WithABatchAboveTheLimit_IsRejectedBeforeTheCopyStarts()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);
        await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WriteAsync(Envelope(1, 2, 3, 4, 5), PostgreSqlUnitTestSupport.TestCancellation));

        Assert.Equal(PostgreSqlErrorMessages.BatchTooLarge, failure.Message);
        Assert.Equal(1, probe.Connections);
        Assert.Equal(0, probe.Imports);
    }

    [Fact]
    public async Task WriteAsync_WithABatchOfExactlyTheLimit_StartsTheCopy()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        await using var sink = CreateSink(dataSource, probe);
        await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WriteAsync(Envelope(1, 2, 3, 4), PostgreSqlUnitTestSupport.TestCancellation));

        // The seam throws as soon as the COPY would begin, so reaching it proves the inclusive boundary.
        Assert.Equal(CopyStartedMarker, failure.Message);
        Assert.Equal(1, probe.Imports);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndNeverStartsTheCopy()
    {
        using var dataSource = PostgreSqlUnitTestSupport.CreateUnreachableDataSource();
        var probe = new CopyProbe();
        var sink = CreateSink(dataSource, probe);

        await sink.InitializeAsync(PostgreSqlUnitTestSupport.TestCancellation);
        await sink.DisposeAsync();
        await sink.DisposeAsync();

        Assert.Equal(1, probe.Connections);
        Assert.Equal(0, probe.Imports);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await sink.WriteAsync(Envelope(1), PostgreSqlUnitTestSupport.TestCancellation));

        Assert.Equal(0, probe.Imports);
    }

    private static PostgreSqlBinaryCopyBatchSink<int> CreateSink(
        NpgsqlDataSource dataSource,
        CopyProbe probe,
        CancellationToken activationToken = default) =>
        new(
            dataSource,
            PostgreSqlUnitTestSupport.CopyFromCommand,
            PostgreSqlUnitTestSupport.RowWriter,
            PostgreSqlBinaryCopySinkOptionsSnapshot.Create(
                new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = MaxRowsPerBatch }),
            null,
            activationToken,
            probe.ConnectionFactory,
            probe.ImporterFactory);

    private static ProcessingEnvelope<IReadOnlyList<int>> Envelope(params int[] values) =>
        ProcessingEnvelope<IReadOnlyList<int>>.Create(values);

    /// <summary>Counts the sink's connection and COPY acquisition attempts without performing any I/O.</summary>
    private sealed class CopyProbe
    {
        private const string CopyStarted = CopyStartedMarker;

        private int _connections;
        private int _imports;

        internal int Connections => Volatile.Read(ref _connections);

        internal int Imports => Volatile.Read(ref _imports);

        internal Func<NpgsqlDataSource, CancellationToken, ValueTask<NpgsqlConnection>> ConnectionFactory =>
            (_, _) =>
            {
                Interlocked.Increment(ref _connections);
                return ValueTask.FromResult(PostgreSqlUnitTestSupport.CreateUnopenedConnection());
            };

        internal Func<NpgsqlConnection, string, CancellationToken, Task<NpgsqlBinaryImporter>> ImporterFactory =>
            (_, _, _) =>
            {
                Interlocked.Increment(ref _imports);
                throw new InvalidOperationException(CopyStarted);
            };
    }
}
