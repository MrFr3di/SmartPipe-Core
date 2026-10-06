#nullable enable

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Combines the run cancellation token with the caller's enumeration token.</summary>
internal static class EfCoreCancellation
{
    /// <summary>Creates a linked token source only when both tokens can be cancelled.</summary>
    /// <param name="activationToken">The run activation token.</param>
    /// <param name="callerToken">The caller's token.</param>
    /// <param name="linked">The token that represents both inputs.</param>
    /// <returns>A disposable linked source, or <see langword="null"/> when no linking is needed.</returns>
    internal static IDisposable? CreateLinked(
        CancellationToken activationToken,
        CancellationToken callerToken,
        out CancellationToken linked)
    {
        if (!activationToken.CanBeCanceled || activationToken == callerToken)
        {
            linked = callerToken;
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(activationToken, callerToken);
        linked = source.Token;
        return source;
    }
}
