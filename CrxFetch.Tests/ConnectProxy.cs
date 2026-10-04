using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrxFetch.Tests;

/// <summary>
/// A minimal HTTP proxy that speaks CONNECT tunnelling, so proxy handling can be exercised
/// deterministically instead of depending on whatever free proxies happen to be alive.
/// </summary>
/// <remarks>
/// Every tunnelled target is recorded in <see cref="ConnectTargets"/>, which is how a test
/// proves the request really went through the proxy rather than around it.
/// </remarks>
internal sealed class ConnectProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<string> _connectTargets = [];
    private readonly Lock _gate = new();
    private Task? _acceptLoop;

    public ConnectProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>The proxy endpoint to configure the library with.</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}";

    /// <summary>Authority targets this proxy has been asked to tunnel to, in order.</summary>
    public IReadOnlyList<string> ConnectTargets
    {
        get
        {
            lock (_gate)
            {
                return _connectTargets.ToArray();
            }
        }
    }

    public void Start()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var clientStream = client.GetStream())
            {
                var requestLine = await ReadRequestHeadAsync(clientStream);
                if (requestLine is null)
                {
                    return;
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(clientStream, "HTTP/1.1 405 Method Not Allowed\r\n\r\n");
                    return;
                }

                lock (_gate)
                {
                    _connectTargets.Add(parts[1]);
                }

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(parts[1].Split(':')[0], int.Parse(parts[1].Split(':')[1]));

                await WriteAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n");

                using var upstreamStream = upstream.GetStream();
                var toUpstream = clientStream.CopyToAsync(upstreamStream);
                var toClient = upstreamStream.CopyToAsync(clientStream);

                await Task.WhenAny(toUpstream, toClient);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or FormatException or ArgumentException)
        {
            // A proxy that drops a connection is exactly what some real proxies do; the test
            // asserts on what was tunnelled, not on the proxy being well behaved.
        }
    }

    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];

        while (builder.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return null;
            }

            builder.Append((char)buffer[0]);
            if (builder.Length >= 4 &&
                builder[^4] == '\r' && builder[^3] == '\n' &&
                builder[^2] == '\r' && builder[^1] == '\n')
            {
                break;
            }
        }

        var firstNewline = builder.ToString().IndexOf("\r\n", StringComparison.Ordinal);
        return firstNewline < 0 ? null : builder.ToString()[..firstNewline];
    }

    private static async Task WriteAsync(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }
}