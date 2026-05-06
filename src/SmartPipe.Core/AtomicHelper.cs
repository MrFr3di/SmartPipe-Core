#nullable enable
namespace SmartPipe.Core;

/// <summary>
/// Provides atomic operations for double values using compare-exchange loops.
/// </summary>
internal static class AtomicHelper
{
    /// <summary>
    /// Atomically updates a double value by applying a transformation function.
    /// </summary>
    /// <param name="location">Reference to the value to update.</param>
    /// <param name="update">Function that computes the new value from the current value.</param>
    public static void CompareExchangeLoop(ref double location, Func<double, double> update)
    {
        double current, updated;
        do
        {
            current = location;
            updated = update(current);
        } while (Interlocked.CompareExchange(ref location, updated, current) != current);
    }
}
