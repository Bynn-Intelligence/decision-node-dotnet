namespace Bynn.DecisionNode;

/// <summary>
/// Settings for a <see cref="DecisionNodeClient"/>. Every one is optional. The key, the
/// base URL and the model fall back to <c>DECISIONNODE_API_KEY</c>,
/// <c>DECISIONNODE_BASE_URL</c> and <c>DECISIONNODE_MODEL</c>, then to the defaults.
/// </summary>
public sealed class DecisionNodeClientOptions
{
    /// <summary>The API key. Falls back to <c>DECISIONNODE_API_KEY</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The API's root URL, without <c>/v1</c>. Falls back to <c>DECISIONNODE_BASE_URL</c>, then
    /// <c>https://api.decisionnode.com</c>. Must be <c>https</c>; plain <c>http</c> is accepted
    /// only for <c>localhost</c>, <c>127.0.0.1</c> and <c>::1</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>The model for every call that names none. Falls back to <c>DECISIONNODE_MODEL</c>, then <c>decisionnode-latest</c>.</summary>
    public string? Model { get; set; }

    /// <summary>The time allowed for each attempt, from sending to the last byte of the body. Above zero; default 10 seconds.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Retries after the first attempt. 0 or more; default 2.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Headers added to every request, for example <c>x-decisionnode-safety-body: true</c>.</summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// The <see cref="System.Net.Http.HttpClient"/> to send with. When <c>null</c> the client
    /// creates and owns one. A client you pass in is not disposed, and its
    /// <c>BaseAddress</c> and <c>Timeout</c> are not used. It must never follow a redirect, so
    /// it is accepted only when its handler is exactly a <see cref="SocketsHttpHandler"/> or
    /// <see cref="HttpClientHandler"/> with <c>AllowAutoRedirect = false</c> (on by default in
    /// .NET), or a <c>WinHttpHandler</c> with <c>AutomaticRedirection = false</c>. Subclasses
    /// of those handlers, any other handler, and any <see cref="DelegatingHandler"/> in the
    /// chain (see <see cref="DelegatingHandlersNeverResendRequests"/>) are refused with an
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Your statement that every <see cref="DelegatingHandler"/> in <see cref="HttpClient"/>'s
    /// handler chain passes each request on once, unchanged, to its inner handler, and never
    /// sends a request again: not after a redirect, not to another URL, not as a retry. The
    /// library cannot see inside a delegating handler, and one that re-sends a request sends
    /// your key with it, so a chain holding one is refused unless this is <c>true</c>. The
    /// terminal handler is checked either way. Default <c>false</c>.
    /// </summary>
    public bool DelegatingHandlersNeverResendRequests { get; set; }

    /// <summary>
    /// Receives one line per attempt: the method, path, status, request id and timing.
    /// State, questions and keys are never logged. Off when <c>null</c>.
    /// </summary>
    public Action<string>? DebugLog { get; set; }
}
