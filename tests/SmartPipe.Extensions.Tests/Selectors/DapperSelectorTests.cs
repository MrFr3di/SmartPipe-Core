#nullable enable
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Selectors;

public class TestEntity
{
    public long Id { get; set; }
    public string? Name { get; set; }
}

[Trait("Category", "CorrectnessRegression")]
public class DapperSelectorTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DapperSelector<object>(null!, "SELECT 1"));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenSqlIsNull()
    {
        var mockConn = new Mock<IDbConnection>();
        Assert.Throws<ArgumentNullException>(() => new DapperSelector<object>(mockConn.Object, null!));
    }

    [Fact]
    public void Constructor_SetsProperties()
    {
        var mockConn = new Mock<IDbConnection>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1");
        Assert.NotNull(selector);
    }

    [Fact]
    public void Constructor_WithParameters_SetsProperties()
    {
        var mockConn = new Mock<IDbConnection>();
        var parameters = new { Id = 1 };

        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT * FROM Test WHERE Id = @Id", parameters);
        Assert.NotNull(selector);
    }

    [Fact]
    public void Constructor_WithCommandTimeout_SetsProperties()
    {
        var mockConn = new Mock<IDbConnection>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1", commandTimeout: 60);
        Assert.NotNull(selector);
    }

    [Fact]
    public void Constructor_WithLogger_SetsProperties()
    {
        var mockConn = new Mock<IDbConnection>();
        var mockLogger = new Mock<ILogger<DapperSelector<TestEntity>>>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1", logger: mockLogger.Object);
        Assert.NotNull(selector);
    }

    [Fact]
    public async Task InitializeAsync_OpensConnection()
    {
        var mockConn = new Mock<IDbConnection>();
        var mockState = ConnectionState.Closed;
        mockConn.SetupGet(c => c.State).Returns(() => mockState);
        mockConn.Setup(c => c.Open()).Callback(() => mockState = ConnectionState.Open);

        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1");
        await selector.InitializeAsync();

        mockConn.Verify(c => c.Open(), Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_WithDbConnection_UsesOpenAsyncWithCancellationToken()
    {
        var connection = new TrackingDbConnection();
        using var cts = new CancellationTokenSource();
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        await selector.InitializeAsync(cts.Token);

        Assert.Equal(1, connection.AsyncOpenCount);
        Assert.Equal(0, connection.SyncOpenCount);
        Assert.Equal(cts.Token, connection.OpenCancellationToken);
    }

    [Fact]
    public async Task InitializeAsync_WithCancelledToken_DoesNotOpenConnection()
    {
        var connection = new TrackingDbConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await selector.InitializeAsync(cts.Token));

        Assert.Equal(0, connection.AsyncOpenCount);
        Assert.Equal(0, connection.SyncOpenCount);
    }

    [Fact]
    public async Task InitializeAsync_WithOpenConnection_DoesNotOpenAgain()
    {
        var connection = new TrackingDbConnection(ConnectionState.Open);
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        await selector.InitializeAsync();

        Assert.Equal(0, connection.AsyncOpenCount);
        Assert.Equal(0, connection.SyncOpenCount);
    }

    [Fact]
    public async Task ReadAsync_ReturnsData_WhenReaderHasRows()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");
        await connection.ExecuteAsync("INSERT INTO Test (Id, Name) VALUES (1, 'TestName')");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Equal("TestName", results[0].Payload.Name);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_WhenReaderHasNoRows()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Empty(results);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_HandlesNullValues_InDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");
        await connection.ExecuteAsync("INSERT INTO Test (Id, Name) VALUES (1, NULL)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Null(results[0].Payload.Name);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_HandlesUnknownProperties()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, UnknownColumn TEXT)");
        await connection.ExecuteAsync("INSERT INTO Test (Id, UnknownColumn) VALUES (1, 'some value')");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Null(results[0].Payload.Name);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_LogsInformation_WhenLoggerProvided()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY)");
        await connection.ExecuteAsync("INSERT INTO Test (Id) VALUES (1)");

        var mockLogger = new Mock<ILogger<DapperSelector<TestEntity>>>();
        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test", logger: mockLogger.Object);
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Dapper source completed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_ThrowsCancellation_WhenTokenCancelled()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY)");
        await connection.ExecuteAsync("INSERT INTO Test (Id) VALUES (1)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in selector.ReadEnvelopesAsync(cts.Token))
            {
            }
        });

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_WithDbConnection_UsesAsyncCommandAndReadWithCancellationToken()
    {
        var reader = TrackingDbDataReader.Create(
            ("Id", typeof(long), 1L),
            ("Name", typeof(string), "Async"));
        var connection = new TrackingDbConnection(ConnectionState.Open, reader);
        using var cts = new CancellationTokenSource();
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var envelope in selector.ReadEnvelopesAsync(cts.Token))
            results.Add(envelope);

        Assert.Single(results);
        Assert.Equal("Async", results[0].Payload.Name);
        Assert.NotNull(connection.LastCommand);
        Assert.Equal(cts.Token, connection.LastCommand.ExecuteCancellationToken);
        Assert.Equal(cts.Token, reader.ReadCancellationToken);
        Assert.Equal(0, connection.LastCommand.SyncExecuteCount);
        Assert.Equal(0, reader.SyncReadCount);
        Assert.Equal(1, reader.AsyncDisposeCount);
    }

    [Fact]
    public async Task ReadAsync_WhenCancelledDuringPendingRead_ThrowsOperationCanceledException()
    {
        var reader = TrackingDbDataReader.CreateBlocking();
        var connection = new TrackingDbConnection(ConnectionState.Open, reader);
        using var cts = new CancellationTokenSource();
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");
        await using var enumerator = selector.ReadEnvelopesAsync(cts.Token).GetAsyncEnumerator();

        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        await reader.ReadStarted.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNextTask);
        Assert.Equal(cts.Token, reader.ReadCancellationToken);
        Assert.Equal(0, reader.SyncReadCount);
    }

    [Fact]
    public async Task ReadAsync_WhenEnumerationStopsEarly_DisposesReader()
    {
        var reader = TrackingDbDataReader.Create(
            ("Id", typeof(long), 1L),
            ("Name", typeof(string), "First"));
        var connection = new TrackingDbConnection(ConnectionState.Open, reader);
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        await foreach (var _ in selector.ReadEnvelopesAsync())
            break;

        Assert.Equal(1, reader.AsyncDisposeCount);
    }

    [Fact]
    public async Task ReadAsync_WithLegacyConnection_UsesSynchronousCompatibilityFallback()
    {
        var reader = new Mock<IDataReader>();
        reader.SetupGet(value => value.IsClosed).Returns(false);
        reader.SetupGet(value => value.FieldCount).Returns(1);
        reader.Setup(value => value.GetName(0)).Returns("Id");
        reader.Setup(value => value.IsDBNull(0)).Returns(false);
        reader.Setup(value => value.GetValue(0)).Returns(1L);
        reader.SetupSequence(value => value.Read()).Returns(true).Returns(false);

        var command = new Mock<IDbCommand>();
        command.SetupProperty(value => value.CommandText);
        command.SetupProperty(value => value.CommandTimeout);
        command.Setup(value => value.ExecuteReader()).Returns(reader.Object);
        command
            .Setup(value => value.ExecuteReader(It.IsAny<CommandBehavior>()))
            .Returns(reader.Object);

        var connection = new Mock<IDbConnection>();
        connection.SetupGet(value => value.State).Returns(ConnectionState.Open);
        connection.Setup(value => value.CreateCommand()).Returns(command.Object);
        var selector = new DapperSelector<TestEntity>(connection.Object, "SELECT 1");

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var envelope in selector.ReadEnvelopesAsync())
            results.Add(envelope);

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        reader.Verify(value => value.Read(), Times.Exactly(2));
        reader.Verify(value => value.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DefaultLeaveOpen_DoesNotDisposeConnection()
    {
        var mockConn = new Mock<IDbConnection>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1");

        await selector.DisposeAsync();

        mockConn.Verify(c => c.Dispose(), Times.Never);
    }

    [Fact]
    public async Task ReadAsync_WithInvalidSql_ThrowsException()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM NonExistentTable");
        await selector.InitializeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in selector.ReadEnvelopesAsync()) { }
        });

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_WithNullParameters_WorksCorrectly()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");
        await connection.ExecuteAsync("INSERT INTO Test (Id, Name) VALUES (1, 'Test')");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test", parameters: null);
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_WithInvalidParameters_ThrowsException()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");

        var selector = new DapperSelector<TestEntity>(
            connection,
            "SELECT * FROM Test WHERE Id = @NonExistentParam",
            parameters: new { DifferentParam = 1 });
        await selector.InitializeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in selector.ReadEnvelopesAsync()) { }
        });

        await selector.DisposeAsync();
    }

    [Fact]
    public void Dispose_DefaultLeaveOpen_DoesNotDisposeConnection()
    {
        var mockConn = new Mock<IDbConnection>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1");

        selector.Dispose();

        mockConn.Verify(c => c.Dispose(), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_WhenLeaveOpenIsFalse_DisposesDbConnectionOnce()
    {
        var connection = new TrackingDbConnection(ConnectionState.Open);
        var selector = new DapperSelector<TestEntity>(
            connection,
            "SELECT 1",
            parameters: null,
            leaveOpen: false,
            commandTimeout: 30,
            logger: null);

        await selector.DisposeAsync();
        await selector.DisposeAsync();
        selector.Dispose();

        Assert.Equal(1, connection.AsyncDisposeCount);
        Assert.Equal(0, connection.SyncDisposeCount);
    }

    [Fact]
    public async Task Dispose_WhenLeaveOpenIsFalse_DisposesDbConnectionOnce()
    {
        var connection = new TrackingDbConnection(ConnectionState.Open);
        var selector = new DapperSelector<TestEntity>(
            connection,
            "SELECT 1",
            parameters: null,
            leaveOpen: false,
            commandTimeout: 30,
            logger: null);

        selector.Dispose();
        selector.Dispose();
        await selector.DisposeAsync();

        Assert.Equal(0, connection.AsyncDisposeCount);
        Assert.Equal(1, connection.SyncDisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_DoesNotThrow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        await selector.DisposeAsync();
        await selector.DisposeAsync(); // Second call should not throw
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        selector.Dispose();
        selector.Dispose(); // Second call should not throw
    }

    [Fact]
    public async Task DisposeAsync_ThenDispose_DoesNotThrow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        await selector.DisposeAsync();
        var exception = Record.Exception(selector.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReadAsync_WithParameters_FiltersResults()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");
        await connection.ExecuteAsync("INSERT INTO Test (Id, Name) VALUES (1, 'One')");
        await connection.ExecuteAsync("INSERT INTO Test (Id, Name) VALUES (2, 'Two')");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test WHERE Id = @Id", new { Id = 1 });
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_WithCommandTimeout_DoesNotThrow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY)");
        await connection.ExecuteAsync("INSERT INTO Test (Id) VALUES (1)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test", commandTimeout: 60);
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithReader_DisposesReader()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY)");
        await connection.ExecuteAsync("INSERT INTO Test (Id) VALUES (1)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();

        // Execute ReadAsync to initialize _reader
        await foreach (var item in selector.ReadEnvelopesAsync()) { }

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task MapRow_WithInt64ToTestEntity_ConvertsCorrectly()
    {
        // Test entity with int Id property (not long)
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        connection.Execute("CREATE TABLE TestConv (Id INTEGER PRIMARY KEY, Name TEXT)");
        connection.Execute("INSERT INTO TestConv (Id, Name) VALUES (1, 'Test')");

        // SQLite returns Id as long (Int64), but TestEntity.Id is long too, so this should work
        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM TestConv");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Equal("Test", results[0].Payload.Name);

        selector.Dispose();
    }

    [Fact]
    public async Task MapRow_WithAllTypes_HandlesConversions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        connection.Execute("CREATE TABLE TestTypes (Id INTEGER PRIMARY KEY, Name TEXT, Value REAL, Active INTEGER)");
        connection.Execute("INSERT INTO TestTypes (Id, Name, Value, Active) VALUES (1, 'Test', 123.45, 1)");

        var selector = new DapperSelector<AllTypesEntity>(connection, "SELECT * FROM TestTypes");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<AllTypesEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Equal("Test", results[0].Payload.Name);
        Assert.Equal(123.45, results[0].Payload.Value);
        Assert.Equal(1, results[0].Payload.Active);

        selector.Dispose();
    }

    [Fact]
    public async Task ReadAsync_WithExplicitDbDataReaderMapper_UsesMapper()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE TestMapper (Id INTEGER PRIMARY KEY, Name TEXT)");
        await connection.ExecuteAsync("INSERT INTO TestMapper (Id, Name) VALUES (5, 'mapped')");

        var selector = new DapperSelector<TestEntity>(
            connection,
            "SELECT Id, Name FROM TestMapper",
            static reader => new TestEntity
            {
                Id = reader.GetInt64(0),
                Name = $"{reader.GetString(1)}!",
            });
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            results.Add(item);

        Assert.Single(results);
        Assert.Equal(5L, results[0].Payload.Id);
        Assert.Equal("mapped!", results[0].Payload.Name);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task MapRow_DefaultMapper_HandlesNullableEnumNumericAndBoolConversions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            "CREATE TABLE TestConversions (Count INTEGER, Status TEXT, Optional INTEGER NULL, Enabled INTEGER)");
        await connection.ExecuteAsync(
            "INSERT INTO TestConversions (Count, Status, Optional, Enabled) VALUES (7, 'Active', NULL, 1)");

        var selector = new DapperSelector<ConversionEntity>(
            connection,
            "SELECT Count, Status, Optional, Enabled FROM TestConversions");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<ConversionEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            results.Add(item);

        Assert.Single(results);
        Assert.Equal(7, results[0].Payload.Count);
        Assert.Equal(ConversionStatus.Active, results[0].Payload.Status);
        Assert.Null(results[0].Payload.Optional);
        Assert.True(results[0].Payload.Enabled);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task MapRow_DefaultMapper_FailsWithClearMessageForUnsupportedConversion()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await connection.ExecuteAsync("CREATE TABLE TestBadConversion (Id TEXT)");
        await connection.ExecuteAsync("INSERT INTO TestBadConversion (Id) VALUES ('not-an-int')");

        var selector = new DapperSelector<IntEntity>(
            connection,
            "SELECT Id FROM TestBadConversion");
        await selector.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in selector.ReadEnvelopesAsync()) { }
        });

        Assert.Contains("Column 'Id'", exception.Message);
        Assert.Contains(nameof(IntEntity.Id), exception.Message);

        await selector.DisposeAsync();
    }

    [Fact]
    public async Task MapRow_WithMissingColumns_SetsDefaultValues()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        // Create table with only Id column
        connection.Execute("CREATE TABLE TestPartial (Id INTEGER PRIMARY KEY)");
        connection.Execute("INSERT INTO TestPartial (Id) VALUES (1)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM TestPartial");
        await selector.InitializeAsync();

        var results = new List<ProcessingEnvelope<TestEntity>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Null(results[0].Payload.Name); // Name column doesn't exist, should be default

        selector.Dispose();
    }
}

public class AllTypesEntity
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public double Value { get; set; }
    public long Active { get; set; } // SQLite returns INTEGER as Int64
}

public enum ConversionStatus
{
    Unknown,
    Active,
}

public class ConversionEntity
{
    public int Count { get; set; }
    public ConversionStatus Status { get; set; }
    public int? Optional { get; set; }
    public bool Enabled { get; set; }
}

public class IntEntity
{
    public int Id { get; set; }
}

internal sealed class TrackingDbConnection : DbConnection
{
    private ConnectionState _state;
    private readonly DbDataReader? _reader;

    public TrackingDbConnection(
        ConnectionState state = ConnectionState.Closed,
        DbDataReader? reader = null)
    {
        _state = state;
        _reader = reader;
    }

    public int AsyncOpenCount { get; private set; }
    public int SyncOpenCount { get; private set; }
    public int AsyncDisposeCount { get; private set; }
    public int SyncDisposeCount { get; private set; }
    public CancellationToken OpenCancellationToken { get; private set; }
    public TrackingDbCommand? LastCommand { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "Test";
    public override string DataSource => "Test";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close()
    {
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

    public override ValueTask DisposeAsync()
    {
        AsyncDisposeCount++;
        _state = ConnectionState.Closed;
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SyncDisposeCount++;
            _state = ConnectionState.Closed;
        }

        base.Dispose(disposing);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand()
    {
        LastCommand = new TrackingDbCommand(this, _reader ?? throw new NotSupportedException());
        return LastCommand;
    }
}

internal sealed class TrackingDbCommand : DbCommand
{
    private readonly DbDataReader _reader;
    private readonly TrackingDbParameterCollection _parameters = new();

    public TrackingDbCommand(DbConnection connection, DbDataReader reader)
    {
        DbConnection = connection;
        _reader = reader;
    }

    public CancellationToken ExecuteCancellationToken { get; private set; }
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
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new TrackingDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        SyncExecuteCount++;
        throw new InvalidOperationException("Synchronous ExecuteReader must not be used.");
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ExecuteCancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_reader);
    }
}

internal sealed class TrackingDbDataReader : DbDataReader
{
    private readonly DataTableReader? _inner;
    private readonly TaskCompletionSource<bool>? _readGate;

    private TrackingDbDataReader(DataTableReader inner)
    {
        _inner = inner;
    }

    private TrackingDbDataReader()
    {
        _readGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public int SyncReadCount { get; private set; }
    public int AsyncDisposeCount { get; private set; }
    public CancellationToken ReadCancellationToken { get; private set; }
    public TaskCompletionSource ReadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TrackingDbDataReader Create(params (string Name, Type Type, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var column in columns)
            table.Columns.Add(column.Name, column.Type);
        table.Rows.Add(columns.Select(column => column.Value).ToArray());
        return new TrackingDbDataReader(table.CreateDataReader());
    }

    public static TrackingDbDataReader CreateBlocking() => new();

    public override int Depth => _inner?.Depth ?? 0;
    public override int FieldCount => _inner?.FieldCount ?? 0;
    public override bool HasRows => _inner?.HasRows ?? false;
    public override bool IsClosed => _inner?.IsClosed ?? false;
    public override int RecordsAffected => _inner?.RecordsAffected ?? 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        SyncReadCount++;
        throw new InvalidOperationException("Synchronous Read must not be used.");
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ReadCancellationToken = cancellationToken;
        ReadStarted.TrySetResult();

        if (_readGate is not null)
            return await _readGate.Task.WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return _inner!.Read();
    }

    public override ValueTask DisposeAsync()
    {
        AsyncDisposeCount++;
        _inner?.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner?.Dispose();
        base.Dispose(disposing);
    }

    public override bool NextResult() => _inner?.NextResult() ?? false;
    public override string GetName(int ordinal) => _inner!.GetName(ordinal);
    public override string GetDataTypeName(int ordinal) => _inner!.GetDataTypeName(ordinal);
    public override Type GetFieldType(int ordinal) => _inner!.GetFieldType(ordinal);
    public override object GetValue(int ordinal) => _inner!.GetValue(ordinal);
    public override int GetValues(object[] values) => _inner!.GetValues(values);
    public override int GetOrdinal(string name) => _inner!.GetOrdinal(name);
    public override bool GetBoolean(int ordinal) => _inner!.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _inner!.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        _inner!.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => _inner!.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        _inner!.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override Guid GetGuid(int ordinal) => _inner!.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => _inner!.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => _inner!.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => _inner!.GetInt64(ordinal);
    public override float GetFloat(int ordinal) => _inner!.GetFloat(ordinal);
    public override double GetDouble(int ordinal) => _inner!.GetDouble(ordinal);
    public override string GetString(int ordinal) => _inner!.GetString(ordinal);
    public override decimal GetDecimal(int ordinal) => _inner!.GetDecimal(ordinal);
    public override DateTime GetDateTime(int ordinal) => _inner!.GetDateTime(ordinal);
    public override bool IsDBNull(int ordinal) => _inner!.IsDBNull(ordinal);
    public override IEnumerator GetEnumerator() => (_inner ?? throw new NotSupportedException()).GetEnumerator();
}

internal sealed class TrackingDbParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() { }
}

internal sealed class TrackingDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;
    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
            Add(value!);
    }

    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => _items.Any(item => item.ParameterName == value);
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) =>
        _items.FindIndex(item => item.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            _items[index] = value;
        else
            _items.Add(value);
    }
}
