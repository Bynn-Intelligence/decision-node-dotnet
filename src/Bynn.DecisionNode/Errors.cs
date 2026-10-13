using System.Text.Json.Nodes;

namespace Bynn.DecisionNode;

/// <summary>
/// The base of every error this library throws. The class tells what happened; the
/// fields carry what the server said. A status without a class of its own (a 3xx, 405,
/// 409, 408 once its retries are spent) is this class itself, with <see cref="Status"/> set.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is the server's message exactly: <c>detail.message</c>,
/// or <c>detail</c> itself when it is a string, and empty when the server sent neither.
/// When no response arrived it is the library's description of what went wrong.
/// </remarks>
public class DecisionNodeError : Exception
{
    public DecisionNodeError(string message)
        : base(message)
    {
    }

    public DecisionNodeError(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal DecisionNodeError(ErrorDetails details)
        : base(details.Message ?? string.Empty, details.Inner)
    {
        Status = details.Status;
        ErrorType = details.ErrorType;
        RequestId = details.RequestId;
        Reason = details.Reason;
        Body = details.Body;
        Boosters = details.Boosters ?? BoosterStatus.ParseHeader(null);
        Details = details.Details;
    }

    /// <summary>The HTTP status, or <c>null</c> when no response arrived.</summary>
    public int? Status { get; }

    /// <summary>The server's <c>detail.error_type</c>, for example <c>max_tokens_exceeded</c>.</summary>
    public string? ErrorType { get; }

    /// <summary>The <c>x-request-id</c> response header. Quote it when you contact support.</summary>
    public string? RequestId { get; }

    /// <summary>
    /// The server's explanation in words, from the <c>x-decisionnode-error</c> header, for
    /// example "question 'q': a score takes up to 10 levels, got 11".
    /// </summary>
    public string? Reason { get; }

    /// <summary>The parsed response body, when it was JSON.</summary>
    public JsonNode? Body { get; }

    /// <summary>
    /// Each booster the request named, with its status and time, from the
    /// <c>x-decisionnode-boosters</c> response header, in the request's order, so a caller can
    /// see which required booster failed. Empty when the header is absent or no response arrived.
    /// </summary>
    public IReadOnlyDictionary<string, BoosterStatus> Boosters { get; } = BoosterStatus.ParseHeader(null);

    /// <summary>
    /// The server's <c>detail.details</c>, more about the error, for example
    /// <c>{"reason": "enrollment_unpriced"}</c>; <c>null</c> when the body has none.
    /// </summary>
    public JsonObject? Details { get; }
}

/// <summary>400: the request is not valid for this model.</summary>
public class BadRequestError : DecisionNodeError
{
    internal BadRequestError(ErrorDetails d) : base(d) { }
}

/// <summary>400 with <c>error_type</c> <c>max_tokens_exceeded</c>: the request is longer than the model takes.</summary>
public class ContextLimitError : BadRequestError
{
    internal ContextLimitError(ErrorDetails d) : base(d) { }
}

/// <summary>401, or no API key was configured (then nothing is sent).</summary>
public class AuthenticationError : DecisionNodeError
{
    internal AuthenticationError(ErrorDetails d) : base(d) { }
}

/// <summary>402: the account has no credit left.</summary>
public class InsufficientCreditError : DecisionNodeError
{
    internal InsufficientCreditError(ErrorDetails d) : base(d) { }
}

/// <summary>403 for any reason other than a refusal.</summary>
public class PermissionDeniedError : DecisionNodeError
{
    internal PermissionDeniedError(ErrorDetails d) : base(d) { }
}

/// <summary>403 with <c>refusal_error</c>: the request was refused under the usage policy. Never retried.</summary>
public class RefusalError : DecisionNodeError
{
    internal RefusalError(ErrorDetails d, string? category, Safety? safety)
        : base(d)
    {
        Category = category;
        Safety = safety;
    }

    /// <summary>The category that was refused: <c>harm</c> or <c>self_harm</c>.</summary>
    public string? Category { get; }

    /// <summary>The safety check's result for the refused request.</summary>
    public Safety? Safety { get; }
}

/// <summary>
/// 413: the request body is over the API's limit of
/// <see cref="DecisionNodeClient.MaxBodyBytes"/> (26,214,400 bytes, 25 MiB). Also thrown before
/// anything is sent, with no status and no request id, for a body the client can tell is over
/// it. Never retried: send large images as uploads and name them by upload id.
/// </summary>
public class RequestTooLargeError : DecisionNodeError
{
    internal RequestTooLargeError(ErrorDetails d) : base(d) { }
}

/// <summary>404.</summary>
public class NotFoundError : DecisionNodeError
{
    internal NotFoundError(ErrorDetails d) : base(d) { }
}

/// <summary>422: the body does not have the shape the API takes.</summary>
public class ValidationError : DecisionNodeError
{
    internal ValidationError(ErrorDetails d, JsonArray? entries)
        : base(d)
    {
        Entries = entries;
    }

    /// <summary>The server's list of validation entries (<c>type</c>, <c>loc</c>, <c>msg</c>, <c>input</c>).</summary>
    public JsonArray? Entries { get; }
}

/// <summary>429: too many requests. Retried by default (except <see cref="SessionLimitError"/>).</summary>
public class RateLimitError : DecisionNodeError
{
    internal RateLimitError(ErrorDetails d, TimeSpan? retryAfter)
        : base(d)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the server asked to wait, from <c>retry-after-ms</c> or <c>Retry-After</c>; at most 60 seconds.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// 429 with <c>error_type</c> <c>session_limit_exceeded</c>: the key already holds as many
/// open sessions as it may (<c>sessions.max_per_key</c> of <c>GET /v1/models</c>). Never
/// retried: end a session first. A <see cref="RateLimitError"/>, so code that handles rate
/// limits handles it too.
/// </summary>
public class SessionLimitError : RateLimitError
{
    internal SessionLimitError(ErrorDetails d, TimeSpan? retryAfter)
        : base(d, retryAfter)
    {
    }
}

/// <summary>529: the service is overloaded. Retried by default.</summary>
public class OverloadedError : DecisionNodeError
{
    internal OverloadedError(ErrorDetails d, TimeSpan? retryAfter)
        : base(d)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the server asked to wait, from <c>retry-after-ms</c> or <c>Retry-After</c>.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Any other 5xx, or a successful status with a body that is not JSON. Retried by default.</summary>
public class ServerError : DecisionNodeError
{
    internal ServerError(ErrorDetails d) : base(d) { }
}

/// <summary>No response arrived: the connection failed or was dropped. Retried by default.</summary>
public class ConnectionError : DecisionNodeError
{
    internal ConnectionError(ErrorDetails d) : base(d) { }
}

/// <summary>An attempt took longer than the client's timeout. Retried by default.</summary>
public class TimeoutError : DecisionNodeError
{
    internal TimeoutError(ErrorDetails d) : base(d) { }
}

/// <summary>Everything a <see cref="DecisionNodeError"/> carries, gathered before choosing its class.</summary>
internal sealed record ErrorDetails
{
    public int? Status { get; init; }
    public string? ErrorType { get; init; }
    public string? Message { get; init; }
    public string? RequestId { get; init; }
    public string? Reason { get; init; }
    public JsonNode? Body { get; init; }
    public IReadOnlyDictionary<string, BoosterStatus>? Boosters { get; init; }
    public JsonObject? Details { get; init; }
    public Exception? Inner { get; init; }
}
