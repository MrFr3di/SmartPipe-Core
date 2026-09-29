using System.Data;
using System.Data.Common;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Tests;

public sealed class DapperBatchCommandSinkTests
{
    private static readonly IReadOnlyList<Dictionary<string, object?>> ThreeItems =
    [
        new() { ["@Id"] = 1 },
        new() { ["@Id"] = 2 },
        new() { ["@Id"] = 3 },
    ];

    [Fact]
    public async Task BatchSink_RejectsAnOversizePayloadBeforeAnySql()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
            MaxBatchItems = 2,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("MaxBatchItems", exception.Message, StringComparison.Ordinal);
        Assert.Empty(connection.Commands);
        Assert.Equal(0, journal.CountOf("execute-nonquery"));
        Assert.Equal(0, journal.CountOf("create-command"));
        Assert.Equal(0, journal.CountOf("begin-transaction"));
        Assert.Equal(0, journal.CountOf("commit"));
        Assert.Equal(0, journal.CountOf("rollback"));

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task BatchSink_EmptyPayloadPerformsNoSqlAndNoTransaction()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope<IReadOnlyList<Dictionary<string, object?>>>([]),
            TestContext.Current.CancellationToken);

        Assert.Empty(connection.Commands);
        Assert.Equal(0, journal.CountOf("execute-nonquery"));
        Assert.Equal(0, journal.CountOf("create-command"));
        Assert.Equal(0, journal.CountOf("begin-transaction"));
        Assert.Equal(0, journal.CountOf("commit"));
        Assert.Equal(0, journal.CountOf("rollback"));
        Assert.Equal(1, journal.CountOf("connection-open"));
        Assert.Equal(0, connection.DisposeCount);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(1, journal.CountOf("connection-dispose"));
    }

    [Fact]
    public async Task BatchSink_PerBatchOrdersBeginExecuteCommitAndTransactionDispose()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
            IsolationLevel = IsolationLevel.Serializable,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);

        var transaction = Assert.IsType<RecordingDbTransaction>(connection.LastTransaction);
        Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
        Assert.Equal(1, transaction.CommitCount);
        Assert.Equal(0, transaction.RollbackCount);
        Assert.Equal(1, transaction.DisposeCount);
        Assert.Same(transaction, connection.SingleCommand.AssignedTransaction);
        Assert.Equal(3, connection.SingleCommand.ExecuteNonQueryCount);
        Assert.Equal(1, connection.SingleCommand.AffectedRows);
        Assert.Single(connection.Commands);
        Assert.Equal(0, connection.DisposeCount);

        var entries = journal.Snapshot().ToList();
        Assert.True(entries.IndexOf("begin-transaction") < entries.IndexOf("execute-nonquery"));
        Assert.True(entries.IndexOf("execute-nonquery") < entries.IndexOf("commit"));
        Assert.True(entries.IndexOf("commit") < entries.IndexOf("transaction-dispose"));
        Assert.Equal(1, journal.CountOf("begin-transaction"));
        Assert.Equal(3, journal.CountOf("execute-nonquery"));
        Assert.Equal(1, journal.CountOf("commit"));
        Assert.Equal(1, journal.CountOf("transaction-dispose"));

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
        Assert.True(journal.Snapshot().ToList().IndexOf("transaction-dispose")
            < journal.Snapshot().ToList().IndexOf("connection-dispose"));
    }

    [Fact]
    public async Task BatchSink_WithoutIsolationLevelDelegatesTheChoiceToTheProvider()
    {
        var (_, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);

        var transaction = Assert.IsType<RecordingDbTransaction>(connection.LastTransaction);
        Assert.Equal(IsolationLevel.Unspecified, transaction.IsolationLevel);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task BatchSink_NoneModePerformsNoTransactionCall()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.None,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);

        Assert.Null(connection.LastTransaction);
        Assert.Equal(0, journal.CountOf("begin-transaction"));
        Assert.Equal(0, journal.CountOf("commit"));
        Assert.Equal(0, journal.CountOf("rollback"));
        Assert.Equal(0, journal.CountOf("transaction-dispose"));
        Assert.Equal(3, connection.SingleCommand.ExecuteNonQueryCount);
        Assert.Null(connection.SingleCommand.AssignedTransaction);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task BatchSink_InvokesTheItemParameterFactoryOncePerItem()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var projected = new List<TestPerson>();
        var descriptor = DapperPipelineComponents.BatchCommandSink<TestPerson>(
            Factory,
            "insert into People (Id, Name) values (@Id, @Name)",
            new DapperBatchSinkOptions
            {
                OperationName = "batch",
                TransactionMode = DapperBatchTransactionMode.None,
            },
            itemParameterFactory: item =>
            {
                projected.Add(item);
                return new Dictionary<string, object?> { ["@Id"] = item.Id, ["@Name"] = item.Name };
            });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        IReadOnlyList<TestPerson> payload =
        [
            new TestPerson { Id = 1, Name = "Alice" },
            new TestPerson { Id = 2, Name = "Bob" },
            new TestPerson { Id = 3, Name = "Carol" },
        ];

        await sink.WriteAsync(
            DapperTestActivation.Envelope(payload),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, projected.Count);
        Assert.Equal([1, 2, 3], projected.Select(item => item.Id));
        Assert.Equal(["Alice", "Bob", "Carol"], projected.Select(item => item.Name));
        Assert.Equal(3, connection.SingleCommand.ExecuteNonQueryCount);
        Assert.Equal(3, journal.CountOf("execute-nonquery"));
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task BatchSink_PassesEachItemAsTheParameterObjectWhenNoFactoryIsGiven()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var descriptor = DapperPipelineComponents.BatchCommandSink<TestPerson>(
            Factory,
            "insert into People (Id, Name) values (@Id, @Name)",
            new DapperBatchSinkOptions
            {
                OperationName = "batch",
                TransactionMode = DapperBatchTransactionMode.None,
            });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        IReadOnlyList<TestPerson> payload =
        [
            new TestPerson { Id = 1, Name = "Alice" },
            new TestPerson { Id = 2, Name = "Bob" },
        ];

        await sink.WriteAsync(
            DapperTestActivation.Envelope(payload),
            TestContext.Current.CancellationToken);

        var command = connection.SingleCommand;
        Assert.Equal(2, command.ExecuteNonQueryCount);
        Assert.Equal(2, command.ParameterValues.Count);
        Assert.Contains(2, command.ParameterValues);
        Assert.Contains("Bob", command.ParameterValues);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task BatchSink_OneRunAcquiresOneConnectionAcrossWritesAndDisposesItOnce()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);
        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);
        await sink.WriteAsync(
            DapperTestActivation.Envelope<IReadOnlyList<Dictionary<string, object?>>>([]),
            TestContext.Current.CancellationToken);

        var connection = Assert.Single(connections);
        Assert.Equal(1, connection.OpenCount);
        Assert.Equal(1, journal.CountOf("connection-open"));
        Assert.Equal(2, connection.Commands.Count);
        Assert.Equal(0, connection.DisposeCount);
        Assert.Single(connections);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(1, journal.CountOf("connection-dispose"));
    }

    [Fact]
    public async Task BatchSink_ConcurrentRunsReceiveDistinctConnections()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var connection = new RecordingDbConnection(journal);
            lock (connections)
                connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var descriptor = DapperPipelineComponents.BatchCommandSink<Dictionary<string, object?>>(
            Factory,
            "insert into People (Id) values (@Id)",
            new DapperBatchSinkOptions
            {
                OperationName = "batch",
                TransactionMode = DapperBatchTransactionMode.PerBatch,
            });
        var first = await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-1"));
        var second = await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-2"));

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken).AsTask(),
            second.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        await Task.WhenAll(
            first.WriteAsync(DapperTestActivation.Envelope(ThreeItems), TestContext.Current.CancellationToken).AsTask(),
            second.WriteAsync(DapperTestActivation.Envelope(ThreeItems), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, connections.Count);
        Assert.NotSame(connections[0], connections[1]);
        Assert.All(connections, connection => Assert.Equal(1, connection.OpenCount));
        Assert.All(connections, connection => Assert.Single(connection.Commands));
        Assert.All(connections, connection => Assert.Equal(1, connection.LastTransaction!.CommitCount));
        Assert.Equal(2, journal.CountOf("connection-open"));
        Assert.Equal(0, journal.CountOf("connection-dispose"));

        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.All(connections, connection => Assert.Equal(1, connection.DisposeCount));
        Assert.Equal(2, journal.CountOf("connection-dispose"));
    }

    [Fact]
    public async Task BatchSink_RollsBackOnExecuteFailureKeepingThePrimaryFailureFirst()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        var executeFailure = new RecordingTestFailure("execute");
        connection.NextExecuteNonQueryFailure = executeFailure;

        var exception = await Assert.ThrowsAsync<RecordingTestFailure>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Same(executeFailure, exception);
        var transaction = Assert.IsType<RecordingDbTransaction>(connection.LastTransaction);
        Assert.Equal(0, transaction.CommitCount);
        Assert.Equal(1, transaction.RollbackCount);
        Assert.Equal(CancellationToken.None, transaction.LastRollbackToken);
        Assert.Equal(1, transaction.DisposeCount);
        Assert.Equal(1, journal.CountOf("execute-nonquery"));
        Assert.Equal(1, journal.CountOf("rollback"));
        Assert.Equal(0, journal.CountOf("commit"));
        var entries = journal.Snapshot().ToList();
        Assert.True(entries.IndexOf("rollback") < entries.IndexOf("transaction-dispose"));
        Assert.Equal(0, connection.DisposeCount);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task BatchSink_RollsBackOnCommitFailureWithoutRetryingTheCommit()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        var commitFailure = new RecordingTestFailure("commit");
        connection.NextCommitFailure = commitFailure;

        var exception = await Assert.ThrowsAsync<RecordingTestFailure>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Same(commitFailure, exception);
        var transaction = Assert.IsType<RecordingDbTransaction>(connection.LastTransaction);
        Assert.Equal(1, transaction.CommitCount);
        Assert.Equal(1, transaction.RollbackCount);
        Assert.Equal(1, transaction.DisposeCount);
        Assert.Equal(3, connection.SingleCommand.ExecuteNonQueryCount);
        Assert.Equal(1, journal.CountOf("commit"));
        Assert.Equal(1, journal.CountOf("rollback"));

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task BatchSink_AttachesRollbackAndDisposeFailuresAfterThePrimaryFailure()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        var executeFailure = new RecordingTestFailure("execute");
        var rollbackFailure = new RecordingTestFailure("rollback");
        var disposeFailure = new RecordingTestFailure("transaction-dispose");
        connection.NextExecuteNonQueryFailure = executeFailure;
        connection.NextRollbackFailure = rollbackFailure;
        connection.NextTransactionDisposeFailure = disposeFailure;

        var exception = await Assert.ThrowsAsync<AggregateException>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, exception.InnerExceptions.Count);
        Assert.Same(executeFailure, exception.InnerExceptions[0]);
        Assert.Same(rollbackFailure, exception.InnerExceptions[1]);
        Assert.Same(disposeFailure, exception.InnerExceptions[2]);
        Assert.Equal(1, journal.CountOf("rollback"));
        Assert.Equal(1, journal.CountOf("transaction-dispose"));
        Assert.Equal(0, connection.DisposeCount);

        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task BatchSink_ReportsATransactionDisposeFailureAfterASuccessfulCommit()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        var disposeFailure = new RecordingTestFailure("transaction-dispose");
        connection.NextTransactionDisposeFailure = disposeFailure;

        var exception = await Assert.ThrowsAsync<RecordingTestFailure>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Same(disposeFailure, exception);
        Assert.Equal(1, journal.CountOf("commit"));
        Assert.Equal(0, journal.CountOf("rollback"));
        await sink.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task BatchSink_DisposeAsyncNeverExecutesSql()
    {
        var (journal, connections, descriptor) = CreateSink(new DapperBatchSinkOptions
        {
            OperationName = "batch",
            TransactionMode = DapperBatchTransactionMode.PerBatch,
        });
        var sink = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.WriteAsync(
            DapperTestActivation.Envelope<IReadOnlyList<Dictionary<string, object?>>>([]),
            TestContext.Current.CancellationToken).AsTask());

        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        var executeCount = journal.CountOf("execute-nonquery");
        await sink.DisposeAsync();
        await sink.DisposeAsync();

        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(executeCount, journal.CountOf("execute-nonquery"));
        var entries = journal.Snapshot();
        var disposeIndex = entries.ToList().IndexOf("connection-dispose");
        Assert.True(disposeIndex >= 0);
        Assert.DoesNotContain("execute-nonquery", entries.Skip(disposeIndex + 1));
        Assert.DoesNotContain("begin-transaction", entries.Skip(disposeIndex + 1));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(executeCount, journal.CountOf("execute-nonquery"));
    }

    [Fact]
    public async Task BatchSink_LogsOnlyIdentityOutcomeAffectedRowsAndDuration()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        var loggerFactory = new RecordingLoggerFactory();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            // One affected row per item, so the reported count also proves the sum across the batch.
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var descriptor = DapperPipelineComponents.BatchCommandSink<Dictionary<string, object?>>(
            Factory,
            "insert into Secret (Token) values (@Token)",
            new DapperBatchSinkOptions
            {
                OperationName = "write-batch",
                TransactionMode = DapperBatchTransactionMode.PerBatch,
            },
            loggerFactory: loggerFactory);
        var context = DapperTestActivation.CreateContext("batch-log-pipeline");
        var sink = await DapperTestActivation.ActivateAsync(descriptor, context);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);

        await sink.WriteAsync(
            DapperTestActivation.Envelope(ThreeItems),
            TestContext.Current.CancellationToken);
        await sink.WriteAsync(
            DapperTestActivation.Envelope<IReadOnlyList<Dictionary<string, object?>>>([]),
            TestContext.Current.CancellationToken);
        await sink.DisposeAsync();

        Assert.Equal(1, loggerFactory.CreateLoggerCalls);
        Assert.Equal(0, loggerFactory.DisposeCalls);
        Assert.Equal(2, loggerFactory.Messages.Count);
        var success = loggerFactory.Messages[0];
        Assert.Contains("write-batch", success, StringComparison.Ordinal);
        Assert.Contains(context.PipelineKey.Value, success, StringComparison.Ordinal);
        Assert.Contains(context.RunId.ToString(), success, StringComparison.Ordinal);
        Assert.Contains("succeeded", success, StringComparison.Ordinal);
        Assert.Contains("3 affected rows", success, StringComparison.Ordinal);
        Assert.Contains("ms", success, StringComparison.Ordinal);
        Assert.DoesNotContain("insert into Secret", success, StringComparison.Ordinal);
        Assert.DoesNotContain("@Id", success, StringComparison.Ordinal);

        var empty = loggerFactory.Messages[1];
        Assert.Contains("empty", empty, StringComparison.Ordinal);
        Assert.Contains("0 affected rows", empty, StringComparison.Ordinal);
        Assert.All(loggerFactory.Exceptions, exception => Assert.Null(exception));
        Assert.Equal(3, connection.SingleCommand.ExecuteNonQueryCount);
    }

    private static (DapperTestJournal Journal, List<RecordingDbConnection> Connections, PipelineComponent<IPipelineSink<IReadOnlyList<Dictionary<string, object?>>>> Descriptor)
        CreateSink(DapperBatchSinkOptions options)
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return ValueTask.FromResult<DbConnection>(connection);
        }

        var descriptor = DapperPipelineComponents.BatchCommandSink<Dictionary<string, object?>>(
            Factory,
            "insert into People (Id) values (@Id)",
            options);
        return (journal, connections, descriptor);
    }
}
