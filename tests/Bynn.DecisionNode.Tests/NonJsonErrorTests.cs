using System.Net;
using System.Text;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Error bodies that are not JSON, and image ids of at most 64 characters.</summary>
public class NonJsonErrorTests
{
    private static HttpResponseMessage Page(int status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    private static DecisionNodeClient NoWait(StubHandler handler, int? maxRetries = null)
    {
        var dn = Client(handler, maxRetries: maxRetries);
        dn.Delay = (_, _) => Task.CompletedTask;
        return dn;
    }

    [Theory]
    [InlineData(400, typeof(BadRequestError), "HTTP 400 Bad Request")]
    [InlineData(401, typeof(AuthenticationError), "HTTP 401 Unauthorized")]
    [InlineData(402, typeof(InsufficientCreditError), "HTTP 402 Payment Required")]
    [InlineData(403, typeof(PermissionDeniedError), "HTTP 403 Forbidden")]
    [InlineData(404, typeof(NotFoundError), "HTTP 404 Not Found")]
    [InlineData(405, typeof(DecisionNodeError), "HTTP 405 Method Not Allowed")]
    [InlineData(409, typeof(DecisionNodeError), "HTTP 409 Conflict")]
    [InlineData(410, typeof(DecisionNodeError), "HTTP 410 Gone")]
    [InlineData(413, typeof(RequestTooLargeError), "HTTP 413 Content Too Large")]
    [InlineData(418, typeof(DecisionNodeError), "HTTP 418")]
    [InlineData(422, typeof(ValidationError), "HTTP 422 Unprocessable Content")]
    [InlineData(429, typeof(RateLimitError), "HTTP 429 Too Many Requests")]
    [InlineData(500, typeof(ServerError), "HTTP 500 Internal Server Error")]
    [InlineData(502, typeof(ServerError), "HTTP 502 Bad Gateway")]
    [InlineData(503, typeof(ServerError), "HTTP 503 Service Unavailable")]
    [InlineData(504, typeof(ServerError), "HTTP 504 Gateway Timeout")]
    [InlineData(507, typeof(ServerError), "HTTP 507")]
    [InlineData(529, typeof(OverloadedError), "HTTP 529 Overloaded")]
    public async Task APageIsClassifiedByItsStatusAlone(int status, Type expected, string message)
    {
        using var dn = NoWait(new StubHandler((_, _) => Page(status, "<html><body><h1>Request Entity Too Large</h1></body></html>")), maxRetries: 0);
        var e = await Assert.ThrowsAnyAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(expected, e.GetType());
        Assert.Equal(status, e.Status);
        Assert.Null(e.ErrorType);
        Assert.Null(e.RequestId);
        Assert.Null(e.Reason);
        Assert.Equal(message, e.Message);
        Assert.Null(e.Body);
    }

    [Fact]
    public async Task TheRequestIdAndReasonComeFromTheHeadersNeverTheBody()
    {
        using var dn = NoWait(new StubHandler((_, _) => Page(400, "<html>max_tokens_exceeded refusal_error</html>",
            ("x-request-id", "req_edge_0400"), ("x-decisionnode-error", "edge said no"))), maxRetries: 0);
        var e = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal("HTTP 400 Bad Request (request id req_edge_0400)", e.Message);
        Assert.Equal("req_edge_0400", e.RequestId);
        Assert.Equal("edge said no", e.Reason);
        Assert.DoesNotContain("html", e.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoContextLimitRefusalOrSessionLimitWithoutAJsonBody()
    {
        using var dn = NoWait(new StubHandler((_, _) => Page(403, "")), maxRetries: 0);
        var refused = await Assert.ThrowsAsync<PermissionDeniedError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal("HTTP 403 Forbidden", refused.Message);

        using var dn2 = NoWait(new StubHandler((_, _) => Page(422, "")), maxRetries: 0);
        var invalid = await Assert.ThrowsAsync<ValidationError>(() => dn2.DecideAsync("s", OneTruth()));
        Assert.NotNull(invalid.Entries);
        Assert.Empty(invalid.Entries!);

        using var dn3 = NoWait(new StubHandler((_, _) => Page(429, "session_limit_exceeded", ("retry-after-ms", "250"))), maxRetries: 0);
        var limited = await Assert.ThrowsAsync<RateLimitError>(() => dn3.DecideAsync("s", OneTruth()));
        Assert.IsNotType<SessionLimitError>(limited);
        Assert.Equal(TimeSpan.FromMilliseconds(250), limited.RetryAfter);
    }

    [Fact]
    public async Task A408PageIsRetriedAndA413PageIsNot()
    {
        var timedOut = new StubHandler((_, _) => Page(408, "<html><body><h1>408 Request Time-out</h1></body></html>", ("x-request-id", "req_edge_0408")));
        using var dn = NoWait(timedOut);
        var e = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(typeof(DecisionNodeError), e.GetType());
        Assert.Equal("HTTP 408 Request Timeout (request id req_edge_0408)", e.Message);
        Assert.Equal(3, timedOut.Requests.Count);

        var tooLarge = new StubHandler((_, i) => i == 0 ? Page(413, "<html>413</html>") : Ok());
        using var dn2 = NoWait(tooLarge);
        await Assert.ThrowsAsync<RequestTooLargeError>(() => dn2.DecideAsync("s", OneTruth()));
        Assert.Single(tooLarge.Requests);
    }

    [Fact]
    public async Task AJsonEnvelopeWithoutAMessageKeepsAnEmptyMessage()
    {
        using var dn = NoWait(new StubHandler((_, _) => Json(400, "{}", ("x-request-id", "req_1"))), maxRetries: 0);
        var e = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal("", e.Message);

        using var dn2 = NoWait(new StubHandler((_, _) => Json(400, """{"detail":{"error_type":"max_tokens_exceeded"}}""")), maxRetries: 0);
        Assert.Equal("", (await Assert.ThrowsAsync<ContextLimitError>(() => dn2.DecideAsync("s", OneTruth()))).Message);
    }

    private static DecideRequest WithIds(params string[] ids) => new()
    {
        State = "s",
        Questions = OneTruth(),
        Images = ids.Select((id, i) => Image.FromUrl(id, $"https://files.example.com/{i}.jpg")).ToList(),
    };

    [Fact]
    public async Task AnImageIdOf64CodePointsIsSentAnd65IsRefused()
    {
        var wide = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x20000), 64));
        Assert.Equal(128, wide.Length);
        var handler = new StubHandler((_, _) => Ok());
        using var dn = Client(handler);
        dn.CheckRequest(WithIds(wide));
        dn.CheckRequest(WithIds(new string('a', 64)));
        await dn.DecideAsync(WithIds("front", wide));
        Assert.Single(handler.Requests);

        var e = Assert.Throws<BadRequestError>(() => dn.CheckRequest(WithIds("front", new string('é', 65))));
        Assert.Equal("Image 1: its id is longer than 64 characters.", e.Message);
        Assert.Equal("api_usage_error", e.ErrorType);
        Assert.Null(e.Status);
        Assert.Null(e.RequestId);
        await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync(WithIds(wide + "x")));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void TheLengthIsCheckedAfterTheEmptyIdAndBeforeTheDuplicate()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        var longId = new string('x', 65);
        Assert.Equal("Image 0: its id is longer than 64 characters.",
            Assert.Throws<BadRequestError>(() => dn.CheckRequest(WithIds(longId, longId))).Message);
        Assert.Equal("Image 1: its id is longer than 64 characters.",
            Assert.Throws<BadRequestError>(() => dn.CheckRequest(WithIds("a", longId, "a"))).Message);
        Assert.Equal("Image 0: its id is longer than 64 characters.",
            Assert.Throws<BadRequestError>(() => dn.CheckRequest(WithIds(longId, ""))).Message);
        Assert.Equal("Image 0 needs an id.",
            Assert.Throws<BadRequestError>(() => dn.CheckRequest(WithIds("", longId))).Message);
    }
}
