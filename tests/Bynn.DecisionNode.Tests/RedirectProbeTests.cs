using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bynn.DecisionNode.Tests;

/// <summary>
/// Handlers that follow a redirect on their own, against two real loopback servers: an
/// origin that answers every request with a 302 to a target, and the target, which records
/// every request it receives. The key must never reach the target.
/// </summary>
public sealed class RedirectProbeTests : IDisposable
{
    private const string Key = "dn_probe_secret";

    private readonly LoopbackServer _target;
    private readonly LoopbackServer _origin;

    public RedirectProbeTests()
    {
        _target = new LoopbackServer(_ => (200, null));
        _origin = new LoopbackServer(_ => (302, _target.Url + "v1/decide"));
    }

    public void Dispose()
    {
        _origin.Dispose();
        _target.Dispose();
    }

    [Fact]
    public async Task TheProbeHandlersDoFollowTheRedirect()
    {
        // The control: used on their own, both probes carry the key to the target, so the
        // tests below would see a leak if the client let one through.
        using (var viaDelegating = new HttpClient(new FollowingDelegatingHandler { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } }))
        {
            await SendWithKey(viaDelegating);
        }
        using (var viaSubclass = new HttpClient(new FollowingHttpClientHandler { AllowAutoRedirect = false }))
        {
            await SendWithKey(viaSubclass);
        }
        Assert.Equal(2, _target.Requests.Count(r => r.Contains("Bearer " + Key, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ADelegatingHandlerThatFollowsA302IsRefusedAndTheTargetSeesNothing()
    {
        var http = new HttpClient(new FollowingDelegatingHandler { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } });
        var e = Assert.Throws<ArgumentException>(() => Make(http));
        Assert.Contains("DelegatingHandler", e.Message);
        Assert.Contains(nameof(DecisionNodeClientOptions.DelegatingHandlersNeverResendRequests), e.Message);
        await Task.Delay(100);
        Assert.Empty(_origin.Requests);
        Assert.Empty(_target.Requests);
    }

    [Fact]
    public async Task ADelegatingHandlerIsRefusedOverEveryKnownTerminalHandler()
    {
        Assert.Throws<ArgumentException>(() => Make(new HttpClient(new FollowingDelegatingHandler { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })));
        Assert.Throws<ArgumentException>(() => Make(new HttpClient(new FollowingDelegatingHandler { InnerHandler = new System.Net.Http.WinHttpHandler { AutomaticRedirection = false } })));
        await Task.Delay(100);
        Assert.Empty(_target.Requests);
    }

    [Fact]
    public async Task AnEmptyHttpClientHandlerSubclassIsRefused()
    {
        var e = Assert.Throws<ArgumentException>(() => Make(new HttpClient(new EmptyHttpClientHandler { AllowAutoRedirect = false })));
        Assert.Contains("subclasses are refused", e.Message);
        Assert.Throws<ArgumentException>(() => Make(new HttpClient(new EmptyHttpClientHandler { AllowAutoRedirect = false }), neverResend: true));
        await Task.Delay(100);
        Assert.Empty(_target.Requests);
    }

    [Fact]
    public async Task AnHttpClientHandlerSubclassThatFollowsA302IsRefusedAndTheTargetSeesNothing()
    {
        Assert.Throws<ArgumentException>(() => Make(new HttpClient(new FollowingHttpClientHandler { AllowAutoRedirect = false })));
        Assert.Throws<ArgumentException>(() => Make(new HttpClient(new FollowingHttpClientHandler { AllowAutoRedirect = false }), neverResend: true));
        await Task.Delay(100);
        Assert.Empty(_origin.Requests);
        Assert.Empty(_target.Requests);
    }

    [Fact]
    public void AnHttpClientSubclassIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Make(new OwnHttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })));
    }

    [Fact]
    public async Task AnExactSocketsHttpHandlerAnswersA302AsAnErrorAndTheTargetSeesNothing()
    {
        using var dn = Make(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }));
        var e = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.DecideAsync("s", Http.OneTruth()));
        Assert.Equal(302, e.Status);
        Assert.Single(_origin.Requests);
        Assert.Empty(_target.Requests);
    }

    [Fact]
    public async Task AnExactHttpClientHandlerAnswersA302AsAnErrorAndTheTargetSeesNothing()
    {
        using var dn = Make(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
        var e = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.DecideAsync("s", Http.OneTruth()));
        Assert.Equal(302, e.Status);
        Assert.Empty(_target.Requests);
    }

    private DecisionNodeClient Make(HttpClient http, bool neverResend = false) =>
        new(new DecisionNodeClientOptions
        {
            ApiKey = Key,
            BaseUrl = _origin.Url.TrimEnd('/'),
            MaxRetries = 0,
            HttpClient = http,
            DelegatingHandlersNeverResendRequests = neverResend,
        });

    private async Task SendWithKey(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _origin.Url + "v1/decide") { Content = new StringContent("{}") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Key);
        using var response = await http.SendAsync(request);
    }

    /// <summary>Re-sends a request, headers included, to the Location of a 3xx.</summary>
    private static async Task<HttpResponseMessage> Follow(HttpRequestMessage request, HttpResponseMessage response, Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    {
        if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is not { } location)
        {
            return response;
        }
        var again = new HttpRequestMessage(request.Method, new Uri(request.RequestUri!, location));
        foreach (var (name, values) in request.Headers.NonValidated)
        {
            again.Headers.TryAddWithoutValidation(name, values);
        }
        if (request.Content is not null)
        {
            again.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync());
        }
        response.Dispose();
        return await send(again);
    }

    private sealed class FollowingDelegatingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await Follow(request, await base.SendAsync(request, cancellationToken), r => base.SendAsync(r, cancellationToken));
    }

    private sealed class FollowingHttpClientHandler : HttpClientHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await Follow(request, await base.SendAsync(request, cancellationToken), r => base.SendAsync(r, cancellationToken));
    }

    private sealed class EmptyHttpClientHandler : HttpClientHandler
    {
    }

    private sealed class OwnHttpClient : HttpClient
    {
        public OwnHttpClient(HttpMessageHandler handler) : base(handler)
        {
        }
    }

    /// <summary>A minimal HTTP/1.1 server on 127.0.0.1 that records each request's head.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, (int Status, string? Location)> _answer;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly List<string> _requests = new();

        public LoopbackServer(Func<string, (int Status, string? Location)> answer)
        {
            _answer = answer;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Task.Run(AcceptLoop);
        }

        public string Url { get; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[65536];
                var head = new StringBuilder();
                int read;
                while (!head.ToString().Contains("\r\n\r\n") && (read = await stream.ReadAsync(buffer)) > 0)
                {
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var text = head.ToString();
                lock (_gate)
                {
                    _requests.Add(text);
                }
                var (status, location) = _answer(text);
                var reply = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n" +
                    (location is null ? "" : $"Location: {location}\r\n") + "\r\n{}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
