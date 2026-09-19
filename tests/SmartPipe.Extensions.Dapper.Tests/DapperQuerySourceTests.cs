using System.Data;
using System.Data.Common;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Tests;

public sealed class DapperQuerySourceTests
{
    [Fact]
    public async Task QuerySource_StreamsTheFirstResultSetAndReleasesResourcesInReverseOrder()
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

        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions { OperationName = "read-people" },
            rowMapper: reader => reader.GetString(0));
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());

        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.Single(connections);
        connection.Reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"], ["Bob"]);

        var envelopes = await DapperTestActivation.ReadAllAsync(
            source,
            TestContext.Current.CancellationToken);

        Assert.Equal(["Alice", "Bob"], envelopes.Select(envelope => envelope.Payload));
        Assert.Single(connections);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, connection.OpenCount);
        var command = connection.SingleCommand;
        Assert.Equal(1, command.ExecuteReaderCount);
        Assert.Equal(0, command.ExecuteNonQueryCount);
        Assert.Equal("select Name from People", command.CommandText);
        Assert.Equal(30, command.CommandTimeout);
        Assert.Equal(CommandType.Text, command.CommandType);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(
            [
                "connection-open",
                "create-command",
                "command-text",
                "execute-reader",
                "reader-read",
                "reader-read",
                "reader-read",
                "reader-dispose",
                "command-dispose",
                "connection-dispose",
            ],
            journal.Snapshot());
    }

    [Fact]
    public async Task QuerySource_ForwardsThePerReadCancellationTokenToEveryRead()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetString(0));
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        using var cancellation = new CancellationTokenSource();

        await source.InitializeAsync(cancellation.Token);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        var reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"], ["Bob"], ["Carol"]);
        connection.Reader = reader;

        var envelopes = await DapperTestActivation.ReadAllAsync(source, cancellation.Token);

        Assert.Equal(3, envelopes.Count);
        Assert.Equal(4, reader.ReadCount);
        Assert.Equal(4, reader.ReadTokens.Count);
        Assert.All(reader.ReadTokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(cancellation.Token, connection.SingleCommand.LastExecuteReaderToken);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.SingleCommand.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task QuerySource_EarlyBreakReleasesReaderCommandAndConnectionExactlyOnce()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetString(0));
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        var reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"], ["Bob"], ["Carol"]);
        connection.Reader = reader;

        await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal("Alice", envelope.Payload);
            break;
        }

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.SingleCommand.ExecuteReaderCount);
        Assert.Equal(1, connection.SingleCommand.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
        Assert.DoesNotContain("execute-nonquery", journal.Snapshot());
        Assert.Equal(
            [
                "connection-open",
                "create-command",
                "command-text",
                "execute-reader",
                "reader-read",
                "reader-dispose",
                "command-dispose",
                "connection-dispose",
            ],
            journal.Snapshot());
    }

    [Fact]
    public async Task QuerySource_MapperFailureStaysPrimaryAndReleasesEverythingExactlyOnce()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var mapperFailure = new RecordingTestFailure("mapper");
        var mapped = 0;
        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions(),
            rowMapper: reader =>
            {
                mapped++;
                if (mapped == 2)
                    throw mapperFailure;
                return reader.GetString(0);
            });
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        var reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"], ["Bob"], ["Carol"]);
        connection.Reader = reader;

        var exception = await Assert.ThrowsAsync<RecordingTestFailure>(
            () => DapperTestActivation.ReadAllAsync(source, TestContext.Current.CancellationToken));

        Assert.Same(mapperFailure, exception);
        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.SingleCommand.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task QuerySource_AttachesCleanupFailuresAfterThePrimaryMapperFailure()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var mapperFailure = new RecordingTestFailure("mapper");
        var disposeFailure = new RecordingTestFailure("connection-dispose");
        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions(),
            rowMapper: _ => throw mapperFailure);
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        connection.DisposeFailure = disposeFailure;
        connection.Reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"]);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => DapperTestActivation.ReadAllAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Same(mapperFailure, exception.InnerExceptions[0]);
        Assert.Same(disposeFailure, exception.InnerExceptions[1]);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(
            [
                "connection-open",
                "create-command",
                "command-text",
                "execute-reader",
                "reader-read",
                "reader-dispose",
                "command-dispose",
                "connection-dispose",
            ],
            journal.Snapshot());
    }

    [Fact]
    public async Task QuerySource_ReaderFailureReleasesReaderCommandAndConnectionExactlyOnce()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var readFailure = new RecordingTestFailure("read");
        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetString(0));
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        var reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"]) { ReadFailure = readFailure };
        connection.Reader = reader;

        var exception = await Assert.ThrowsAsync<RecordingTestFailure>(
            () => DapperTestActivation.ReadAllAsync(source, TestContext.Current.CancellationToken));

        Assert.Same(readFailure, exception);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.SingleCommand.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task QuerySource_CancellationDuringReadReleasesEverythingExactlyOnce()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetString(0));
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        using var cancellation = new CancellationTokenSource();
        await source.InitializeAsync(cancellation.Token);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        var reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"], ["Bob"]);
        reader.BeforeRead = readCount =>
        {
            if (readCount == 1)
                cancellation.Cancel();
        };
        connection.Reader = reader;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DapperTestActivation.ReadAllAsync(source, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.SingleCommand.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task QuerySource_UsesDapperRuntimeRowMappingWhenNoRowMapperIsProvided()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var descriptor = DapperPipelineComponents.QuerySource<TestPerson>(
            Factory,
            "select Id, Name from People",
            new DapperQueryOptions());
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        connection.Reader = new RecordingDbDataReader(
            journal,
            ["Id", "Name"],
            [7, "Alice"],
            [8, "Bob"]);

        var envelopes = await DapperTestActivation.ReadAllAsync(
            source,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, envelopes.Count);
        Assert.Equal(7, envelopes[0].Payload.Id);
        Assert.Equal("Alice", envelopes[0].Payload.Name);
        Assert.Equal(8, envelopes[1].Payload.Id);
        Assert.Equal("Bob", envelopes[1].Payload.Name);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task QuerySource_InvokesTheParametersFactoryOncePerRun()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var factoryCalls = 0;
        var parameters = new Dictionary<string, object?> { ["@Id"] = 7 };
        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Name from People where Id = @Id",
            new DapperQueryOptions(),
            parametersFactory: _ =>
            {
                factoryCalls++;
                return parameters;
            },
            rowMapper: reader => reader.GetString(0));
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());

        await source.InitializeAsync(TestContext.Current.CancellationToken);
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, factoryCalls);

        var connection = Assert.IsType<RecordingDbConnection>(created);
        connection.Reader = new RecordingDbDataReader(journal, ["Name"], ["Alice"]);
        var envelopes = await DapperTestActivation.ReadAllAsync(
            source,
            TestContext.Current.CancellationToken);

        Assert.Single(envelopes);
        Assert.Equal(1, factoryCalls);
        var command = connection.SingleCommand;
        var value = Assert.Single(command.ParameterValues);
        Assert.Equal(7, value);
    }

    [Fact]
    public async Task QuerySource_EachRunAcquiresOneFreshConnectionAndDisposesItOnce()
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

        var descriptor = DapperPipelineComponents.QuerySource<int>(
            Factory,
            "select 1",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetInt32(0));
        var dataSource = new RecordingDbDataSource(() =>
        {
            var connection = new RecordingDbConnection(journal);
            connections.Add(connection);
            return connection;
        });
        var dataSourceDescriptor = DapperPipelineComponents.QuerySource<int>(
            dataSource,
            "select 1",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetInt32(0));

        var sources = new List<IPipelineSource<int>>
        {
            await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-1")),
            await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-2")),
            await DapperTestActivation.ActivateAsync(dataSourceDescriptor, DapperTestActivation.CreateContext("run-3")),
        };

        foreach (var source in sources)
            await source.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, dataSource.CreateConnectionCount);
        Assert.Equal(3, connections.Count);
        Assert.Equal(3, connections.Distinct().Count());
        Assert.All(connections, connection => Assert.Equal(1, connection.OpenCount));

        foreach (var source in sources)
        {
            await source.DisposeAsync();
            await source.DisposeAsync();
        }

        Assert.All(connections, connection => Assert.Equal(1, connection.DisposeCount));
        Assert.DoesNotContain(journal.Snapshot(), entry => entry.StartsWith("execute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuerySource_ConcurrentRunsReceiveDistinctConnections()
    {
        var journal = new DapperTestJournal();
        var connections = new List<RecordingDbConnection>();
        var firstFactoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFactoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        async ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            var index = Interlocked.Increment(ref factoryCalls);
            if (index == 1)
            {
                firstFactoryEntered.TrySetResult();
                await secondFactoryEntered.Task;
            }
            else
            {
                secondFactoryEntered.TrySetResult();
            }

            var connection = new RecordingDbConnection(journal);
            lock (connections)
                connections.Add(connection);
            return connection;
        }

        var descriptor = DapperPipelineComponents.QuerySource<int>(
            Factory,
            "select 1",
            new DapperQueryOptions(),
            rowMapper: reader => reader.GetInt32(0));
        var first = await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-1"));
        var second = await DapperTestActivation.ActivateAsync(descriptor, DapperTestActivation.CreateContext("run-2"));

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken).AsTask(),
            second.InitializeAsync(TestContext.Current.CancellationToken).AsTask());

        await firstFactoryEntered.Task;
        await secondFactoryEntered.Task;
        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, connections.Count);
        Assert.NotSame(connections[0], connections[1]);

        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.All(connections, connection => Assert.Equal(1, connection.DisposeCount));
    }

    [Fact]
    public async Task QuerySource_DisposeAsyncReleasesTheConnectionWithoutExecutingSql()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var descriptor = DapperPipelineComponents.QuerySource<int>(
            Factory,
            "select 1",
            new DapperQueryOptions());
        var source = await DapperTestActivation.ActivateAsync(
            descriptor,
            DapperTestActivation.CreateContext());
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.Empty(connection.Commands);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(["connection-open", "connection-dispose"], journal.Snapshot());
    }

    [Fact]
    public async Task QuerySource_LogsOnlyIdentityOutcomeDurationAndRowCount()
    {
        var journal = new DapperTestJournal();
        RecordingDbConnection? created = null;
        ValueTask<DbConnection> Factory(PipelineActivationContext _, CancellationToken __)
        {
            created = new RecordingDbConnection(journal);
            return ValueTask.FromResult<DbConnection>(created);
        }

        var loggerFactory = new RecordingLoggerFactory();
        var descriptor = DapperPipelineComponents.QuerySource<string>(
            Factory,
            "select Secret from Vault where Token = @Token",
            new DapperQueryOptions { OperationName = "read-vault" },
            parametersFactory: _ => new Dictionary<string, object?> { ["@Token"] = "super-secret" },
            rowMapper: reader => reader.GetString(0),
            loggerFactory: loggerFactory);
        var context = DapperTestActivation.CreateContext("query-log-pipeline");
        var source = await DapperTestActivation.ActivateAsync(descriptor, context);
        await source.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = Assert.IsType<RecordingDbConnection>(created);
        connection.Reader = new RecordingDbDataReader(journal, ["Secret"], ["hidden-value"]);

        var envelopes = await DapperTestActivation.ReadAllAsync(
            source,
            TestContext.Current.CancellationToken);
        await source.DisposeAsync();

        Assert.Single(envelopes);
        Assert.Equal(1, loggerFactory.CreateLoggerCalls);
        Assert.Equal(0, loggerFactory.DisposeCalls);
        var message = Assert.Single(loggerFactory.Messages);
        Assert.Contains("read-vault", message, StringComparison.Ordinal);
        Assert.Contains(context.PipelineKey.Value, message, StringComparison.Ordinal);
        Assert.Contains(context.RunId.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("succeeded", message, StringComparison.Ordinal);
        Assert.Contains("1 rows", message, StringComparison.Ordinal);
        Assert.Contains("ms", message, StringComparison.Ordinal);
        Assert.DoesNotContain("select Secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("recording", message, StringComparison.Ordinal);
        Assert.All(loggerFactory.Exceptions, exception => Assert.Null(exception));
    }
}
