#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public class JsonFileSourceTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPathIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonFileSource<string>(null!));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenPathIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSource<string>(""));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenPathIsWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSource<string>("   "));
    }

    [Fact]
    public async Task ReadAsync_ReturnsExpectedJson_ForValidFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Используем правильный JSON формат - массив строк
            await File.WriteAllTextAsync(path, "[\"test\"]");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

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
    public async Task ReadAsync_ThrowsFileNotFoundException_ForMissingFile()
    {
        var source = new JsonFileSource<string>("missing.json");
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await foreach (var item in source.ReadAsync()) { }
        });
    }

    [Fact]
    public async Task ReadAsync_ThrowsJsonException_ForMalformedJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not json");

            var source = new JsonFileSource<string>(path);
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
    public async Task ReadAsync_ReturnsEmptyEnumerable_ForEmptyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesNdjsonFormat()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON format: each line is a separate JSON object
            var ndjson = "\"item1\"\n\"item2\"\n\"item3\"\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Equal(3, items.Count);
            Assert.Equal("item1", items[0].Payload);
            Assert.Equal("item2", items[1].Payload);
            Assert.Equal("item3", items[2].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesNdjsonWithObjects()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON with objects
            var ndjson = "{\"Value\":\"item1\"}\n{\"Value\":\"item2\"}\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<TestItem>(path);
            var items = new List<ProcessingContext<TestItem>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
            Assert.Equal("item1", items[0].Payload?.Value);
            Assert.Equal("item2", items[1].Payload?.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_ForNdjsonWithEmptyLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON with empty lines
            var ndjson = "\"item1\"\n\n\"item2\"\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesJsonArrayWithObjects()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = "[{\"Value\":\"item1\"},{\"Value\":\"item2\"}]";
            await File.WriteAllTextAsync(path, json);

            var source = new JsonFileSource<TestItem>(path);
            var items = new List<ProcessingContext<TestItem>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
            Assert.Equal("item1", items[0].Payload?.Value);
            Assert.Equal("item2", items[1].Payload?.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesWhitespaceOnlyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "   \n  \n");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            // NDJSON format will try to read lines, whitespace lines are skipped
            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesSingleObjectJson_AsNdjson()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Single object - will be treated as NDJSON (first char is not '[')
            var json = "{\"Value\":\"test\"}";
            await File.WriteAllTextAsync(path, json);

            var source = new JsonFileSource<TestItem>(path);
            var items = new List<ProcessingContext<TestItem>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Single(items);
            Assert.Equal("test", items[0].Payload?.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_SkipsNullItems_InNdjson()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON with invalid JSON line that deserializes to null
            var ndjson = "\"valid\"\nnull\n\"also valid\"\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<string?>(path);
            var items = new List<ProcessingContext<string?>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            // null items should be skipped
            Assert.Equal(2, items.Count);
            Assert.Equal("valid", items[0].Payload);
            Assert.Equal("also valid", items[1].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_SkipsNullItems_InJsonArray()
    {
        var path = Path.GetTempFileName();
        try
        {
            // JSON array with null value
            var json = "[\"valid\",null,\"also valid\"]";
            await File.WriteAllTextAsync(path, json);

            var source = new JsonFileSource<string?>(path);
            var items = new List<ProcessingContext<string?>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            // null items should be skipped
            Assert.Equal(2, items.Count);
            Assert.Equal("valid", items[0].Payload);
            Assert.Equal("also valid", items[1].Payload);
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
        await File.WriteAllTextAsync(path, "[\"test\"]");
        
        var source = new JsonFileSource<string>(path);
        await source.DisposeAsync();
        
        File.Delete(path);
    }

    [Fact]
    public async Task ReadAsync_ThrowsCancellation_WhenTokenCancelled()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[\"test\"]");

            var source = new JsonFileSource<string>(path);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var item in source.ReadAsync(cts.Token))
                {
                }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesEmptyJsonArray()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingContext<string>>();

            await foreach (var item in source.ReadAsync())
            {
                items.Add(item);
            }

            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private class TestItem
    {
        public string? Value { get; set; }
    }
}
