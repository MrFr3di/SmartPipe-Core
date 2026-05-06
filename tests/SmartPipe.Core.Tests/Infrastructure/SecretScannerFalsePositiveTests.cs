// <copyright file="SecretScannerFalsePositiveTests.cs" company="SmartPipe">
// Copyright (c) SmartPipe. All rights reserved.
// </copyright>

using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Core.Tests.Infrastructure.TestCases;

namespace SmartPipe.Core.Tests.Infrastructure;

#nullable enable

/// <summary>
/// Tests to verify that SecretScanner does not produce false positives on large benign inputs.
/// Validates that large legitimate data (1MB, 5MB, 10MB) of various formats is correctly
/// identified as containing no secrets.
/// </summary>
public class SecretScannerFalsePositiveTests
{
    /// <summary>
    /// Gets all large benign input test cases for xUnit MemberData.
    /// </summary>
    public static IEnumerable<object[]> AllLargeBenignTestCases => LargeBenignInputs.GetAllTestCases();

    /// <summary>
    /// Tests that large benign inputs do not trigger false positive secret detection.
    /// Uses theory with member data from LargeBenignInputs to test multiple sizes and formats.
    /// </summary>
    /// <param name="size">The size category (e.g., "1MB", "5MB", "10MB")</param>
    /// <param name="type">The data type (e.g., "Lorem Ipsum", "JSON", "XML", "C# Code")</param>
    /// <param name="input">The large benign input string to scan</param>
    [Theory]
    [MemberData(nameof(AllLargeBenignTestCases))]
    public void Scan_LargeBenignInput_ShouldNotDetectSecrets(string size, string type, string input)
    {
        // Act
        var result = SecretScanner.HasSecrets(input);

        // Assert
        result.Should().BeFalse($"large benign {size} {type} input should not be detected as containing secrets");
    }
}
