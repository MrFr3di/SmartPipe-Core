#nullable enable
using System.Data;
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
    public async Task ReadAsync_ReturnsData_WhenReaderHasRows()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        
        await connection.ExecuteAsync("CREATE TABLE Test (Id INTEGER PRIMARY KEY, Name TEXT)");
        await connection.ExecuteAsync("INSERT INTO Test (Id, Name) VALUES (1, 'TestName')");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM Test");
        await selector.InitializeAsync();
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
            await foreach (var item in selector.ReadAsync(cts.Token))
            {
            }
        });
        
        await selector.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutError()
    {
        var mockConn = new Mock<IDbConnection>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1");
        
        await selector.DisposeAsync();
        
        mockConn.Verify(c => c.Dispose(), Times.Once);
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
            await foreach (var item in selector.ReadAsync()) { }
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

        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
            await foreach (var item in selector.ReadAsync()) { }
        });

        await selector.DisposeAsync();
    }

    [Fact]
    public void Dispose_DisposesConnection()
    {
        var mockConn = new Mock<IDbConnection>();
        var selector = new DapperSelector<TestEntity>(mockConn.Object, "SELECT 1");
        
        selector.Dispose();
        
        mockConn.Verify(c => c.Dispose(), Times.Once);
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
    public void DisposeAsync_ThenDispose_DoesNotThrow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        
        var selector = new DapperSelector<TestEntity>(connection, "SELECT 1");

        selector.DisposeAsync().Wait();
        selector.Dispose(); // Should not throw
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
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
        
        var results = new List<ProcessingContext<TestEntity>>();
        await foreach (var item in selector.ReadAsync())
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
        await foreach (var item in selector.ReadAsync()) { }
        
        await selector.DisposeAsync();
    }

    [Fact]
    public void MapRow_WithInt64ToTestEntity_ConvertsCorrectly()
    {
        // Test entity with int Id property (not long)
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        
        connection.Execute("CREATE TABLE TestConv (Id INTEGER PRIMARY KEY, Name TEXT)");
        connection.Execute("INSERT INTO TestConv (Id, Name) VALUES (1, 'Test')");

        // SQLite returns Id as long (Int64), but TestEntity.Id is long too, so this should work
        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM TestConv");
        selector.InitializeAsync().Wait();

        var results = new List<ProcessingContext<TestEntity>>();
        foreach (var item in selector.ReadAsync().ToBlockingEnumerable())
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.Equal(1L, results[0].Payload.Id);
        Assert.Equal("Test", results[0].Payload.Name);

        selector.Dispose();
    }

    [Fact]
    public void MapRow_WithAllTypes_HandlesConversions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        
        connection.Execute("CREATE TABLE TestTypes (Id INTEGER PRIMARY KEY, Name TEXT, Value REAL, Active INTEGER)");
        connection.Execute("INSERT INTO TestTypes (Id, Name, Value, Active) VALUES (1, 'Test', 123.45, 1)");

        var selector = new DapperSelector<AllTypesEntity>(connection, "SELECT * FROM TestTypes");
        selector.InitializeAsync().Wait();

        var results = new List<ProcessingContext<AllTypesEntity>>();
        foreach (var item in selector.ReadAsync().ToBlockingEnumerable())
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
    public void MapRow_WithMissingColumns_SetsDefaultValues()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        
        // Create table with only Id column
        connection.Execute("CREATE TABLE TestPartial (Id INTEGER PRIMARY KEY)");
        connection.Execute("INSERT INTO TestPartial (Id) VALUES (1)");

        var selector = new DapperSelector<TestEntity>(connection, "SELECT * FROM TestPartial");
        selector.InitializeAsync().Wait();

        var results = new List<ProcessingContext<TestEntity>>();
        foreach (var item in selector.ReadAsync().ToBlockingEnumerable())
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
