using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

sealed record LoopbackRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

sealed record LoopbackResponse(int StatusCode, string Body, string ContentType = "text/plain; charset=utf-8");

/// <summary>A minimal HTTP/1.1 server on 127.0.0.1 that answers one request per connection.</summary>
sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<LoopbackRequest, LoopbackResponse> _respond;
    private readonly List<LoopbackRequest> _requests = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public LoopbackHttpServer(Func<LoopbackRequest, LoopbackResponse> respond)
    {
        _respond = respond;
        _listener.Start();
        BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = AcceptAsync();
    }

    public Uri BaseAddress { get; }

    public IReadOnlyList<LoopbackRequest> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToArray();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (_stop.IsCancellationRequested
                && exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stop() may surface as cancellation or as a socket error; either ends the loop.
                return;
            }

            using (connection)
            {
                try
                {
                    await HandleAsync(connection.GetStream(), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var reader = new AsciiReader(stream, cancellationToken);
        var requestLine = (await reader.ReadLineAsync().ConfigureAwait(false)).Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var line = await reader.ReadLineAsync().ConfigureAwait(false); line.Length != 0; line = await reader.ReadLineAsync().ConfigureAwait(false))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        byte[] body;
        if (headers.TryGetValue("Content-Length", out var length))
        {
            body = await reader.ReadExactAsync(int.Parse(length, CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var chunks = new MemoryStream();
            while (true)
            {
                var size = int.Parse(await reader.ReadLineAsync().ConfigureAwait(false), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    await reader.ReadLineAsync().ConfigureAwait(false);
                    break;
                }

                chunks.Write(await reader.ReadExactAsync(size).ConfigureAwait(false));
                await reader.ReadLineAsync().ConfigureAwait(false);
            }

            body = chunks.ToArray();
        }
        else
        {
            body = [];
        }

        var request = new LoopbackRequest(requestLine[0], requestLine[1], headers, Encoding.UTF8.GetString(body));
        lock (_requests)
            _requests.Add(request);

        var response = _respond(request);
        var payload = Encoding.UTF8.GetBytes(response.Body);
        var head = $"HTTP/1.1 {response.StatusCode} Loopback\r\nContent-Type: {response.ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class AsciiReader(Stream stream, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public async Task<string> ReadLineAsync()
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_start == _end && !await FillAsync().ConfigureAwait(false))
                    throw new IOException("The connection closed inside an HTTP line.");

                var value = (char)_buffer[_start++];
                if (value == '\n')
                    return line.ToString().TrimEnd('\r');
                line.Append(value);
            }
        }

        public async Task<byte[]> ReadExactAsync(int count)
        {
            var result = new byte[count];
            var written = 0;
            while (written < count)
            {
                if (_start == _end && !await FillAsync().ConfigureAwait(false))
                    throw new IOException("The connection closed inside an HTTP body.");

                var take = Math.Min(count - written, _end - _start);
                Array.Copy(_buffer, _start, result, written, take);
                _start += take;
                written += take;
            }

            return result;
        }

        private async Task<bool> FillAsync()
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            return _end > 0;
        }
    }
}
