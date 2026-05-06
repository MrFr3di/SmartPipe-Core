#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Data sink — the final stage of the pipeline.</summary>
/// <typeparam name="T">Type of elements consumed by the sink.</typeparam>
public interface ISink<T>
{
    /// <summary>Async initialization (open file, connect to DB, etc.).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing async initialization.</returns>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Write a single <see cref="ProcessingResult{T}"/> to the sink.</summary>
    /// <param name="result">Processing result to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing async write operation.</returns>
    Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default);

    /// <summary>Release resources.</summary>
    Task DisposeAsync();
}
