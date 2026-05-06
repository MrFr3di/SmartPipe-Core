#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public class DeadLetterSourceTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPathIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DeadLetterSource<string>(null!));
    }

    [Fact]
    public async Task InitializeAsync_ThrowsFileNotFoundException_WhenFileMissing()
    {
        var source = new DeadLetterSource<string>("nonexistent.json");
        await Assert.ThrowsAsync<FileNotFoundException>(() => source.InitializeAsync());
    }

    [Fact]
    public async Task InitializeAsync_Completes_WhenFileExists()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");
            
            var source = new DeadLetterSource<string>(path);
            await source.InitializeAsync(); // Should not throw
            
            Assert.True(true); // If we get here, test passed
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnEmptyEnumerable_WhenNoDeadLetters()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "[]");

        var source = new DeadLetterSource<string>(path);
        var items = new List<ProcessingContext<string>>();

        await foreach (var item in source.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Empty(items);
        File.Delete(path);
    }

    [Fact]
    public async Task ReadAsync_ReturnsItems_WhenDeadLettersExist()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Create JSON with failed items (dead letters) using proper ProcessingResult structure
            var deadLetters = new[]
            {
                new { Value = "item1", IsSuccess = false, Error = new { Message = "test error", Type = "Transient" }, TraceId = 1UL },
                new { Value = "item2", IsSuccess = false, Error = new { Message = "test error 2", Type = "Permanent" }, TraceId = 2UL }
            };
            var json = JsonSerializer.Serialize(deadLetters);
            await File.WriteAllTextAsync(path, json);

            var source = new DeadLetterSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.NotEmpty(items);
            Assert.Equal("item1", items[0].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldSkipSuccessfulItems()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Create JSON with mix of success and failure
            // Note: IsSuccess = true items should be skipped
            var items = new object[]
            {
                new { Value = "success", IsSuccess = true, Error = default(object), TraceId = default(ulong?) },
                new { Value = "failure", IsSuccess = false, Error = new { Message = "test", Type = "Transient" }, TraceId = (ulong?)1UL },
            };
            var json = JsonSerializer.Serialize(items);
            await File.WriteAllTextAsync(path, json);

            var source = new DeadLetterSource<string>(path);
            var result = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                result.Add(item);
            }

            // Only failed items should be returned
            Assert.Single(result);
            Assert.Equal("failure", result[0].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleMissingValueProperty()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Item without Value property - should be skipped
            var items = new[]
            {
                new { IsSuccess = false, Error = "test error" }
            };
            var json = JsonSerializer.Serialize(items);
            await File.WriteAllTextAsync(path, json);

            var source = new DeadLetterSource<string>(path);
            var result = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                result.Add(item);
            }

            // No valid items with Value property
            Assert.Empty(result);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleNullValue()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Item with null Value - should be skipped
            var items = new[]
            {
                new { Value = (string?)null, IsSuccess = false, Error = "test error" }
            };
            var json = JsonSerializer.Serialize(items);
            await File.WriteAllTextAsync(path, json);

            var source = new DeadLetterSource<string>(path);
            var result = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                result.Add(item);
            }

            // Null values should be skipped
            Assert.Empty(result);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleInvalidJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "invalid json");

            var source = new DeadLetterSource<string>(path);
            
            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleEmptyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "");

            var source = new DeadLetterSource<string>(path);
            
            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleCancellation()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "[{\"value\":\"test\",\"isSuccess\":false}]");

        var source = new DeadLetterSource<string>(path);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in source.ReadAsync(cts.Token))
            {
            }
        });

        File.Delete(path);
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleSingleObjectJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Single object instead of array - should be handled as a single item
            var json = JsonSerializer.Serialize(new { Value = "test", IsSuccess = false });
            await File.WriteAllTextAsync(path, json);

            var source = new DeadLetterSource<string>(path);
            
            var items = new List<ProcessingContext<string>>();
            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            // Single object should be processed
            Assert.Single(items);
            Assert.Equal("test", items[0].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutError()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "[]");
        
        var source = new DeadLetterSource<string>(path);
        await source.DisposeAsync();
        
        File.Delete(path);
    }

    [Fact]
    public async Task ReadAsync_ShouldHandleComplexType()
    {
        var path = Path.GetTempFileName();
        try
        {
            var deadLetters = new[]
            {
                new { Value = new { Id = 1, Name = "Test" }, IsSuccess = false, Error = "test error", TraceId = 1UL }
            };
            var json = JsonSerializer.Serialize(deadLetters);
            await File.WriteAllTextAsync(path, json);

            var source = new DeadLetterSource<TestComplexType>(path);
            var result = new List<ProcessingContext<TestComplexType>>();

            await foreach (var item in source.ReadAsync())
            {
                result.Add(item);
            }

            Assert.Single(result);
            Assert.Equal(1, result[0].Payload?.Id);
            Assert.Equal("Test", result[0].Payload?.Name);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ThrowsJsonException_ForUnexpectedJsonStructure()
    {
        var path = Path.GetTempFileName();
        try
        {
            // JSON with unexpected root element (string instead of array or object)
            await File.WriteAllTextAsync(path, "\"just a string\"");

            var source = new DeadLetterSource<string>(path);

            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ThrowsJsonException_ForJsonNumberRoot()
    {
        var path = Path.GetTempFileName();
        try
        {
            // JSON with unexpected root element (number instead of array or object)
            await File.WriteAllTextAsync(path, "12345");

            var source = new DeadLetterSource<string>(path);

            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private class TestComplexType
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
