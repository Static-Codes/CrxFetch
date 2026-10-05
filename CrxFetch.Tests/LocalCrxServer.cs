using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrxFetch.Tests;

/// <summary>
/// A local HTTP server for exercising the hosted-link download path offline: direct responses,
/// redirects, multi-hop chains and the failure cases, with no internet and no public proxy.
/// </summary>
internal sealed class LocalCrxServer : IDisposable
{
    private const string TestId = "ojjgnpkioondelmggbekfhllhdaimnho";

    private readonly HttpListener _listener = new();
    private readonly byte[] _package;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public LocalCrxServer()
    {
        _package = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "valid_publisher.crx3"));

        Port = FreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{Port}/");

        // Routes: 
        // direct.crx serves the bytes, 
        // redirect.crx points at it, 
        // hop.crx chains twice, 
        // the rest are failure cases.
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public Uri BaseAddress { get; }

    public static string ExtensionId => TestId;

    public Uri Direct() => new(BaseAddress, "direct.crx");

    public Uri Redirect() => new(BaseAddress, "redirect.crx");

    public Uri MultiHop() => new(BaseAddress, "hop.crx");

    public Uri Missing() => new(BaseAddress, "missing.crx");

    public Uri RedirectWithoutLocation() => new(BaseAddress, "no-location.crx");

    public Uri Loop() => new(BaseAddress, "loop.crx");

    public Uri NotACrx() => new(BaseAddress, "notacrx.crx");

    public Uri Url(string name) => new(BaseAddress, name);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task LoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) {
                return;
            }

            try { Handle(context); }
            catch (HttpListenerException) { return; }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
        var response = context.Response;

        // Cases only describe the response; writing it happens once, below.
        var route = path switch {
            "direct.crx" => new Route(200, ContentType: "application/x-chrome-extension", Payload: _package),
            "notacrx.crx" => new Route(200, ContentType: "application/octet-stream", Payload: Encoding.UTF8.GetBytes("this is not a crx")),
            "empty.crx" => new Route(200, Payload: []),
            "redirect.crx" => new Route(302, Location: "direct.crx"),
            "hop.crx" => new Route(302, Location: "hop2.crx"),
            "hop2.crx" => new Route(302, Location: "direct.crx"),
            "loop.crx" => new Route(302, Location: "loop.crx"),
            "no-location.crx" => new Route(302),
            _ => new Route(404)
        };

        response.StatusCode = route.Status;

        if (route.Location is not null)
        {
            response.Headers.Add("Location", route.Location);
        }

        if (route.ContentType is not null)
        {
            response.ContentType = route.ContentType;
        }

        if (route.Payload is not null)
        {
            response.ContentLength64 = route.Payload.Length;
            response.OutputStream.Write(route.Payload, 0, route.Payload.Length);
        }

        response.Close();
    }

    private readonly record struct Route(int Status, string? Location = null, string? ContentType = null, byte[]? Payload = null);

    public void Dispose()
    {
        _shutdown.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _shutdown.Dispose();
    }
}