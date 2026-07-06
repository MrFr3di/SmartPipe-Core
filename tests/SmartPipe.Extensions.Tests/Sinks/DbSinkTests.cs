#nullable enable
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Tests.Selectors;
using System.ComponentModel.DataAnnotations.Schema;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sinks;

[Trait("Category", "CorrectnessRegression")]
public class DbSinkTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DbSinkTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Create test table
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE TestEntities (
                Id INTEGER PRIMARY KEY,
                Name TEXT
            )";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DbSink<object>(null!));
    }

    [Fact]
    public async Task WriteAsync_InsertsToDatabase_SingleItem()
    {
        var sink = new DbSink<TestEntity>(_connection);
        await sink.InitializeAsync();

        var entity = new TestEntity { Id = 1, Name = "Test" };
        var result = ProcessingEnvelope<TestEntity>.Create(entity);
        await sink.WriteAsync(result);

        // Verify data was inserted
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TestEntities";
        var count = (long)command.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task WriteAsync_DoesNotInsert_WhenPayloadIsNull()
    {
        var sink = new DbSink<TestEntity?>(_connection, "INSERT INTO TestEntities (Id, Name) VALUES (@Id, @Name)");
        await sink.InitializeAsync();

        var result = ProcessingEnvelope<TestEntity?>.Create(null);
        await sink.WriteAsync(result);

        // Verify no data was inserted
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TestEntities";
        var count = (long)command.ExecuteScalar()!;
        Assert.Equal(0, count);
    }

    [Fact]
    public void Constructor_CreatesSink_ForValidConnection()
    {
        var sink = new DbSink<TestEntity>(_connection);
        Assert.NotNull(sink);
    }

    [Fact]
    public async Task DisposeAsync_LegacyIdbConnection_ClosesAlreadyOpenConnection()
    {
        var connection = new TrackingExecuteDbConnection(ConnectionState.Open);
        var sink = new DbSink<TestEntity>((IDbConnection)connection, "INSERT INTO TestEntities (Id, Name) VALUES (@Id, @Name)");

        await sink.DisposeAsync();

        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task DisposeAsync_DbConnectionLeaveOpenTrue_DoesNotCloseAlreadyOpenConnection()
    {
        var connection = new TrackingExecuteDbConnection(ConnectionState.Open);
        var sink = new DbSink<TestEntity>(
            connection,
            "INSERT INTO TestEntities (Id, Name) VALUES (@Id, @Name)",
            leaveOpen: true);

        await sink.DisposeAsync();

        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task InitializeAsync_DbConnection_UsesOpenAsyncAndClosesWhenOpenedBySink()
    {
        var connection = new TrackingExecuteDbConnection(ConnectionState.Closed);
        using var cts = new CancellationTokenSource();
        var sink = new DbSink<TestEntity>(
            connection,
            "INSERT INTO TestEntities (Id, Name) VALUES (@Id, @Name)",
            leaveOpen: true);

        await sink.InitializeAsync(cts.Token);
        await sink.DisposeAsync();

        Assert.Equal(1, connection.AsyncOpenCount);
        Assert.Equal(0, connection.SyncOpenCount);
        Assert.Equal(cts.Token, connection.OpenCancellationToken);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task WriteAsync_UsesCommandDefinitionCancellationToken()
    {
        var connection = new TrackingExecuteDbConnection(ConnectionState.Open);
        var sink = new DbSink<TestEntity>(
            connection,
            "INSERT INTO TestEntities (Id, Name) VALUES (@Id, @Name)",
            leaveOpen: true);
        using var cts = new CancellationTokenSource();

        await sink.WriteAsync(
            ProcessingEnvelope<TestEntity>.Create(new TestEntity { Id = 7, Name = "seven" }),
            cts.Token);

        Assert.NotNull(connection.LastCommand);
        Assert.Equal(cts.Token, connection.LastCommand.ExecuteCancellationToken);
        Assert.Equal(1, connection.LastCommand.AsyncExecuteCount);
        Assert.Equal(0, connection.LastCommand.SyncExecuteCount);
    }

    [Fact]
    public async Task GeneratedInsertSql_FiltersIndexerNotMappedAndGeneratedProperties()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "CREATE TABLE FilteredEntities (Name TEXT)";
        command.ExecuteNonQuery();
        var sink = new DbSink<FilteredEntity>(_connection);
        await sink.InitializeAsync();

        await sink.WriteAsync(ProcessingEnvelope<FilteredEntity>.Create(new FilteredEntity
        {
            Id = 123,
            Name = "filtered",
            Ignored = "ignored",
        }));

        command.CommandText = "SELECT Name FROM FilteredEntities";
        var name = (string?)command.ExecuteScalar();
        Assert.Equal("filtered", name);
    }

    [Fact]
    public void GeneratedInsertSql_ThrowsWhenNoWritableMappedPropertiesRemain()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new DbSink<NoInsertPropertiesEntity>(_connection));

        Assert.Contains("No insertable properties", exception.Message);
    }

    [Table("TestEntities")]
    private class TestEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Table("FilteredEntities")]
    private class FilteredEntity
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public string? Name { get; set; }

        [NotMapped]
        public string? Ignored { get; set; }

        public string this[int index] => index.ToString();
    }

    private class NoInsertPropertiesEntity
    {
        [NotMapped]
        public string? Ignored { get; set; }
    }

    private sealed class TrackingExecuteDbConnection : DbConnection
    {
        private ConnectionState _state;

        public TrackingExecuteDbConnection(ConnectionState state)
        {
            _state = state;
        }

        public int AsyncOpenCount { get; private set; }
        public int SyncOpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public CancellationToken OpenCancellationToken { get; private set; }
        public TrackingExecuteDbCommand? LastCommand { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "Test";
        public override string DataSource => "Test";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close()
        {
            CloseCount++;
            _state = ConnectionState.Closed;
        }

        public override void Open()
        {
            SyncOpenCount++;
            _state = ConnectionState.Open;
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            AsyncOpenCount++;
            OpenCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {
            LastCommand = new TrackingExecuteDbCommand(this);
            return LastCommand;
        }
    }

    private sealed class TrackingExecuteDbCommand : DbCommand
    {
        private readonly TrackingDbParameterCollection _parameters = new();

        public TrackingExecuteDbCommand(DbConnection connection)
        {
            DbConnection = connection;
        }

        public CancellationToken ExecuteCancellationToken { get; private set; }
        public int AsyncExecuteCount { get; private set; }
        public int SyncExecuteCount { get; private set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery()
        {
            SyncExecuteCount++;
            throw new InvalidOperationException("Synchronous ExecuteNonQuery must not be used.");
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            AsyncExecuteCount++;
            ExecuteCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(1);
        }

        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new TrackingDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();
    }
}
