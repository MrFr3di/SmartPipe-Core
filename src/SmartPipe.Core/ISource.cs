using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Data source for the pipeline.</summary>
/// <typeparam name="T">Type of elements produced by the source.</typeparam>
public interface ISource<T>
{
    /// <summary>Async initialization, called once before reading.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Read data as an async stream of contexts.</summary>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default);

    /// <summary>Release resources.</summary>
    Task DisposeAsync();
}
