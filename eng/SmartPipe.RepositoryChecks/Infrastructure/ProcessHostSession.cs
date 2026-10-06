using System.IO.Pipes;

namespace SmartPipe.RepositoryChecks.Infrastructure;

internal sealed class ProcessHostSession : IDisposable
{
    private readonly NamedPipeServerStream _control;
    private readonly TimeSpan _handshakeTimeout;

    public ProcessHostSession(TimeSpan handshakeTimeout)
    {
        _handshakeTimeout = handshakeTimeout;
        PipeName = $"smartpipe-process-host-{Guid.NewGuid():N}";
        Nonce = Guid.NewGuid().ToString("N");
        _control = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public string PipeName { get; }

    public string Nonce { get; }

    public bool StartCommitted { get; private set; }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        await RunHandshakeStepAsync(
                token => _control.WaitForConnectionAsync(token),
                cancellationToken)
            .ConfigureAwait(false);
        var ready = await RunHandshakeStepAsync(
                token => ProcessHostControlProtocol.ReadAsync(_control, Nonce, token),
                cancellationToken)
            .ConfigureAwait(false);
        RequireMessage(ready, ProcessHostControlMessageKind.Ready, allowDetail: false);
    }

    public async Task<bool> SendStartAndWaitForResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Cancellation concurrent with this write must use owned tree termination:
        // the host may receive START even if the controller's write is interrupted.
        StartCommitted = true;
        await ProcessHostControlProtocol.WriteAsync(
                _control,
                Nonce,
                new ProcessHostControlMessage(ProcessHostControlMessageKind.Start),
                cancellationToken)
            .ConfigureAwait(false);

        var result = await RunHandshakeStepAsync(
                token => ProcessHostControlProtocol.ReadAsync(_control, Nonce, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Kind == ProcessHostControlMessageKind.StartFailed && result.Detail is not null)
        {
            return false;
        }

        RequireMessage(result, ProcessHostControlMessageKind.Started, allowDetail: false);
        return true;
    }

    public async Task<int> ReadExitCodeAsync(CancellationToken cancellationToken)
    {
        var exit = await ProcessHostControlProtocol.ReadAsync(
                _control,
                Nonce,
                cancellationToken)
            .ConfigureAwait(false);
        RequireMessage(exit, ProcessHostControlMessageKind.Exit, allowDetail: true);
        if (!int.TryParse(
                exit.Detail,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var exitCode))
        {
            throw new ProcessHostProtocolException("The process-host EXIT payload is invalid.");
        }

        return exitCode;
    }

    public async Task SendTeardownAsync()
    {
        await RunHandshakeStepAsync(
                token => ProcessHostControlProtocol.WriteAsync(
                    _control,
                    Nonce,
                    new ProcessHostControlMessage(ProcessHostControlMessageKind.Teardown),
                    token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async Task<Exception?> TrySendCancelAsync()
    {
        if (StartCommitted || !_control.IsConnected)
        {
            return null;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await ProcessHostControlProtocol.WriteAsync(
                    _control,
                    Nonce,
                    new ProcessHostControlMessage(ProcessHostControlMessageKind.Cancel),
                    deadline.Token)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException
                                           or OperationCanceledException
                                           or ProcessHostProtocolException)
        {
            return exception;
        }
    }

    public void Dispose()
    {
        _control.Dispose();
    }

    private async Task RunHandshakeStepAsync(
        Func<CancellationToken, Task> step,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_handshakeTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            await step(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProcessHostProtocolException(
                "The process-host control handshake timed out.",
                exception);
        }
    }

    private async Task<T> RunHandshakeStepAsync<T>(
        Func<CancellationToken, Task<T>> step,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_handshakeTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await step(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProcessHostProtocolException(
                "The process-host control handshake timed out.",
                exception);
        }
    }

    private static void RequireMessage(
        ProcessHostControlMessage message,
        ProcessHostControlMessageKind expectedKind,
        bool allowDetail)
    {
        if (message.Kind != expectedKind || (!allowDetail && message.Detail is not null))
        {
            throw new ProcessHostProtocolException(
                $"Expected process-host control message {expectedKind}.");
        }
    }
}
