#nullable enable

using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>Creates configured SmartPipe channels on demand.</summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public interface ISmartPipeChannelFactory<TInput, TOutput>
{
    /// <summary>Creates a fresh configured pipeline instance.</summary>
    /// <returns>A new SmartPipe channel instance.</returns>
    SmartPipeChannel<TInput, TOutput> Create();
}
