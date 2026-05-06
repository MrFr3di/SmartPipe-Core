using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ProbabilisticMergeTests
{
    private const int HllPrecision = 14; // Higher precision for better accuracy with 900K items
    private const int ItemsPerFilter = 500_000;
    private const int OverlapCount = 100_000;
    private const int ExpectedUnique = 900_000;
    private const double HllRelativeError = 0.03; // ~3% error for precision 14

    /// <summary>
    /// Test 1: HyperLogLog merge correctness.
    /// Create two HLL estimators, add 500K unique hashes to each with 100K overlap,
    /// merge them, verify result matches direct estimation of 900K unique hashes.
    /// </summary>
    [Fact]
    public void HyperLogLog_Merge_ShouldEstimate900KUniqueItems()
    {
        // Arrange
        var hll1 = new HyperLogLogEstimator(HllPrecision);
        var hll2 = new HyperLogLogEstimator(HllPrecision);

        // Add 500K items to first HLL (0 to 499,999)
        for (ulong i = 0; i < ItemsPerFilter; i++)
        {
            hll1.Add(i);
        }

        // Add 500K items to second HLL (400,000 to 899,999) - creates 100K overlap (400,000-499,999)
        for (ulong i = ItemsPerFilter - OverlapCount; i < ItemsPerFilter + (ItemsPerFilter - OverlapCount); i++)
        {
            hll2.Add(i);
        }

        // Act: Merge the two estimators
        var merged = HyperLogLogEstimator.Merge(hll1, hll2);

        // Estimate directly from all 900K unique items
        var directHll = new HyperLogLogEstimator(HllPrecision);
        for (ulong i = 0; i < ExpectedUnique; i++)
        {
            directHll.Add(i);
        }
        double directEstimate = directHll.Estimate();

        // Assert: Merged estimate should be close to direct estimate
        double mergedEstimate = merged.Estimate();

        // Allow for HLL error margin
        double tolerance = directEstimate * HllRelativeError;
        mergedEstimate.Should().BeApproximately(directEstimate, tolerance,
            $"Merged HLL should estimate ~{ExpectedUnique} unique items with ~{HllRelativeError * 100}% error");

        // Also verify it's in the right ballpark (between 600K and 1.2M)
        mergedEstimate.Should().BeGreaterThan(600_000, "Merged estimate should be > 600K");
        mergedEstimate.Should().BeLessThan(1_200_000, "Merged estimate should be < 1.2M");
    }

    /// <summary>
    /// Test 2: CuckooFilter merge correctness.
    /// Create two filters, add 500 items to each with 100 overlap,
    /// merge them, verify merged filter contains all 900 unique items and no false negatives.
    /// </summary>
    [Fact]
    public void CuckooFilter_Merge_ShouldContainAll900UniqueItems()
    {
        // Arrange
        const int expectedItems = 1000; // Enough capacity for 900 unique items
        var filter1 = new CuckooFilter(expectedItems: expectedItems);
        var filter2 = new CuckooFilter(expectedItems: expectedItems);

        // Track all unique items for verification
        var allUniqueItems = new HashSet<ulong>();

        // Add 500 items to first filter (0 to 499)
        for (ulong i = 0; i < ItemsPerFilter / 1000; i++) // 500 items (scaled down from 500K for test speed)
        {
            filter1.Add(i);
            allUniqueItems.Add(i);
        }

        // Add 500 items to second filter (400 to 899) - creates 100 overlap (400-499)
        int itemsPerFilterScaled = ItemsPerFilter / 1000; // 500
        int overlapScaled = OverlapCount / 1000; // 100
        for (ulong i = (ulong)(itemsPerFilterScaled - overlapScaled); i < (ulong)(itemsPerFilterScaled + (itemsPerFilterScaled - overlapScaled)); i++)
        {
            filter2.Add(i);
            allUniqueItems.Add(i);
        }

        // Act: Merge filter2 into filter1
        filter1.Merge(filter2);

        // Assert: Merged filter should contain all unique items (no false negatives)
        int falseNegatives = 0;
        foreach (var item in allUniqueItems)
        {
            if (!filter1.Contains(item))
            {
                falseNegatives++;
            }
        }

        falseNegatives.Should().Be(0,
            $"Merged CuckooFilter should contain all {allUniqueItems.Count} unique items with no false negatives. Found {falseNegatives} false negatives.");

        // Also verify the count is correct (should be close to 900)
        filter1.Count.Should().BeGreaterThan(800, "Merged filter should contain at least 800 items");
        filter1.Count.Should().BeLessThanOrEqualTo(900, "Merged filter should contain at most 900 items");
    }

    /// <summary>
    /// Additional test: Verify CuckooFilter merge handles empty filter correctly.
    /// </summary>
    [Fact]
    public void CuckooFilter_Merge_WithEmptyFilter_ShouldWork()
    {
        // Arrange
        var filter1 = new CuckooFilter(expectedItems: 100);
        var emptyFilter = new CuckooFilter(expectedItems: 100);

        // Add some items to filter1
        for (ulong i = 0; i < 50; i++)
        {
            filter1.Add(i);
        }

        // Act: Merge empty filter into filter1
        filter1.Merge(emptyFilter);

        // Assert: filter1 should still contain all original items
        for (ulong i = 0; i < 50; i++)
        {
            filter1.Contains(i).Should().BeTrue($"Item {i} should still be in filter after merging empty filter");
        }
        filter1.Count.Should().Be(50);
    }

    /// <summary>
    /// Additional test: Verify HyperLogLog merge with empty estimator.
    /// </summary>
    [Fact]
    public void HyperLogLog_Merge_WithEmptyEstimator_ShouldWork()
    {
        // Arrange
        var hll1 = new HyperLogLogEstimator(12);
        var emptyHll = new HyperLogLogEstimator(12);

        // Add items to hll1
        for (ulong i = 0; i < 10000; i++)
        {
            hll1.Add(i);
        }

        // Act: Merge empty HLL into hll1
        var merged = HyperLogLogEstimator.Merge(hll1, emptyHll);

        // Assert: Should estimate ~10000
        var estimate = merged.Estimate();
        estimate.Should().BeGreaterThan(7000, "Merged with empty should still estimate ~10000");
        estimate.Should().BeLessThan(15000, "Merged with empty should still estimate ~10000");
    }
}
