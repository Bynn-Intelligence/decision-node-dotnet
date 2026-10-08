using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

public sealed partial class DecisionNodeClient
{
    /// <summary>
    /// Opens a session (<c>POST /v1/sessions</c>): a state and questions held by the server,
    /// answered again for every frame you send on its WebSocket. A model is always sent. A
    /// <c>429</c> <c>session_limit_exceeded</c> is never retried and raises
    /// <see cref="SessionLimitError"/>.
    /// </summary>
    /// <param name="state">The session's standing context; <c>null</c> leaves it out.</param>
    /// <param name="questions">The questions every frame is answered with. At least one.</param>
    /// <param name="instructions">Instructions for the whole session; <c>null</c> leaves them out.</param>
    /// <param name="model">The model; the client's model when <c>null</c> or empty.</param>
    /// <param name="window">How many recent frames each answer sees; the server's default when <c>null</c>.</param>
    /// <param name="ttlSeconds">The session's lifetime; the server's default when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call, retries included.</param>
    public async Task<Session> OpenSessionAsync(
        object? state,
        IReadOnlyDictionary<string, Question> questions,
        object? instructions = null,
        string? model = null,
        int? window = null,
        int? ttlSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var qs = QuestionMap(questions, nameof(questions));
        var body = new JsonObject { ["model"] = NonEmpty(model) ?? Model };
        if (instructions is not null)
        {
            body["instructions"] = JsonValues.ToNode(instructions, "instructions");
        }
        if (state is not null)
        {
            body["state"] = JsonValues.ToNode(state, "the state");
        }
        body["questions"] = qs;
        if (window is { } w)
        {
            body["window"] = w;
        }
        if (ttlSeconds is { } ttl)
        {
            body["ttl_seconds"] = ttl;
        }
        LocalChecks.SessionOpen(body, Held(model));
        var (parsed, result) = await SendJsonAsync(HttpMethod.Post, "/v1/sessions", JsonValues.ToUtf8(body), JsonContentType, RetryPolicy.SessionOpen, cancellationToken).ConfigureAwait(false);
        if (parsed is not JsonObject raw || JsonValues.AsString(raw["session_id"]) is null || JsonValues.AsString(raw["stream_url"]) is null)
        {
            throw new ServerError(new ErrorDetails
            {
                Status = result.Status,
                RequestId = result.Header("x-request-id"),
                Message = "the response of POST /v1/sessions has no session_id or stream_url",
                Body = parsed,
            });
        }
        return new Session(this, raw);
    }

    /// <summary>Opens a session described by <paramref name="options"/>.</summary>
    public Task<Session> OpenSessionAsync(SessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenSessionAsync(options.State, options.Questions, options.Instructions, options.Model, options.Window, options.TtlSeconds, cancellationToken);
    }

    /// <summary>
    /// Keeps one feed of readings answered for as long as it runs: opens a session, connects,
    /// sends each reading as a frame (latest wins on the server, so a fast feed gets the newest
    /// answer, never a backlog) and passes every <see cref="Reply"/> and <see cref="FrameError"/>
    /// to <paramref name="onReply"/> as soon as it arrives. A session that ends with a reopen
    /// code is opened again: <c>4410</c> at once, <c>4408</c> on the next reading, the others on
    /// the next reading after a backoff of 1 to 30 seconds with jitter (reset after a session
    /// that answered frames). On <c>1013</c> it connects to the same session again after the
    /// backoff. Any other close code stops it.
    /// </summary>
    /// <param name="body">The session to open, and to open again after a reopen code.</param>
    /// <param name="feed">The readings; each is sent as one frame.</param>
    /// <param name="onReply">Called with each reply and frame error, one at a time, in order.</param>
    /// <param name="maxSessions">The most sessions it opens in all; it returns when that is reached.</param>
    /// <param name="cancellationToken">Stops supervising.</param>
    /// <returns>
    /// When the feed ends: after the outstanding replies (up to 3 seconds) and ending the
    /// session, the last <see cref="SessionClosed"/> seen, or <c>null</c>. When a close code
    /// stops it: that <see cref="SessionClosed"/>.
    /// </returns>
    public async Task<SessionClosed?> SuperviseAsync(
        SessionOptions body,
        IAsyncEnumerable<SessionFrame> feed,
        Func<SessionEvent, ValueTask> onReply,
        int maxSessions = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(onReply);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        var supervisor = new Supervisor(this, body, onReply, maxSessions, cancellationToken);
        try
        {
            return await supervisor.RunAsync(feed).ConfigureAwait(false);
        }
        finally
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>As the other overload, with a synchronous callback.</summary>
    public Task<SessionClosed?> SuperviseAsync(
        SessionOptions body,
        IAsyncEnumerable<SessionFrame> feed,
        Action<SessionEvent> onReply,
        int maxSessions = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onReply);
        return SuperviseAsync(body, feed, ev =>
        {
            onReply(ev);
            return ValueTask.CompletedTask;
        }, maxSessions, cancellationToken);
    }

    /// <summary>The handshake headers: the key, the User-Agent and the client's own headers.</summary>
    internal IEnumerable<KeyValuePair<string, string>> HandshakeHeaders()
    {
        yield return new("Authorization", "Bearer " + _apiKey);
        yield return new("User-Agent", UserAgent);
        foreach (var header in _headers)
        {
            yield return header;
        }
    }

    internal string? ApiKey => _apiKey;

    internal HttpMessageInvoker Http => _http;
}

/// <summary>The state of one <see cref="DecisionNodeClient.SuperviseAsync(SessionOptions, IAsyncEnumerable{SessionFrame}, Func{SessionEvent, ValueTask}, int, CancellationToken)"/> call.</summary>
internal sealed class Supervisor : IAsyncDisposable
{
    private static readonly TimeSpan Outstanding = TimeSpan.FromSeconds(3);

    private readonly DecisionNodeClient _client;
    private readonly SessionOptions _body;
    private readonly Func<SessionEvent, ValueTask> _onReply;
    private readonly int _maxSessions;
    private readonly CancellationToken _ct;
    private readonly object _gate = new();
    private Session? _session;
    private Task? _pump;
    private CancellationTokenSource? _pumpStop;
    private Task<Session>? _opening;
    private SessionClosed? _last;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _feedEnded;
    private int _failures;
    private int _opened;
    private int _answered;
    private int _pending;

    internal Supervisor(DecisionNodeClient client, SessionOptions body, Func<SessionEvent, ValueTask> onReply, int maxSessions, CancellationToken ct)
    {
        _client = client;
        _body = body;
        _onReply = onReply;
        _maxSessions = maxSessions;
        _ct = ct;
        _idle.TrySetResult();
    }

    internal async Task<SessionClosed?> RunAsync(IAsyncEnumerable<SessionFrame> feed)
    {
        await foreach (var reading in feed.WithCancellation(_ct).ConfigureAwait(false))
        {
            for (var tries = 0; ; tries++)
            {
                var session = await ReadyAsync().ConfigureAwait(false);
                if (session is null)
                {
                    return _last;
                }
                lock (_gate)
                {
                    if (_pending++ == 0)
                    {
                        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                try
                {
                    await session.SendAsync(reading, _ct).ConfigureAwait(false);
                    break;
                }
                catch (ConnectionError) when (tries == 0)
                {
                    // The socket closed under the send: let the pump record why, then send
                    // this reading on the session that follows.
                    Settle(1, 0);
                    if (_pump is { } pump)
                    {
                        await pump.ConfigureAwait(false);
                    }
                }
            }
        }
        _feedEnded = true;
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }
        try
        {
            await idle.WaitAsync(Outstanding, _ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Replies still owed after 3 seconds are not waited for.
        }
        await StopPumpAsync().ConfigureAwait(false);
        if (_session is { IsConnected: true } open)
        {
            await open.EndAsync(_ct).ConfigureAwait(false);
        }
        return _last;
    }

    /// <summary>A connected session for the next reading, or <c>null</c> when a close code stops supervising.</summary>
    private async Task<Session?> ReadyAsync()
    {
        while (true)
        {
            if (_session is { IsConnected: true } connected)
            {
                return connected;
            }
            if (_pump is { } pump)
            {
                await pump.ConfigureAwait(false);
                _pump = null;
            }
            if (_opening is { } opening)
            {
                _opening = null;
                await Retire().ConfigureAwait(false);
                _session = await opening.ConfigureAwait(false);
                _last = null;
                StartPump(_session);
                continue;
            }
            var last = _last;
            var keep = last?.Code == 1013 && _session is not null;
            if (last is not null && !last.Reopen && !keep)
            {
                return null;
            }
            if (!keep && _opened >= _maxSessions)
            {
                return null;
            }
            if (last is not null && last.Code is not (4408 or 4410))
            {
                if (_answered > 0)
                {
                    _failures = 0;
                }
                var seconds = Math.Min(30.0, Math.Pow(2, _failures)) * (0.5 + (Random.Shared.NextDouble() * 0.5));
                await _client.Delay(TimeSpan.FromSeconds(seconds), _ct).ConfigureAwait(false);
                _failures++;
            }
            if (keep)
            {
                // 1013: the session is kept; connect to it again.
                await _session!.DisposeAsync().ConfigureAwait(false);
                await _session.Reopened().ConnectAsync(_ct).ConfigureAwait(false);
            }
            else
            {
                await Retire().ConfigureAwait(false);
                _session = await OpenAsync().ConfigureAwait(false);
            }
            _last = null;
            StartPump(_session);
        }
    }

    private async Task<Session> OpenAsync()
    {
        _opened++;
        _answered = 0;
        var session = await _client.OpenSessionAsync(_body, _ct).ConfigureAwait(false);
        try
        {
            await session.ConnectAsync(_ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    private async Task Retire()
    {
        if (_session is { } old)
        {
            _session = null;
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void StartPump(Session session)
    {
        _pumpStop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var stop = _pumpStop.Token;
        _pump = Task.Run(() => PumpAsync(session, stop));
    }

    /// <summary>Delivers a session's events as they arrive, until it closes.</summary>
    private async Task PumpAsync(Session session, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                var ev = await session.ReadAsync(stop).ConfigureAwait(false);
                if (ev is SessionClosed closed)
                {
                    _last = closed;
                    if (closed.Code == 4410 && !_feedEnded && _opened < _maxSessions)
                    {
                        // The session's time is up: open the next one at once, ready for the next reading.
                        _opening = OpenAsync();
                    }
                    Settle(int.MaxValue, 0);
                    return;
                }
                if (ev is Reply)
                {
                    Interlocked.Increment(ref _answered);
                }
                await _onReply(ev).ConfigureAwait(false);
                Settle(1, (ev as Reply)?.Dropped?.Count ?? 0);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>Counts frames that ended (answered, failed, or dropped by latest wins).</summary>
    private void Settle(int frames, int dropped)
    {
        lock (_gate)
        {
            _pending = frames == int.MaxValue ? 0 : Math.Max(0, _pending - frames - dropped);
            if (_pending == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private async Task StopPumpAsync()
    {
        _pumpStop?.Cancel();
        if (_pump is { } pump)
        {
            await pump.ConfigureAwait(false);
            _pump = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopPumpAsync().ConfigureAwait(false);
        _pumpStop?.Dispose();
        if (_opening is { } opening)
        {
            // Opened at once, never used: end it so it does not hold a session slot.
            _opening = null;
            try
            {
                var unused = await opening.ConfigureAwait(false);
                await unused.EndAsync(CancellationToken.None).ConfigureAwait(false);
                await unused.DisposeAsync().ConfigureAwait(false);
            }
            catch (DecisionNodeError)
            {
            }
        }
        await Retire().ConfigureAwait(false);
    }
}

/// <summary>The body of <see cref="DecisionNodeClient.OpenSessionAsync(SessionOptions, CancellationToken)"/>.</summary>
public sealed class SessionOptions
{
    /// <summary>The session's standing context; left out when <c>null</c>.</summary>
    public object? State { get; init; }

    /// <summary>The questions every frame is answered with. At least one.</summary>
    public required IReadOnlyDictionary<string, Question> Questions { get; init; }

    /// <summary>Instructions for the whole session; left out when <c>null</c>.</summary>
    public object? Instructions { get; init; }

    /// <summary>The model; the client's model when <c>null</c> or empty.</summary>
    public string? Model { get; init; }

    /// <summary>How many recent frames each answer sees; the server's default when <c>null</c>.</summary>
    public int? Window { get; init; }

    /// <summary>The session's lifetime in seconds; the server's default when <c>null</c>.</summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>One frame for <see cref="Session.SendAsync(SessionFrame, CancellationToken)"/>.</summary>
public sealed class SessionFrame
{
    /// <summary>The reading: a string or any JSON-serialisable value. Optional beside an image.</summary>
    public object? Frame { get; init; }

    /// <summary>One image, as bytes or an upload id (frames take no URL).</summary>
    public Image? Image { get; init; }

    /// <summary>Ask only these of the session's questions; all when <c>null</c>.</summary>
    public IEnumerable<string>? Questions { get; init; }

    /// <summary><c>true</c> empties the window before this frame is answered.</summary>
    public bool? Reset { get; init; }

    /// <summary>The frame's number, carried back on its reply; counted from 1 when <c>null</c>.</summary>
    public long? Seq { get; init; }
}

/// <summary>What a session's stream delivers: a <see cref="Reply"/>, a <see cref="FrameError"/> or, last, <see cref="SessionClosed"/>.</summary>
public abstract class SessionEvent
{
    private protected SessionEvent()
    {
    }
}

/// <summary>The answers to one frame.</summary>
public sealed class Reply : SessionEvent
{
    internal Reply(JsonObject raw)
    {
        Raw = raw;
        Seq = JsonValues.AsLong(raw["seq"]);
        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        if (raw["answers"] is JsonObject map)
        {
            foreach (var (id, node) in map)
            {
                answers[id] = node is JsonObject ? Answer.Parse(id, node) : new UnknownAnswer(new JsonObject { ["value"] = node?.DeepClone() });
            }
        }
        Answers = new ReadOnlyDictionary<string, Answer>(answers);
        if (raw["dropped"] is JsonArray dropped)
        {
            Dropped = dropped.Select(JsonValues.AsLong).OfType<long>().ToList();
        }
        Usage = Session.UsageOf(raw["usage"]);
        LatencyMs = JsonValues.TryDouble(raw["latency_ms"], out var ms) ? ms : null;
        Safety = Safety.FromJson(raw["safety"]);
    }

    /// <summary>The <c>seq</c> of the frame answered.</summary>
    public long? Seq { get; }

    /// <summary>The answers, by question id, typed as in a <see cref="Decision"/>.</summary>
    public IReadOnlyDictionary<string, Answer> Answers { get; }

    /// <summary>The <c>seq</c> of each frame this one superseded since the last reply (latest wins), as received; <c>null</c> when none.</summary>
    public IReadOnlyList<long>? Dropped { get; }

    /// <summary>What the frame billed.</summary>
    public Usage? Usage { get; }

    /// <summary>The server's time for this frame, in milliseconds.</summary>
    public double? LatencyMs { get; }

    /// <summary>The safety check on this frame.</summary>
    public Safety? Safety { get; }

    /// <summary>The reply exactly as received.</summary>
    public JsonObject Raw { get; }

    /// <summary>The answer with this id.</summary>
    public Answer this[string id] => Answers[id];
}

/// <summary>A frame the server could not answer. A value, never thrown: the session goes on.</summary>
public sealed class FrameError : SessionEvent
{
    internal FrameError(JsonObject raw)
    {
        Raw = raw;
        Seq = JsonValues.AsLong(raw["seq"]);
        var error = raw["error"] as JsonObject;
        ErrorType = JsonValues.AsString(error?["error_type"]);
        Message = JsonValues.AsString(error?["message"]) ?? string.Empty;
        Category = JsonValues.AsString(error?["category"]);
        RetryAfterMs = JsonValues.AsLong(raw["retry_after_ms"]);
        Usage = raw["usage"] is null ? null : Session.UsageOf(raw["usage"]);
        Safety = Safety.FromJson(raw["safety"]);
    }

    /// <summary>The frame's <c>seq</c>, or <c>null</c> when the message could not be read as a frame.</summary>
    public long? Seq { get; }

    /// <summary><c>api_usage_error</c>, <c>rate_limit_error</c>, <c>overloaded_error</c>, <c>api_error</c>, <c>refusal_error</c> or an upload's error.</summary>
    public string? ErrorType { get; }

    /// <summary>The server's message.</summary>
    public string Message { get; }

    /// <summary>A refusal's category: <c>harm</c> or <c>self_harm</c>.</summary>
    public string? Category { get; }

    /// <summary>How long to wait before sending again, for <c>rate_limit_error</c> and <c>overloaded_error</c>.</summary>
    public long? RetryAfterMs { get; }

    /// <summary>What a refused frame billed.</summary>
    public Usage? Usage { get; }

    /// <summary>The safety check on a refused frame, with <see cref="Safety.Blocked"/>.</summary>
    public Safety? Safety { get; }

    /// <summary>The message exactly as received.</summary>
    public JsonObject Raw { get; }
}

/// <summary>
/// The end of a session's stream: the WebSocket close code and reason. <see cref="Reopen"/>
/// is true for the codes after which opening the session again with the same body is the
/// right answer (<c>1001</c>, <c>1006</c>, <c>1011</c>, <c>4404</c>, <c>4408</c>, <c>4409</c>,
/// <c>4410</c>). On <c>1013</c> the session is kept and the client connects to it again.
/// </summary>
public sealed class SessionClosed : SessionEvent
{
    internal SessionClosed(int code, string reason)
    {
        Code = code;
        Reason = reason;
    }

    /// <summary>The close code; <c>1006</c> when the connection was lost without a close frame.</summary>
    public int Code { get; }

    /// <summary>The close reason, short text for logs; branch on <see cref="Code"/>.</summary>
    public string Reason { get; }

    /// <summary>Whether to open the session again with the same body.</summary>
    public bool Reopen => Code is 1001 or 1006 or 1011 or 4404 or 4408 or 4409 or 4410;

    public override string ToString() => $"session closed with {Code} {Reason}".TrimEnd();
}

/// <summary>
/// An open session: the open response's fields, and its WebSocket stream. Connect, send
/// frames, read replies, and end it. <see cref="SendAsync(SessionFrame, CancellationToken)"/>
/// never waits for a reply; read them with <see cref="RepliesAsync"/>. Dispose it to drop the socket.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly DecisionNodeClient _client;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private ClientWebSocket? _socket;
    private Task? _reader;
    private SessionClosed? _closed;
    private long _seq;

    internal Session(DecisionNodeClient client, JsonObject raw)
    {
        _client = client;
        Raw = raw;
        SessionId = JsonValues.AsString(raw["session_id"])!;
        StreamUrl = JsonValues.AsString(raw["stream_url"])!;
        Model = JsonValues.AsString(raw["model"]);
        Window = (int?)JsonValues.AsLong(raw["window"]);
        TtlSeconds = (int?)JsonValues.AsLong(raw["ttl_seconds"]);
        ExpiresAt = JsonValues.AsString(raw["expires_at"]);
        Limits = raw["limits"] as JsonObject;
        Questions = (raw["questions"] as JsonArray)?.Select(JsonValues.AsString).OfType<string>().ToList() ?? new List<string>();
        PrefixTokens = JsonValues.AsLong(raw["prefix_tokens"]);
        Usage = UsageOf(raw["usage"]);
        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        if (raw["answers"] is JsonObject map)
        {
            foreach (var (id, node) in map)
            {
                answers[id] = node is JsonObject ? Answer.Parse(id, node) : new UnknownAnswer(new JsonObject { ["value"] = node?.DeepClone() });
            }
        }
        Answers = new ReadOnlyDictionary<string, Answer>(answers);
        Safety = Safety.FromJson(raw["safety"]);
    }

    /// <summary>The session's id, <c>ses_</c> and 24 hex digits.</summary>
    public string SessionId { get; }

    /// <summary>The model version holding the session.</summary>
    public string? Model { get; }

    /// <summary>The WebSocket URL of the stream.</summary>
    public string StreamUrl { get; }

    public int? Window { get; }

    public int? TtlSeconds { get; }

    /// <summary>When the session ends at the latest, as sent.</summary>
    public string? ExpiresAt { get; }

    /// <summary>The session's limits: <c>max_frame_tokens</c>, <c>max_frame_images</c>, <c>window_tokens</c>, <c>idle_timeout_seconds</c>.</summary>
    public JsonObject? Limits { get; }

    /// <summary>The session's question ids, in answer order.</summary>
    public IReadOnlyList<string> Questions { get; }

    public long? PrefixTokens { get; }

    /// <summary>What the open billed.</summary>
    public Usage? Usage { get; }

    /// <summary>The answers to the standing state at open, by question id.</summary>
    public IReadOnlyDictionary<string, Answer> Answers { get; }

    public Safety? Safety { get; }

    /// <summary>The open response exactly as received.</summary>
    public JsonObject Raw { get; }

    /// <summary>Whether the socket is connected and not yet closed.</summary>
    public bool IsConnected => _socket is not null && _closed is null;

    /// <summary>
    /// Opens the session's WebSocket with the key in the <c>Authorization</c> header (never in
    /// the URL) and the client's headers. A handshake the server refuses with a close code
    /// arrives as the first event, a <see cref="SessionClosed"/>.
    /// </summary>
    /// <exception cref="DecisionNodeError">The server answered the handshake with an HTTP status instead of upgrading.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is not null && _closed is null)
        {
            throw new InvalidOperationException("the session is already connected");
        }
        if (!Uri.TryCreate(StreamUrl, UriKind.Absolute, out var uri)
            || !(uri.Scheme == "wss" || (uri.Scheme == "ws" && IsLoopback(uri.Host))))
        {
            throw new DecisionNodeError(new ErrorDetails
            {
                Message = $"the stream URL '{StreamUrl}' is not wss; the key is never sent over plain text",
            });
        }
        if (string.IsNullOrEmpty(_client.ApiKey))
        {
            throw new AuthenticationError(new ErrorDetails { Message = "no API key: pass apiKey or set DECISIONNODE_API_KEY" });
        }
        _closed = null;
        _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleWriter = true });
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        // The server pings every 20 s and the reader answers at once; no unsolicited pongs.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        foreach (var (name, value) in _client.HandshakeHeaders())
        {
            socket.Options.SetRequestHeader(name, value);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_client.Timeout);
        try
        {
            await socket.ConnectAsync(uri, _client.Http, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var status = (int)socket.HttpStatusCode;
            socket.Dispose();
            if (status is > 0 and not 101)
            {
                throw new DecisionNodeError(new ErrorDetails
                {
                    Status = status,
                    Message = $"the stream refused the upgrade with {status}",
                    Inner = ex,
                });
            }
            var details = new ErrorDetails { Message = "the stream could not be connected: " + ex.Message, Inner = ex };
            throw ex is OperationCanceledException ? new TimeoutError(details) : new ConnectionError(details);
        }
        _socket = socket;
        _reader = Task.Run(() => ReadLoopAsync(socket, _events.Writer));
    }

    /// <summary>
    /// Sends one frame: one JSON text message. <c>seq</c> is counted from 1 when not given.
    /// A frame's image follows the client's inline threshold: small bytes inline, large or
    /// already uploaded ones by <c>upload_id</c>. Returns the frame's <c>seq</c>.
    /// </summary>
    /// <exception cref="BadRequestError">
    /// The frame breaks a fixed rule (a <c>seq</c> out of range, no frame value and no image,
    /// an empty or wrongly typed frame, an empty or unknown question list, nesting over 128,
    /// an image that is a URL or breaks the image rules): nothing is written and no <c>seq</c>
    /// is used. Its <c>ErrorType</c> is <c>api_usage_error</c>, as the server's frame error would be.
    /// </exception>
    public Task<long> SendAsync(SessionFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return SendAsync(frame.Frame, frame.Image, frame.Questions, frame.Reset, frame.Seq, cancellationToken);
    }

    /// <summary>Sends one frame. As <see cref="SendAsync(SessionFrame, CancellationToken)"/>.</summary>
    public async Task<long> SendAsync(
        object? frame = null,
        Image? image = null,
        IEnumerable<string>? questions = null,
        bool? reset = null,
        long? seq = null,
        CancellationToken cancellationToken = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("connect the session before sending");
        // The frame's fixed rules, before anything is written: a refused frame takes no seq.
        var frameNode = frame is null ? null : JsonValues.ToNode(frame, "the frame");
        var asked = questions?.ToList();
        LocalChecks.Frame(seq, frameNode, image is not null, asked, Questions);
        if (image is not null)
        {
            if (image.Url is not null)
            {
                throw LocalChecks.BadRequest("image: a frame takes bytes or an upload, not a url.", "api_usage_error");
            }
            LocalChecks.One(image, _client.Held(Model));
        }
        var number = seq ?? Interlocked.Increment(ref _seq);
        var message = new JsonObject { ["seq"] = number };
        if (frameNode is not null)
        {
            message["frame"] = frameNode;
        }
        if (image is not null)
        {
            var (wires, _) = await _client.PlaceImagesAsync(new[] { image }, cancellationToken).ConfigureAwait(false);
            var wire = (JsonObject)wires![0]!;
            wire.Remove("id");
            wires.Clear();
            message["image"] = wire;
        }
        if (asked is not null)
        {
            message["questions"] = new JsonArray(asked.Select(q => (JsonNode?)JsonValue.Create(q)).ToArray());
        }
        if (reset is not null)
        {
            message["reset"] = reset.Value;
        }
        var bytes = JsonValues.ToUtf8(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new ConnectionError(new ErrorDetails { Message = "the frame could not be sent: the stream is closed", Inner = ex });
        }
        finally
        {
            _sendLock.Release();
        }
        return number;
    }

    /// <summary>
    /// Every event in order: <see cref="Reply"/> and <see cref="FrameError"/> values, ending
    /// with the <see cref="SessionClosed"/> that closed the stream.
    /// </summary>
    public async IAsyncEnumerable<SessionEvent> RepliesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var ev = await ReadAsync(cancellationToken).ConfigureAwait(false);
            yield return ev;
            if (ev is SessionClosed)
            {
                yield break;
            }
        }
    }

    /// <summary>The next event; after the stream closed, its <see cref="SessionClosed"/> again.</summary>
    public async ValueTask<SessionEvent> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("connect the session before reading");
        }
        if (_events.Reader.TryRead(out var ready))
        {
            return Note(ready);
        }
        try
        {
            return Note(await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (ChannelClosedException) when (_closed is not null)
        {
            return _closed;
        }
    }

    internal bool TryRead(out SessionEvent? ev)
    {
        if (_events.Reader.TryRead(out var next))
        {
            ev = Note(next);
            return true;
        }
        ev = _closed is not null && _events.Reader.Completion.IsCompleted ? _closed : null;
        return ev is not null;
    }

    private SessionEvent Note(SessionEvent ev)
    {
        if (ev is SessionClosed closed)
        {
            _closed = closed;
        }
        return ev;
    }

    /// <summary>Ends the session (<c>DELETE /v1/sessions/{id}</c>). An already ended session (a <c>404</c>) is success.</summary>
    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ExchangeAsync(HttpMethod.Delete, "/v1/sessions/" + Uri.EscapeDataString(SessionId), null, null, DecisionNodeClient.RetryPolicy.Default, stream: false, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundError)
        {
            // Already ended.
        }
    }

    /// <summary>Closes the socket with <c>1000</c>, which ends the session on the server.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Drops the socket. The session itself ends on the server when its socket closes.</summary>
    public async ValueTask DisposeAsync()
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }
        if (socket.State == WebSocketState.Open)
        {
            await CloseAsync().ConfigureAwait(false);
        }
        socket.Abort();
        if (_reader is { } reader)
        {
            await reader.ConfigureAwait(false);
        }
        socket.Dispose();
    }

    /// <summary>This session, ready to connect again (after <c>1013</c>).</summary>
    internal Session Reopened()
    {
        _socket = null;
        _reader = null;
        _closed = null;
        return this;
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, ChannelWriter<SessionEvent> writer)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        SessionClosed closed;
        try
        {
            while (true)
            {
                buffer.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.GetMemory(16 * 1024), CancellationToken.None).ConfigureAwait(false);
                    buffer.Advance(result.Count);
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closed = new SessionClosed((int?)socket.CloseStatus ?? 1005, socket.CloseStatusDescription ?? string.Empty);
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await socket.CloseOutputAsync(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                        {
                        }
                    }
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(buffer.WrittenSpan) as JsonObject;
                }
                catch (JsonException)
                {
                    message = null;
                }
                if (message is null)
                {
                    continue;
                }
                writer.TryWrite(message.ContainsKey("error") ? new FrameError(message) : new Reply(message));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Lost without a close frame.
            closed = new SessionClosed(1006, string.Empty);
        }
        writer.TryWrite(closed);
        writer.TryComplete();
    }

    internal static Usage? UsageOf(JsonNode? node) =>
        node is JsonObject usage ? new Usage(JsonValues.AsLong(usage["input_tokens"]), JsonValues.AsLong(usage["output_tokens"])) : null;

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1";
}
