#nullable enable
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using System.ComponentModel.DataAnnotations.Schema;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sinks;

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
        var result = ProcessingResult<TestEntity>.Success(entity, 1);
        await sink.WriteAsync(result);

        // Verify data was inserted
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TestEntities";
        var count = (long)command.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task WriteAsync_DoesNotInsert_WhenResultIsFailure()
    {
        var sink = new DbSink<TestEntity>(_connection);
        await sink.InitializeAsync();

        var result = ProcessingResult<TestEntity>.Failure(new SmartPipeError("test", ErrorType.Permanent), 1);
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

    [Table("TestEntities")]
    private class TestEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
