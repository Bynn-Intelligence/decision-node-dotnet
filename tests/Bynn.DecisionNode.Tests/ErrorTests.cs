using System.Net;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class ErrorTests
{
    private static async Task<DecisionNodeError> Fail(int status, string body, params (string, string)[] headers)
    {
        var stub = new StubHandler((_, _) => Json(status, body, headers));
        using var dn = Client(stub, maxRetries: 0);
        var e = await Assert.ThrowsAnyAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Single(stub.Requests);
        return e;
    }

    [Theory]
    [InlineData(400, typeof(BadRequestError))]
    [InlineData(401, typeof(AuthenticationError))]
    [InlineData(402, typeof(InsufficientCreditError))]
    [InlineData(403, typeof(PermissionDeniedError))]
    [InlineData(404, typeof(NotFoundError))]
    [InlineData(301, typeof(DecisionNodeError))]
    [InlineData(302, typeof(DecisionNodeError))]
    [InlineData(307, typeof(DecisionNodeError))]
    [InlineData(405, typeof(DecisionNodeError))]
    [InlineData(409, typeof(DecisionNodeError))]
    [InlineData(418, typeof(DecisionNodeError))]
    [InlineData(422, typeof(ValidationError))]
    [InlineData(429, typeof(RateLimitError))]
    [InlineData(500, typeof(ServerError))]
    [InlineData(503, typeof(ServerError))]
    [InlineData(529, typeof(OverloadedError))]
    public async Task StatusSelectsTheClass(int status, Type expected)
    {
        var e = await Fail(status, """{"detail":{"error_type":"x","message":"m"}}""", ("x-request-id", "req_e"));
        Assert.IsType(expected, e);
        Assert.Equal(status, e.Status);
        Assert.Equal("x", e.ErrorType);
        Assert.Equal("m", e.Message);
        Assert.Equal("req_e", e.RequestId);
    }

    [Fact]
    public async Task MaxTokensExceededIsAContextLimitError()
    {
        var e = await Fail(400, """{"detail":{"error_type":"max_tokens_exceeded"}}""", ("x-decisionnode-error", "the request is 10604 tokens"));
        var c = Assert.IsType<ContextLimitError>(e);
        Assert.IsAssignableFrom<BadRequestError>(c);
        Assert.Equal(string.Empty, c.Message);
        Assert.Equal("the request is 10604 tokens", c.Reason);
    }

    [Fact]
    public async Task StringDetailIsTheMessage()
    {
        var e = await Fail(400, """{"detail":"Too many score levels."}""");
        Assert.Null(e.ErrorType);
        Assert.Equal("Too many score levels.", e.Message);
    }

    [Fact]
    public async Task RefusalCarriesCategoryAndSafety()
    {
        var e = await Fail(403, """{"detail":{"error_type":"refusal_error","message":"Refused.","category":"harm"}}""",
            ("x-decisionnode-safety", "harm=0.964;blocked, self_harm=0.002"));
        var r = Assert.IsType<RefusalError>(e);
        Assert.Equal("harm", r.Category);
        Assert.Equal(new[] { "harm" }, r.Safety!.Flagged);
        Assert.Equal(0.964, r.Safety.Harm);
    }

    [Fact]
    public async Task ValidationErrorCarriesEntries()
    {
        var e = await Fail(422, """{"detail":[{"type":"missing","loc":["body","questions"],"msg":"Field required"}]}""");
        var v = Assert.IsType<ValidationError>(e);
        Assert.Equal("missing", (string?)v.Entries![0]!["type"]);
        Assert.Equal(string.Empty, v.Message);
    }

    [Fact]
    public async Task RetryAfterMsWinsOverRetryAfter()
    {
        var e = await Fail(429, """{"detail":{"error_type":"rate_limit_error"}}""", ("retry-after", "2"), ("retry-after-ms", "1500"));
        Assert.Equal(TimeSpan.FromSeconds(1.5), Assert.IsType<RateLimitError>(e).RetryAfter);
        var o = await Fail(529, """{"detail":{}}""", ("Retry-After", "3"));
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<OverloadedError>(o).RetryAfter);
        var none = await Fail(529, """{"detail":{}}""");
        Assert.Null(Assert.IsType<OverloadedError>(none).RetryAfter);
    }

    [Fact]
    public async Task NonJsonErrorBodyStillMapsByStatus()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>Bad gateway</html>") });
        using var dn = Client(stub, maxRetries: 0);
        var e = await Assert.ThrowsAsync<ServerError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(502, e.Status);
        Assert.Null(e.ErrorType);
    }

    [Fact]
    public async Task MessageIsTheServersMessageAndReasonTheHeader()
    {
        var e = await Fail(400, """{"detail":{"error_type":"invalid_request_error","message":"A score question takes 1 to 10 levels."}}""",
            ("x-decisionnode-error", "question 'q': a score takes up to 10 levels, got 11"));
        Assert.Equal("A score question takes 1 to 10 levels.", e.Message);
        Assert.Equal("question 'q': a score takes up to 10 levels, got 11", e.Reason);
    }

    [Fact]
    public async Task SuccessWithANonJsonBodyIsAServerError()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });
        using var dn = Client(stub, maxRetries: 0);
        var e = await Assert.ThrowsAsync<ServerError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(200, e.Status);
    }

    [Fact]
    public async Task RetryAfterIsCappedAtSixtySeconds()
    {
        var e = await Fail(429, """{"detail":{}}""", ("retry-after-ms", "600000"));
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.IsType<RateLimitError>(e).RetryAfter);
        var o = await Fail(529, """{"detail":{}}""", ("Retry-After", "3600"));
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.IsType<OverloadedError>(o).RetryAfter);
    }

    [Fact]
    public void RetryWaitReadsHttpDatesAndCapsThem()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), DecisionNodeClient.RetryWait(n => n == "retry-after" ? "Wed, 07 Oct 2026 12:00:30 GMT" : null, now));
        Assert.Equal(TimeSpan.FromSeconds(60), DecisionNodeClient.RetryWait(n => n == "retry-after" ? "Wed, 07 Oct 2026 13:00:00 GMT" : null, now));
        Assert.Equal(TimeSpan.Zero, DecisionNodeClient.RetryWait(n => n == "retry-after" ? "Wed, 07 Oct 2026 11:00:00 GMT" : null, now));
    }

    [Fact]
    public void RetryWaitReadsHttpDates()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        var wait = DecisionNodeClient.RetryWait(n => n == "retry-after" ? at : null);
        Assert.InRange(wait!.Value.TotalSeconds, 25, 31);
    }
}
