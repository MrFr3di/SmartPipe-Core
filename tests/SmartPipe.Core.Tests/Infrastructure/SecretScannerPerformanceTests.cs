#nullable enable
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using SmartPipe.Core.Tests.Infrastructure.TestCases;

namespace SmartPipe.Core.Tests.Infrastructure;

/// <summary>
/// Performance tests for SecretScanner to validate throughput requirements.
/// Verifies that large benign inputs are processed within acceptable throughput limits (>10MB/s).
/// </summary>
[Trait("Category", "Stress")]
public class SecretScannerPerformanceTests
{
    private const int MinimumMeasuredBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Tests that 1MB benign input meets the >10MB/s throughput requirement.
    /// </summary>
    [Fact]
    public void Scan_1MBBenignInput_ShouldMeetThroughput()
    {
        var input = LargeBenignInputs.Generate1MbLoremIpsum();
        AssertThroughput(input, "1MB input");
    }

    /// <summary>
    /// Tests that 5MB benign input meets the >10MB/s throughput requirement.
    /// </summary>
    [Fact]
    public void Scan_5MBBenignInput_ShouldMeetThroughput()
    {
        var input = LargeBenignInputs.Generate5MbLoremIpsum();
        AssertThroughput(input, "5MB input");
    }

    /// <summary>
    /// Tests that 10MB benign input meets the >10MB/s throughput requirement.
    /// </summary>
    [Fact]
    public void Scan_10MBBenignInput_ShouldMeetThroughput()
    {
        var input = LargeBenignInputs.Generate10MbLoremIpsum();
        AssertThroughput(input, "10MB input");
    }

    /// <summary>
    /// Tests throughput for 1MB JSON input.
    /// </summary>
    [Fact]
    public void Scan_1MBJsonInput_ShouldMeetThroughput()
    {
        var input = LargeBenignInputs.Generate1MbJson();
        AssertThroughput(input, "1MB JSON input");
    }

    /// <summary>
    /// Tests throughput for 1MB XML input.
    /// </summary>
    [Fact]
    public void Scan_1MBXmlInput_ShouldMeetThroughput()
    {
        var input = LargeBenignInputs.Generate1MbXml();
        AssertThroughput(input, "1MB XML input");
    }

    /// <summary>
    /// Tests throughput for 1MB C# code input.
    /// </summary>
    [Fact]
    public void Scan_1MBCSharpInput_ShouldMeetThroughput()
    {
        var input = LargeBenignInputs.Generate1MbCSharpCode();
        AssertThroughput(input, "1MB C# code input");
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(5, 4)]
    [InlineData(10, 2)]
    public void MeasurementIterations_CoverAtLeast16MiB(int inputMiB, int expectedIterations)
    {
        GetMeasurementIterations(inputMiB * 1024 * 1024).Should().Be(expectedIterations);
    }

    private static void AssertThroughput(string input, string description)
    {
        SecretScanner.HasSecrets(input).Should().BeFalse($"{description} should not contain secrets");

        var payloadBytes = Encoding.UTF8.GetByteCount(input);
        var iterations = GetMeasurementIterations(payloadBytes);
        var allBenign = true;
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            allBenign &= !SecretScanner.HasSecrets(input);
        }
        stopwatch.Stop();

        allBenign.Should().BeTrue($"{description} should not contain secrets");
        var throughput = CalculateThroughput((long)payloadBytes * iterations, stopwatch.Elapsed);
        throughput.Should().BeGreaterThan(10,
            $"aggregate throughput should be >10MiB/s for {description}; actual: {throughput:F2}MiB/s over {iterations} iterations");
    }

    private static int GetMeasurementIterations(int payloadBytes) =>
        System.Math.Max(1, (int)System.Math.Ceiling(MinimumMeasuredBytes / (double)payloadBytes));

    private static double CalculateThroughput(long bytes, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            throw new InvalidOperationException("Measured elapsed time must be positive.");

        var megabytes = bytes / 1024.0 / 1024.0;
        return megabytes / elapsed.TotalSeconds;
    }
}
