using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Tests;

public sealed class DapperSqliteIntegrationTests
{
    [Fact]
    public async Task QuerySource_StreamsRowsFromATempSqliteDatabase()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteAsync(
                "insert into People (Id, Name) values (1, 'Alice'); insert into People (Id, Name) values (2, 'Bob'); insert into People (Id, Name) values (3, 'Carol');");
            var dataSource = new SqliteDbDataSource(database.ConnectionString);

            var runtimeMapping = DapperPipelineComponents.QuerySource<TestPerson>(
                dataSource,
                "select Id, Name from People order by Id",
                new DapperQueryOptions { OperationName = "read-people" });
            var source = await DapperTestActivation.ActivateAsync(
                runtimeMapping,
                DapperTestActivation.CreateContext("sqlite-runtime-mapping"));
            var envelopes = await DapperTestActivation.ReadAllAsync(
                source,
                TestContext.Current.CancellationToken);
            await source.DisposeAsync();

            Assert.Equal(3, envelopes.Count);
            Assert.Equal([1, 2, 3], envelopes.Select(envelope => envelope.Payload.Id));
            Assert.Equal(["Alice", "Bob", "Carol"], envelopes.Select(envelope => envelope.Payload.Name));

            var explicitMapping = DapperPipelineComponents.QuerySource<string>(
                dataSource,
                "select Name from People where Id = @Id",
                new DapperQueryOptions { OperationName = "read-name" },
                parametersFactory: _ => new Dictionary<string, object?> { ["@Id"] = 2 },
                rowMapper: reader => reader.GetString(0));
            var single = await DapperTestActivation.ActivateAsync(
                explicitMapping,
                DapperTestActivation.CreateContext("sqlite-explicit-mapping"));
            var singleEnvelopes = await DapperTestActivation.ReadAllAsync(
                single,
                TestContext.Current.CancellationToken);
            await single.DisposeAsync();

            Assert.Equal("Bob", Assert.Single(singleEnvelopes).Payload);
            Assert.Equal(2, dataSource.CreateConnectionCount);
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task QuerySource_ConsumesOnlyTheFirstResultSet()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteAsync(
                "insert into People (Id, Name) values (1, 'Alice'); insert into People (Id, Name) values (2, 'Bob');");
            var descriptor = DapperPipelineComponents.QuerySource<int>(
                new SqliteDbDataSource(database.ConnectionString),
                "select Id from People order by Id; select 99 as Id",
                new DapperQueryOptions { OperationName = "first-result-set" },
                rowMapper: reader => reader.GetInt32(0));
            var source = await DapperTestActivation.ActivateAsync(
                descriptor,
                DapperTestActivation.CreateContext("sqlite-first-result-set"));

            var envelopes = await DapperTestActivation.ReadAllAsync(
                source,
                TestContext.Current.CancellationToken);
            await source.DisposeAsync();

            Assert.Equal([1, 2], envelopes.Select(envelope => envelope.Payload));
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task CommandSink_InsertsOneRowPerEnvelope()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            var loggerFactory = new RecordingLoggerFactory();
            var descriptor = DapperPipelineComponents.CommandSink<TestPerson>(
                new SqliteDbDataSource(database.ConnectionString),
                "insert into People (Id, Name) values (@Id, @Name)",
                new DapperSinkOptions { OperationName = "insert-person" },
                loggerFactory: loggerFactory);
            var sink = await DapperTestActivation.ActivateAsync(
                descriptor,
                DapperTestActivation.CreateContext("sqlite-command-sink"));
            await sink.InitializeAsync(TestContext.Current.CancellationToken);

            await sink.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = 1, Name = "Alice" }),
                TestContext.Current.CancellationToken);
            await sink.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = 2, Name = "Bob" }),
                TestContext.Current.CancellationToken);
            await sink.DisposeAsync();

            Assert.Equal(2, await database.ScalarAsync("select count(*) from People;"));
            Assert.Equal(2, loggerFactory.Messages.Count);
            Assert.All(
                loggerFactory.Messages,
                message => Assert.Contains("1 affected rows", message, StringComparison.Ordinal));
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task CommandSink_ExecuteFailureLeavesTheDatabaseUnchanged()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            var descriptor = DapperPipelineComponents.CommandSink<TestPerson>(
                new SqliteDbDataSource(database.ConnectionString),
                "insert into People (Id, Name) values (@Id, @Name)",
                new DapperSinkOptions { OperationName = "insert-person" });
            var sink = await DapperTestActivation.ActivateAsync(
                descriptor,
                DapperTestActivation.CreateContext("sqlite-command-failure"));
            await sink.InitializeAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<DbException>(() => sink.WriteAsync(
                DapperTestActivation.Envelope(new TestPerson { Id = 1, Name = null! }),
                TestContext.Current.CancellationToken).AsTask());
            await sink.DisposeAsync();

            Assert.Equal(0, await database.ScalarAsync("select count(*) from People;"));
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task BatchCommandSink_PerBatchInsertsTheWholeBatch()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            var loggerFactory = new RecordingLoggerFactory();
            var descriptor = DapperPipelineComponents.BatchCommandSink<TestPerson>(
                new SqliteDbDataSource(database.ConnectionString),
                "insert into People (Id, Name) values (@Id, @Name)",
                new DapperBatchSinkOptions
                {
                    OperationName = "insert-batch",
                    TransactionMode = DapperBatchTransactionMode.PerBatch,
                    IsolationLevel = IsolationLevel.Serializable,
                },
                itemParameterFactory: item => new Dictionary<string, object?>
                {
                    ["@Id"] = item.Id,
                    ["@Name"] = item.Name,
                },
                loggerFactory: loggerFactory);
            var sink = await DapperTestActivation.ActivateAsync(
                descriptor,
                DapperTestActivation.CreateContext("sqlite-batch-sink"));
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<TestPerson> payload =
            [
                new TestPerson { Id = 1, Name = "Alice" },
                new TestPerson { Id = 2, Name = "Bob" },
                new TestPerson { Id = 3, Name = "Carol" },
            ];

            await sink.WriteAsync(
                DapperTestActivation.Envelope(payload),
                TestContext.Current.CancellationToken);
            await sink.DisposeAsync();

            Assert.Equal(3, await database.ScalarAsync("select count(*) from People;"));
            var message = Assert.Single(loggerFactory.Messages);
            Assert.Contains("3 affected rows", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Alice", message, StringComparison.Ordinal);
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task BatchCommandSink_ExecuteFailureRollsBackTheWholeBatch()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            var descriptor = DapperPipelineComponents.BatchCommandSink<TestPerson>(
                new SqliteDbDataSource(database.ConnectionString),
                "insert into People (Id, Name) values (@Id, @Name)",
                new DapperBatchSinkOptions
                {
                    OperationName = "insert-batch",
                    TransactionMode = DapperBatchTransactionMode.PerBatch,
                },
                itemParameterFactory: item => new Dictionary<string, object?>
                {
                    ["@Id"] = item.Id,
                    ["@Name"] = item.Name,
                });
            var sink = await DapperTestActivation.ActivateAsync(
                descriptor,
                DapperTestActivation.CreateContext("sqlite-batch-rollback"));
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<TestPerson> payload =
            [
                new TestPerson { Id = 1, Name = "Alice" },
                new TestPerson { Id = 2, Name = "Bob" },
                new TestPerson { Id = 2, Name = "Duplicate" },
            ];

            await Assert.ThrowsAnyAsync<DbException>(() => sink.WriteAsync(
                DapperTestActivation.Envelope(payload),
                TestContext.Current.CancellationToken).AsTask());
            await sink.DisposeAsync();

            Assert.Equal(0, await database.ScalarAsync("select count(*) from People;"));
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task BatchCommandSink_WithoutTransactionModeLeavesTheBatchWithoutRollback()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        try
        {
            var descriptor = DapperPipelineComponents.BatchCommandSink<TestPerson>(
                new SqliteDbDataSource(database.ConnectionString),
                "insert into People (Id, Name) values (@Id, @Name)",
                new DapperBatchSinkOptions
                {
                    OperationName = "insert-batch",
                    TransactionMode = DapperBatchTransactionMode.None,
                },
                itemParameterFactory: item => new Dictionary<string, object?>
                {
                    ["@Id"] = item.Id,
                    ["@Name"] = item.Name,
                });
            var sink = await DapperTestActivation.ActivateAsync(
                descriptor,
                DapperTestActivation.CreateContext("sqlite-batch-none"));
            await sink.InitializeAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<TestPerson> payload =
            [
                new TestPerson { Id = 1, Name = "Alice" },
                new TestPerson { Id = 2, Name = "Bob" },
                new TestPerson { Id = 2, Name = "Duplicate" },
            ];

            await Assert.ThrowsAnyAsync<DbException>(() => sink.WriteAsync(
                DapperTestActivation.Envelope(payload),
                TestContext.Current.CancellationToken).AsTask());
            await sink.DisposeAsync();

            Assert.Equal(2, await database.ScalarAsync("select count(*) from People;"));
        }
        finally
        {
            database.Delete();
        }
    }

    private sealed class SqliteTestDatabase
    {
        private SqliteTestDatabase(string connectionString, string path)
        {
            ConnectionString = connectionString;
            Path = path;
        }

        public string ConnectionString { get; }

        public string Path { get; }

        public static async Task<SqliteTestDatabase> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"smartpipe-dapper-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "create table People (Id integer primary key, Name text not null);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return new(connectionString, path);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Delete()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
