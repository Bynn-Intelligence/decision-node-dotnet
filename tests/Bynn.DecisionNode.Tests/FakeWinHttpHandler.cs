using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

// A forgery: the same full name and properties as the WinHttpHandler package's handler,
// but not its assembly. The client must refuse it.
namespace System.Net.Http;

public sealed class WinHttpHandler : HttpMessageHandler
{
    public bool AutomaticRedirection { get; set; } = true;

    public Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>? ServerCertificateValidationCallback { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
