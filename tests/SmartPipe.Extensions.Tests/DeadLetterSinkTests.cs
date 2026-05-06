#nullable enable

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

#pragma warning disable CS8620 // Suppress nullable mismatch with Moq ILogger verification

namespace SmartPipe.Extensions.Tests;

public class DeadLetterSinkTests
{
    [Fact]
    public async Task WriteAsync_SuccessResult_ShouldNotStore()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            var result = ProcessingResult<string>.Success("ok", 1UL);

            await sink.WriteAsync(result);
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(tempFile);
            content.Should().BeEmpty(); // No errors stored, empty file
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteAsync_FailureResult_ShouldStore()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            var error = new SmartPipeError("test error", ErrorType.Permanent);
            var result = ProcessingResult<string>.Failure(error, 2UL);
            
            await sink.WriteAsync(result);
            await sink.DisposeAsync();
            
            File.Exists(tempFile).Should().BeTrue();
            var content = await File.ReadAllTextAsync(tempFile);
            content.Should().Contain("test error");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Test 1: WriteAsync_SuccessfulWrite_ItemWrittenToFile
    /// Creates DeadLetterSink with temp file, writes failed result, verifies item written.
    /// </summary>
    [Fact]
    public async Task WriteAsync_SuccessfulWrite_ItemWrittenToFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            var error = new SmartPipeError("item failed", ErrorType.Transient);
            var result = ProcessingResult<string>.Failure(error, 42UL);

            await sink.WriteAsync(result);
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(tempFile);
            content.Should().NotBeEmpty();

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(1);

            var deserialized = JsonSerializer.Deserialize<JsonElement>(lines[0]);
            deserialized.GetProperty("IsSuccess").GetBoolean().Should().BeFalse();
            deserialized.GetProperty("TraceId").GetUInt64().Should().Be(42UL);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Test 2: WriteAsync_IoException_RetriesAndSkips
    /// Simulates IOException that always throws, verifies retries and skip behavior.
    /// </summary>
    [Fact]
    public async Task WriteAsync_IoException_RetriesAndSkips()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();

        // Create sink with memory stream for testing
        var memoryStream = new MemoryStream();
        var sink = new DeadLetterSink<string>(path: "dummy.json", logger: loggerMock.Object, stream: memoryStream);

        // Set all 3 attempts to throw IOException
        sink.SetTestExceptionForTesting(new bool[] { true, true, true });

        var error = new SmartPipeError("test error", ErrorType.Permanent);
        var result = ProcessingResult<string>.Failure(error, 1UL);

        // Should not throw, should skip after retries
        await sink.WriteAsync(result);

        // Verify Error log was called (final failure)
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to write")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Dispose to clean up
        await sink.DisposeAsync();
    }

    /// <summary>
    /// Test 3: WriteAsync_IoException_RecoversOnRetry
    /// Simulates IOException for first 2 attempts, succeeds on 3rd.
    /// </summary>
    [Fact]
    public async Task WriteAsync_IoException_RecoversOnRetry()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();

        // Create sink with memory stream for testing
        var memoryStream = new MemoryStream();
        var sink = new DeadLetterSink<string>(path: "dummy.json", logger: loggerMock.Object, stream: memoryStream);

        // Throw on attempts 1 and 2, succeed on attempt 3
        sink.SetTestExceptionForTesting(new bool[] { true, true, false });

        var error = new SmartPipeError("recoverable error", ErrorType.Transient);
        var result = ProcessingResult<string>.Failure(error, 99UL);

        await sink.WriteAsync(result);

        // Verify Warning logs for retries (2 warnings for attempts 1 and 2)
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("attempt")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));

        // Dispose to clean up
        await sink.DisposeAsync();

        // Now check what was written to the memory stream
        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream);
        var writtenContent = reader.ReadToEnd();
        writtenContent.Should().NotBeEmpty();
        writtenContent.Should().Contain("recoverable error");
    }

    /// <summary>
    /// Test 4: WriteAsync_IoException_LogsRetries
    /// Verifies that retry attempts are logged as Warning.
    /// </summary>
    [Fact]
    public async Task WriteAsync_IoException_LogsRetries()
    {
        var loggerMock = new Mock<ILogger<DeadLetterSink<string>>>();
        
        // Create sink with memory stream for testing
        var memoryStream = new MemoryStream();
        var sink = new DeadLetterSink<string>(path: "dummy.json", logger: loggerMock.Object, stream: memoryStream);
        
        // Set all 3 attempts to throw IOException
        sink.SetTestExceptionForTesting(new bool[] { true, true, true });
        
        var error = new SmartPipeError("logged error", ErrorType.Permanent);
        var result = ProcessingResult<string>.Failure(error, 5UL);
        
        await sink.WriteAsync(result);
        
        // Verify 2 Warning logs (one for each retry attempt, 3rd attempt logs Error)
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
        
        // Verify 1 Error log (final failure after 3 attempts)
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        
        // Dispose to clean up
        await sink.DisposeAsync();
    }

    /// <summary>
    /// Test 5: DisposeAsync_DrainsRemainingItems
    /// Posts 10 failed items and verifies all are written on DisposeAsync.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DrainsRemainingItems()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sink = new DeadLetterSink<string>(tempFile);
            await sink.InitializeAsync();

            // Write 10 failed items
            for (int i = 0; i < 10; i++)
            {
                var error = new SmartPipeError($"error {i}", ErrorType.Transient);
                var result = ProcessingResult<string>.Failure(error, (ulong)i);
                await sink.WriteAsync(result);
            }

            // Dispose should drain all items
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(tempFile);
            content.Should().NotBeEmpty();

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(10);

            // Verify each line is valid JSON
            for (int i = 0; i < 10; i++)
            {
                var json = JsonSerializer.Deserialize<JsonElement>(lines[i]);
                json.GetProperty("TraceId").GetUInt64().Should().Be((ulong)i);
                json.GetProperty("IsSuccess").GetBoolean().Should().BeFalse();
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
