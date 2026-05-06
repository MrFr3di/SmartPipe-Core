#nullable enable
using System.Diagnostics;
using FluentAssertions;
using SmartPipe.Core.Tests.Infrastructure.TestCases;

namespace SmartPipe.Core.Tests.Infrastructure;

/// <summary>
/// Performance tests for SecretScanner to validate throughput requirements.
/// Verifies that large benign inputs are processed within acceptable throughput limits (>10MB/s).
/// </summary>
public class SecretScannerPerformanceTests
{
    /// <summary>
    /// Tests that 1MB benign input meets the >10MB/s throughput requirement.
    /// </summary>
    [Fact]
    public void Scan_1MBBenignInput_ShouldMeetThroughput()
    {
        // Arrange
        var input = LargeBenignInputs.Generate1MbLoremIpsum();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = SecretScanner.HasSecrets(input);

        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("1MB benign input should not contain secrets");

        var throughput = CalculateThroughput(input.Length, stopwatch.ElapsedMilliseconds);
        throughput.Should().BeGreaterThan(10,
            $"Throughput should be >10MB/s, actual: {throughput:F2}MB/s for 1MB input");
    }

    /// <summary>
    /// Tests that 5MB benign input meets the >10MB/s throughput requirement.
    /// </summary>
    [Fact]
    public void Scan_5MBBenignInput_ShouldMeetThroughput()
    {
        // Arrange
        var input = LargeBenignInputs.Generate5MbLoremIpsum();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = SecretScanner.HasSecrets(input);

        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("5MB benign input should not contain secrets");

        var throughput = CalculateThroughput(input.Length, stopwatch.ElapsedMilliseconds);
        throughput.Should().BeGreaterThan(10,
            $"Throughput should be >10MB/s, actual: {throughput:F2}MB/s for 5MB input");
    }

    /// <summary>
    /// Tests that 10MB benign input meets the >10MB/s throughput requirement.
    /// </summary>
    [Fact]
    public void Scan_10MBBenignInput_ShouldMeetThroughput()
    {
        // Arrange
        var input = LargeBenignInputs.Generate10MbLoremIpsum();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = SecretScanner.HasSecrets(input);

        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("10MB benign input should not contain secrets");

        var throughput = CalculateThroughput(input.Length, stopwatch.ElapsedMilliseconds);
        throughput.Should().BeGreaterThan(10,
            $"Throughput should be >10MB/s, actual: {throughput:F2}MB/s for 10MB input");
    }

    /// <summary>
    /// Tests throughput for 1MB JSON input.
    /// </summary>
    [Fact]
    public void Scan_1MBJsonInput_ShouldMeetThroughput()
    {
        // Arrange
        var input = LargeBenignInputs.Generate1MbJson();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = SecretScanner.HasSecrets(input);

        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("1MB JSON input should not contain secrets");

        var throughput = CalculateThroughput(input.Length, stopwatch.ElapsedMilliseconds);
        throughput.Should().BeGreaterThan(10,
            $"Throughput should be >10MB/s, actual: {throughput:F2}MB/s for 1MB JSON input");
    }

    /// <summary>
    /// Tests throughput for 1MB XML input.
    /// </summary>
    [Fact]
    public void Scan_1MBXmlInput_ShouldMeetThroughput()
    {
        // Arrange
        var input = LargeBenignInputs.Generate1MbXml();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = SecretScanner.HasSecrets(input);

        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("1MB XML input should not contain secrets");

        var throughput = CalculateThroughput(input.Length, stopwatch.ElapsedMilliseconds);
        throughput.Should().BeGreaterThan(10,
            $"Throughput should be >10MB/s, actual: {throughput:F2}MB/s for 1MB XML input");
    }

    /// <summary>
    /// Tests throughput for 1MB C# code input.
    /// </summary>
    [Fact]
    public void Scan_1MBCSharpInput_ShouldMeetThroughput()
    {
        // Arrange
        var input = LargeBenignInputs.Generate1MbCSharpCode();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = SecretScanner.HasSecrets(input);

        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("1MB C# code input should not contain secrets");

        var throughput = CalculateThroughput(input.Length, stopwatch.ElapsedMilliseconds);
        throughput.Should().BeGreaterThan(10,
            $"Throughput should be >10MB/s, actual: {throughput:F2}MB/s for 1MB C# code input");
    }

    /// <summary>
    /// Calculates throughput in MB/s.
    /// </summary>
    /// <param name="bytes">Number of bytes processed</param>
    /// <param name="milliseconds">Time taken in milliseconds</param>
    /// <returns>Throughput in MB/s</returns>
    private static double CalculateThroughput(int bytes, long milliseconds)
    {
        if (milliseconds == 0) return double.MaxValue;
        var seconds = milliseconds / 1000.0;
        var megabytes = bytes / 1024.0 / 1024.0;
        return megabytes / seconds;
    }
}
