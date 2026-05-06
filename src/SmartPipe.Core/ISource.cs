#nullable enable

namespace SmartPipe.Core;

/// <summary>Data source for the pipeline.</summary>
/// <typeparam name="T">Type of elements produced by the source.</typeparam>
public interface ISource<T>
{
    /// <summary>Initialize the source (open connections, load config, etc.).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Read all items from the source as an async enumerable.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of <see cref="ProcessingContext{T}"/> instances with payload data.</returns>
    IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default);

    /// <summary>Release all resources used by the source.</summary>
    Task DisposeAsync();
}
