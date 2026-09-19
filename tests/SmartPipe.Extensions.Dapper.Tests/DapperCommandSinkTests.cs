using System.Data;
using System.Data.Common;
using Dapper;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper.Internal;

namespace SmartPipe.Extensions.Dapper.Tests;

public sealed class DapperCommandSinkTests
{
    [Fact]
    public async Task CommandSink_ExecutesExactlyOneCommandPerEnvelope()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperSinkOptions { OperationName = "insert" });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        foreach (var name in new[] { "Alice", "Bob", "Carol" })
        {
            await sink.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = 1, Name = name }),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, connection.Commands.Count);
        Assert.All(connection.Commands, command => Assert.Equal(1, command.ExecuteNonQueryCount));
        Assert.Equal(3, journal.CountOf("create-command"));
        Assert.Equal(3, journal.CountOf("execute-nonquery"));
        Assert.Equal(3, journal.CountOf("command-dispose"));
        Assert.Equal(0, journal.CountOf("begin-transaction"));
        Assert.Equal(0, connection.DisposeCount);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(3, journal.CountOf("execute-nonquery"));
    }

    [Fact]
    public async Task CommandSink_InvokesTheParameterFactoryOncePerWrite()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        var factoryCalls = 0;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            factoryCalls++;
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var parameterFactoryCalls = 0;
        var descriptor = DapperPipelineComponents.CommandSink<TestPerson>(
            Factory,
            "update People set Name = @Name where Id = @Id",
            new DapperSinkOptions { OperationName = "update" },
            parameterFactory: envelope =>
            {
                parameterFactoryCalls++;
                return new Dictionary<string, object?> { ["@Id"] = envelope.Payload.Id };
            });

        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 7 }),
            TestContext.Current.CancellationToken);
        await sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 8 }),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, parameterFactoryCalls);
        Assert.Equal(2, connection.Commands.Count);
        Assert.Equal(7, Assert.Single(connection.Commands[0].ParameterValues));
        Assert.Equal(8, Assert.Single(connection.Commands[1].ParameterValues));
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CommandSink_PassesTheEnvelopePayloadAsTheParameterObjectWhenNoFactoryIsGiven()
    {
        var (_, connections, descriptor) = CreateSink(new DapperSinkOptions { OperationName = "insert" });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 7, Name = "Alice" }),
            TestContext.Current.CancellationToken);

        var command = connection.SingleCommand;
        Assert.Equal(2, command.ParameterValues.Count);
        Assert.Contains(7, command.ParameterValues);
        Assert.Contains("Alice", command.ParameterValues);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task CommandSink_ForwardsCommandTextTimeoutTypeAndCacheFlags()
    {
        var (_, connections, descriptor) = CreateSink(new DapperSinkOptions
        {
            OperationName = "stored-procedure",
            CommandTimeoutSeconds = 45,
            CommandType = CommandType.StoredProcedure,
            CacheMode = DapperCommandCacheMode.NoCache,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 7 }),
            TestContext.Current.CancellationToken);

        var command = connection.SingleCommand;
        Assert.Equal("update People set Name = @Name where Id = @Id", command.CommandText);
        Assert.Equal(45, command.CommandTimeout);
        Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        await sink.DisposeAsync();

        Assert.Equal(
            CommandFlags.None,
            DapperSinkOptionsSnapshot.Create(new DapperSinkOptions { OperationName = "cached" }).Flags);
        Assert.Equal(
            CommandFlags.NoCache,
            DapperSinkOptionsSnapshot.Create(new DapperSinkOptions
            {
                OperationName = "uncached",
                CacheMode = DapperCommandCacheMode.NoCache,
            }).Flags);
        Assert.Equal(
            CommandFlags.NoCache,
            DapperQueryOptionsSnapshot.Create(new DapperQueryOptions
            {
                CacheMode = DapperCommandCacheMode.NoCache,
            }).Flags);
        Assert.Equal(
            CommandFlags.NoCache,
            DapperBatchSinkOptionsSnapshot.Create(new DapperBatchSinkOptions
            {
                OperationName = "batch",
                TransactionMode = DapperBatchTransactionMode.None,
                CacheMode = DapperCommandCacheMode.NoCache,
            }).Flags);
    }

    [Fact]
    public async Task CommandSink_OneRunAcquiresOneConnectionAcrossWritesAndDisposesItOnce()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperSinkOptions { OperationName = "insert" });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        for (var index = 0; index < 4; index++)
        {
            await sink.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = index }),
                TestContext.Current.CancellationToken);
        }

        var connection = Assert.Single(connections);
        Assert.Equal(1, connection.OpenCount);
        Assert.Equal(1, journal.CountOf("connection-open"));
        Assert.Equal(4, connection.Commands.Count);
        Assert.Equal(0, connection.DisposeCount);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(1, journal.CountOf("connection-dispose"));
    }

    [Fact]
    public async Task CommandSink_ConcurrentRunsReceiveDistinctConnections()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperSinkOptions { OperationName = "insert" });
        var first = await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-1"));
        var second = await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-2"));

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken).AsTask(),
            second.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        await Task.WhenAll(
            first.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = 1 }),
                TestContext.Current.CancellationToken).AsTask(),
            second.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = 2 }),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, connections.Count);
        Assert.NotSame(connections[0], connections[1]);
        Assert.All(connections, connection => Assert.Equal(1, connection.OpenCount));
        Assert.All(connections, connection => Assert.Single(connection.Commands));
        Assert.Equal(2, journal.CountOf("connection-open"));

        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.All(connections, connection => Assert.Equal(1, connection.DisposeCount));
        Assert.Equal(2, journal.CountOf("connection-dispose"));
    }

    [Fact]
    public async Task CommandSink_DisposeAsyncExecutesNoSqlAndRejectsLaterWrites()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperSinkOptions { OperationName = "insert" });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 1 }),
            TestContext.Current.CancellationToken).AsTask());

        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 1 }),
            TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        await sink.DisposeAsync();
        await sink.DisposeAsync();

        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(1, journal.CountOf("execute-nonquery"));
        var entries = journal.Snapshot();
        var disposeIndex = entries.ToList().IndexOf("connection-dispose");
        Assert.True(disposeIndex >= 0);
        Assert.DoesNotContain("execute-nonquery", entries.Skip(disposeIndex + 1));
        Assert.Equal(1, journal.CountOf("connection-dispose"));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 2 }),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, journal.CountOf("execute-nonquery"));
    }

    [Fact]
    public async Task CommandSink_ExecuteFailurePropagatesTheOriginalFailure()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperSinkOptions { OperationName = "insert" });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        var executeFailure = new RecordingTestFailure("execute");
        connection.NextExecuteNonQueryFailure = executeFailure;

        var exception = await Assert.ThrowsAsync<RecordingTestFailure>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 2 }),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Same(executeFailure, exception);
        Assert.Equal(1, journal.CountOf("execute-nonquery"));
        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task CommandSink_LogsOnlyIdentityOutcomeAffectedRowsAndDuration()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        var loggerFactory = new RecordingLoggerFactory();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var connection = new RecordingDbConnection(journal) { AffectedRows = 5 };
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var descriptor = DapperPipelineComponents.CommandSink<TestPerson>(
            Factory,
            "update Secret set Token = @Token where Id = @Id",
            new DapperSinkOptions { OperationName = "write-secret" },
            parameterFactory: envelope => new Dictionary<string, object?>
            {
                ["@Id"] = envelope.Payload.Id,
                ["@Token"] = "super-secret",
            },
            loggerFactory: loggerFactory);
        var context = DapperTestActivation.CreateContext("sink-log-pipeline");
        var sink = await DapperTestActivation.ActivateAsync(descriptor, context);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 7 }),
            TestContext.Current.CancellationToken);
        connection.NextExecuteNonQueryFailure = new RecordingTestFailure("execute");
        await Assert.ThrowsAsync<RecordingTestFailure>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(new TestPerson { Id = 8 }),
            TestContext.Current.CancellationToken).AsTask());
        await sink.DisposeAsync();

        Assert.Equal(1, loggerFactory.CreateLoggerCalls);
        Assert.Equal(0, loggerFactory.DisposeCalls);
        Assert.Equal(2, loggerFactory.Messages.Count);
        var success = loggerFactory.Messages[0];
        Assert.Contains("write-secret", success, StringComparison.Ordinal);
        Assert.Contains(context.PipelineKey.Value, success, StringComparison.Ordinal);
        Assert.Contains(context.RunId.ToString(), success, StringComparison.Ordinal);
        Assert.Contains("succeeded", success, StringComparison.Ordinal);
        Assert.Contains("5 affected rows", success, StringComparison.Ordinal);
        Assert.Contains("ms", success, StringComparison.Ordinal);
        Assert.DoesNotContain("update Secret", success, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", success, StringComparison.Ordinal);
        Assert.DoesNotContain("recording", success, StringComparison.Ordinal);

        var failure = loggerFactory.Messages[1];
        Assert.Contains("failed", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("update Secret", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", failure, StringComparison.Ordinal);
        Assert.All(loggerFactory.Exceptions, exception => Assert.Null(exception));
    }

    private static (DapperTestJournal Journal, List<RecordingDbConnection> Connections, PipelineComponent<IPipelineSink<TestPerson>> Descriptor)
        CreateSink(DapperSinkOptions options)
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var descriptor = DapperPipelineComponents.CommandSink<TestPerson>(
            Factory,
            "update People set Name = @Name where Id = @Id",
            options);
        return (journal, connections, descriptor);
    }
}
