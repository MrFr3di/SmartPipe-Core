#nullable enable

namespace SmartPipe.Extensions.Dapper.Internal;

/// <summary>Combines the activation token of a run with the token of one requested operation.</summary>
internal static class DapperCancellation
{
    /// <summary>
    /// Returns a linked source when both tokens can be cancelled independently, or <see langword="null"/>
    /// when <paramref name="effective"/> already carries the single token the caller must honour.
    /// </summary>
    internal static CancellationTokenSource? CreateLinked(
        CancellationToken activation,
        CancellationToken requested,
        out CancellationToken effective)
    {
        if (!activation.CanBeCanceled)
        {
            effective = requested;
            return null;
        }

        if (!requested.CanBeCanceled || requested == activation)
        {
            effective = activation;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(activation, requested);
        effective = linked.Token;
        return linked;
    }
}
