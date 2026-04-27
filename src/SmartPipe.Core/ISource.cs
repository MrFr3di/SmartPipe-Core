using System.Runtime.CompilerServices;

namespace SmartPipe.Core;

/// <summary>Data source for the pipeline.</summary>
/// <typeparam name="T">Type of elements produced by the source.</typeparam>
public interface ISource<T>
{
    Task InitializeAsync(CancellationToken ct = default);

    IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default);

    Task DisposeAsync();
}
