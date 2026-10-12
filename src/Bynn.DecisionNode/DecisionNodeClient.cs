using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// The DecisionNode API client. Safe to share between threads and tasks; it reuses its
/// connections (HTTP/2 where the server offers it) and its image uploads, so create one and
/// keep it.
/// </summary>
/// <example>
/// <code>
/// using Bynn.DecisionNode;
/// using static Bynn.DecisionNode.Question;
///
/// using var dn = new DecisionNodeClient(); // reads DECISIONNODE_API_KEY
/// var d = await dn.DecideAsync(
///     new { ticket = "I was charged twice and nobody has replied for 3 days." },
///     new Dictionary&lt;string, Question&gt; { ["escalate"] = Truth("Escalate to a human now?") });
/// Console.WriteLine(d.Truth("escalate").Truth); // 0.94
/// </code>
/// </example>
public sealed partial class DecisionNodeClient : IDisposable
{
    /// <summary>This library's version, sent in the User-Agent header.</summary>
    public const string Version = "0.3.0";

    /// <summary>The version of the DecisionNode SDK specification this library implements.</summary>
    public const string SpecVersion = "1.3";

    /// <summary>
    /// Local image bytes in one request (or one session frame, or one batch line) that
    /// together total under this many bytes go inline as base64; at or above it they are
    /// uploaded and sent by <c>upload_id</c>. Override it with
    /// <see cref="DecisionNodeClientOptions.InlineThreshold"/>.
    /// </summary>
    public const int DefaultInlineThreshold = 524288;

    /// <summary>
    /// The API's request body limit, 26,214,400 bytes (25 MiB). Inline images and video frames
    /// count toward it; uploaded images and image URLs do not. A body over it is refused with a
    /// <see cref="RequestTooLargeError"/> before anything is sent, so large images belong in
    /// uploads (any local image at or above <see cref="DefaultInlineThreshold"/> is uploaded).
    /// </summary>
    public const int MaxBodyBytes = 26_214_400;

    /// <summary>The API's default root URL.</summary>
    public const string DefaultBaseUrl = "https://api.decisionnode.com";

    /// <summary>
    /// The default model, <c>decisionnode-moe</c>: the family id, which always means the current
    /// release. <c>decisionnode-flash</c> is the faster, lower-cost family.
    /// </summary>
    public const string DefaultModel = "decisionnode-moe";

    /// <summary>The default number of retries after the first attempt.</summary>
    public const int DefaultMaxRetries = 2;

    /// <summary>
    /// The default time allowed for each attempt; 30 seconds for a decision whose boosters name
    /// <c>ocr</c> (see <see cref="Timeout"/>).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The time allowed for each attempt of a decision naming <c>ocr</c> when no timeout was set.</summary>
    internal static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The default time allowed for each attempt of an image's storage <c>PUT</c>, apart from <see cref="DefaultTimeout"/>.</summary>
    public static readonly TimeSpan DefaultUploadTimeout = TimeSpan.FromSeconds(60);

    private const string UserAgent = "decisionnode-dotnet/" + Version;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);

    /// <summary>The longest wait the client keeps when the server asks it to wait before a retry.</summary>
    internal static readonly TimeSpan MaxServerWait = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _apiKey;
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly Action<string>? _debugLog;
    private readonly UploadCache _uploads = new();

    /// <summary>True when the caller set <see cref="DecisionNodeClientOptions.Timeout"/>: it is then used as given, even for ocr.</summary>
    private readonly bool _timeoutSet;

    /// <summary>Creates a client from settings, the environment and the defaults, in that order.</summary>
    public DecisionNodeClient(DecisionNodeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // An empty string counts as not given: the environment, then the default, applies.
        _apiKey = NonEmpty(options.ApiKey) ?? NonEmpty(Environment.GetEnvironmentVariable("DECISIONNODE_API_KEY"));
        BaseUrl = CheckBaseUrl((NonEmpty(options.BaseUrl) ?? NonEmpty(Environment.GetEnvironmentVariable("DECISIONNODE_BASE_URL")) ?? DefaultBaseUrl).TrimEnd('/'));
        Model = NonEmpty(options.Model) ?? NonEmpty(Environment.GetEnvironmentVariable("DECISIONNODE_MODEL")) ?? DefaultModel;
        Timeout = options.Timeout ?? DefaultTimeout;
        _timeoutSet = options.Timeout is not null;
        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), Timeout, "Timeout must be above zero.");
        }
        MaxRetries = options.MaxRetries ?? DefaultMaxRetries;
        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), MaxRetries, "MaxRetries must be 0 or more.");
        }
        UploadTimeout = options.UploadTimeout ?? DefaultUploadTimeout;
        if (UploadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), UploadTimeout, "UploadTimeout must be above zero.");
        }
        InlineThreshold = options.InlineThreshold ?? DefaultInlineThreshold;
        if (InlineThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), InlineThreshold, "InlineThreshold must be 0 or more.");
        }
        _headers = (options.Headers ?? new Dictionary<string, string>()).ToArray();
        _debugLog = options.DebugLog;
        if (options.HttpClient is { } http)
        {
            CheckCallerHttpClient(http, BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase), options.DelegatingHandlersNeverResendRequests);
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                // A redirect is answered as an error, never followed, so the key is never
                // sent to another URL or over plain HTTP.
                AllowAutoRedirect = false,
                // Many requests share few connections: HTTP/2 multiplexes them, and a second
                // connection opens when one fills its streams.
                EnableMultipleHttp2Connections = true,
            };
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        Batches = new BatchesClient(this);
        Collections = new CollectionsClient(this);
    }

    /// <summary>
    /// The base URL must be <c>https</c>; plain <c>http</c> is accepted only for
    /// <c>localhost</c>, <c>127.0.0.1</c> and <c>::1</c>, so a key never travels unencrypted.
    /// </summary>
    private static string CheckBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"the base URL '{baseUrl}' is not an absolute URL", "baseUrl");
        }
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return baseUrl;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackName(uri.Host))
        {
            return baseUrl;
        }
        throw new ArgumentException(
            $"the base URL '{baseUrl}' must use https; plain http is accepted only for localhost, 127.0.0.1 and ::1",
            "baseUrl");
    }

    private static bool IsLoopbackName(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "[::1]"
        || host == "::1";

    /// <summary>
    /// Refuses an <see cref="HttpClient"/> that cannot be shown to refuse redirects, or that
    /// turns off certificate checks for an <c>https</c> base URL. A followed redirect would
    /// send the request, the key and the caller's own headers to another URL; a switched-off
    /// certificate check would let anyone in the middle read the key.
    /// </summary>
    /// <remarks>
    /// Only handlers whose behaviour is known are accepted, matched by exact type, never by
    /// base class: a subclass can override <c>SendAsync</c> and follow a redirect itself.
    /// The terminal handler must be exactly <see cref="SocketsHttpHandler"/> or
    /// <see cref="HttpClientHandler"/> with <c>AllowAutoRedirect</c> off, or
    /// <c>WinHttpHandler</c> with <c>AutomaticRedirection</c> off. A
    /// <see cref="DelegatingHandler"/> can send a request again to any URL, so one anywhere in
    /// the chain is refused unless <paramref name="delegatingHandlersNeverResend"/> is set.
    /// </remarks>
    private static void CheckCallerHttpClient(HttpClient http, bool https, bool delegatingHandlersNeverResend)
    {
        if (http.GetType() != typeof(HttpClient))
        {
            throw new ArgumentException(
                $"the HttpClient is a {http.GetType().FullName}, not an HttpClient, so the client cannot make sure it never follows a redirect; " +
                "pass new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })",
                "httpClient");
        }
        var handler = HandlerField?.GetValue(http) as HttpMessageHandler;
        if (handler is null)
        {
            throw new ArgumentException(
                "the HttpClient's handler cannot be inspected, so the client cannot make sure it never follows a redirect; " +
                "create it from a SocketsHttpHandler with AllowAutoRedirect = false",
                "httpClient");
        }
        while (handler is DelegatingHandler delegating)
        {
            if (!delegatingHandlersNeverResend)
            {
                throw new ArgumentException(
                    $"the HttpClient's handler chain holds a DelegatingHandler ({delegating.GetType().FullName}), which can send a request, " +
                    "and the key, again to another URL. Pass an HttpClient built directly on a SocketsHttpHandler with AllowAutoRedirect = false, " +
                    "or, if every DelegatingHandler in the chain passes each request on once, unchanged, and never sends it again, " +
                    "set DelegatingHandlersNeverResendRequests = true",
                    "httpClient");
            }
            handler = delegating.InnerHandler ?? throw new ArgumentException(
                $"the HttpClient's {delegating.GetType().Name} has no inner handler, so the client cannot make sure it never follows a redirect",
                "httpClient");
        }
        bool follows;
        bool certificateChecksOff;
        var type = handler.GetType();
        if (type == typeof(SocketsHttpHandler))
        {
            var sockets = (SocketsHttpHandler)handler;
            follows = sockets.AllowAutoRedirect;
            certificateChecksOff = sockets.SslOptions.RemoteCertificateValidationCallback is not null;
        }
        else if (type == typeof(HttpClientHandler))
        {
            var client = (HttpClientHandler)handler;
            follows = client.AllowAutoRedirect;
            certificateChecksOff = client.ServerCertificateCustomValidationCallback is not null;
        }
        else if (IsWinHttpHandler(type))
        {
            // WinHttpHandler lives in a separate package, so it is read by reflection.
            follows = type.GetProperty("AutomaticRedirection")?.GetValue(handler) is not false;
            certificateChecksOff = type.GetProperty("ServerCertificateValidationCallback")?.GetValue(handler) is not null;
        }
        else
        {
            throw new ArgumentException(
                $"the HttpClient's handler {type.FullName} is not one DecisionNode can inspect, so the client cannot make sure " +
                "it never follows a redirect. Use exactly a SocketsHttpHandler or HttpClientHandler with AllowAutoRedirect = false " +
                "(or a WinHttpHandler with AutomaticRedirection = false); subclasses are refused, because they can follow a redirect themselves",
                "httpClient");
        }
        if (follows)
        {
            throw new ArgumentException(
                "the HttpClient follows redirects; DecisionNode never follows one, so a key is never sent to another URL. " +
                "Create it from a handler with AllowAutoRedirect = false, for example " +
                "new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })",
                "httpClient");
        }
        if (https && certificateChecksOff)
        {
            throw new ArgumentException(
                "the HttpClient's handler replaces the server certificate check with a callback of its own, which can turn the check off; " +
                "DecisionNode keeps the platform's certificate and host name checks for an https base URL, so a key never reaches anyone else. " +
                "Remove the certificate validation callback",
                "httpClient");
        }
    }

    /// <summary>
    /// The WinHttpHandler package's sealed handler. It is matched by its full name, its
    /// assembly's name and the assembly's public key token, so a type that only copies the
    /// name is refused.
    /// </summary>
    private static bool IsWinHttpHandler(Type type)
    {
        if (type.FullName != "System.Net.Http.WinHttpHandler" || !type.IsSealed)
        {
            return false;
        }
        var assembly = type.Assembly.GetName();
        var token = assembly.GetPublicKeyToken();
        return assembly.Name == "System.Net.Http.WinHttpHandler"
            && token is not null
            && Convert.ToHexString(token).Equals(WinHttpHandlerPublicKeyToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The public key token of the assemblies Microsoft ships for .NET.</summary>
    private const string WinHttpHandlerPublicKeyToken = "cc7b13ffcd2ddd51";

    private static readonly System.Reflection.FieldInfo? HandlerField =
        typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// Creates a client. Every argument is optional; <paramref name="apiKey"/>,
    /// <paramref name="baseUrl"/> and <paramref name="model"/> fall back to the environment.
    /// </summary>
    public DecisionNodeClient(
        string? apiKey = null,
        string? baseUrl = null,
        string? model = null,
        TimeSpan? timeout = null,
        int? maxRetries = null,
        IDictionary<string, string>? headers = null,
        HttpClient? httpClient = null,
        int? inlineThreshold = null,
        TimeSpan? uploadTimeout = null)
        : this(new DecisionNodeClientOptions
        {
            InlineThreshold = inlineThreshold,
            UploadTimeout = uploadTimeout,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Model = model,
            Timeout = timeout,
            MaxRetries = maxRetries,
            Headers = headers,
            HttpClient = httpClient,
        })
    {
    }

    /// <summary>The API's root URL in use.</summary>
    public string BaseUrl { get; }

    /// <summary>The model sent when a call names none.</summary>
    public string Model { get; }

    /// <summary>
    /// The time allowed for each attempt. When no timeout was set, a decision whose boosters name
    /// <c>ocr</c> is allowed 30 seconds; a timeout that was set is used as given, even below that.
    /// A decision naming <c>ocr</c> is never retried after a timeout, since the server finishes
    /// the attempt and may bill it.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>Retries after the first attempt.</summary>
    public int MaxRetries { get; }

    /// <summary>The time allowed for each attempt of an image's storage <c>PUT</c>.</summary>
    public TimeSpan UploadTimeout { get; }

    /// <summary>The inline threshold in use, in bytes (see <see cref="DefaultInlineThreshold"/>).</summary>
    public int InlineThreshold { get; }

    /// <summary>The Batch API: create, add, finalize, get, list, results and cancel.</summary>
    public BatchesClient Batches { get; }

    /// <summary>The workspace's collections of faces, which the <c>face_recognition</c> booster searches.</summary>
    public CollectionsClient Collections { get; }

    /// <summary>
    /// The wait between attempts. Tests replace it to record the waits the client schedules
    /// without spending them.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>The clock the upload cache reads. Tests replace it.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;


    // ------------------------------------------------------------------ the surface

    /// <summary>Sends one decision: a state, questions by id, and optional images and videos.</summary>
    /// <param name="state">What is judged: a string or any JSON-serialisable value. <c>null</c> sends an empty state.</param>
    /// <param name="questions">The questions, by id. At least one.</param>
    /// <param name="images">Images sent beside the state, up to 16. URLs are sent as given; local bytes inline or uploaded by size.</param>
    /// <param name="videos">Videos sent as frames beside the state, up to 4. Frames always go inline.</param>
    /// <param name="model">The model for this call, a fine-tuned model's name included; the client's model when <c>null</c> or empty.</param>
    /// <param name="boosters">Boosters that read the request's images before the decision, up to 4, in order. Their readings come back in <see cref="Decision.Evidence"/>.</param>
    /// <param name="fallback">
    /// <c>false</c>: no other model answers when the one asked for has no ready worker, and the
    /// request gets an <see cref="OverloadedError"/> instead. Left out when <c>null</c>: the
    /// server's default, which may answer with another model and says so in <see cref="Decision.Fallback"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call, retries included.</param>
    /// <exception cref="ArgumentException">No questions, or a value that cannot be serialised; nothing is sent.</exception>
    /// <exception cref="BadRequestError">The request breaks a fixed rule of the API (an image, a video, a question's limits), found before anything is sent.</exception>
    /// <exception cref="RequestTooLargeError">The body would be over <see cref="MaxBodyBytes"/>, found before anything is sent; or the API's 413.</exception>
    public Task<Decision> DecideAsync(
        object? state,
        IReadOnlyDictionary<string, Question> questions,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default)
    {
        var request = Prepare(state, questions, images, videos, model, boosters, fallback);
        return SendDecisionAsync(DecidePath, request, cancellationToken);
    }

    /// <summary>
    /// Runs every check <see cref="DecideAsync(DecideRequest, CancellationToken)"/> runs before
    /// sending (the fixed rules of the questions, the state and the images, held
    /// <c>/v1/models</c> limits included) and throws exactly what it would throw. Sends and
    /// uploads nothing; returns when the request would be sent. The helpers
    /// (<see cref="CheckAsync"/> and the others) send <c>decide</c> requests and are checked the same way.
    /// </summary>
    /// <exception cref="ArgumentException">No questions, or a value that cannot be serialised.</exception>
    /// <exception cref="BadRequestError">A fixed rule is broken (with the API's <c>error_type</c>).</exception>
    /// <exception cref="ValidationError">A rule the API answers with <c>422</c> is broken, with the server's entries.</exception>
    /// <exception cref="RequestTooLargeError">The body would be over <see cref="MaxBodyBytes"/>.</exception>
    public void CheckRequest(DecideRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = Prepare(request.State, request.Questions, request.Images, request.Videos, request.Model, request.Boosters, request.Fallback);
    }

    /// <summary>Sends one decision described by a <see cref="DecideRequest"/>.</summary>
    public Task<Decision> DecideAsync(DecideRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DecideAsync(request.State, request.Questions, request.Images, request.Videos, request.Model, request.Boosters, request.Fallback, cancellationToken);
    }

    /// <summary>
    /// Sends many decisions, at most <paramref name="concurrency"/> at a time over the
    /// client's shared connections. Returns one result per request, in input order: a
    /// decision, or the error that request ended with. One failure does not stop the others.
    /// An image several requests share is uploaded once.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrency"/> is below 1; nothing is sent.</exception>
    /// <exception cref="ArgumentException">A request has no questions or a value that cannot be serialised; nothing is sent.</exception>
    /// <exception cref="BadRequestError">A request breaks a fixed rule of the API; nothing is sent.</exception>
    public Task<IReadOnlyList<DecisionResult>> DecideManyAsync(
        IEnumerable<DecideRequest> requests,
        int concurrency = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        // Every request is built and checked first, so a fault in any fails before anything is sent.
        var prepared = requests.Select(r =>
        {
            ArgumentNullException.ThrowIfNull(r, nameof(requests));
            return Prepare(r.State, r.Questions, r.Images, r.Videos, r.Model, r.Boosters, r.Fallback);
        }).ToList();
        return DecideManyCoreAsync(prepared, concurrency, cancellationToken);
    }

    private async Task<IReadOnlyList<DecisionResult>> DecideManyCoreAsync(List<PreparedRequest> list, int concurrency, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(concurrency);

        async Task<DecisionResult> RunOne(PreparedRequest request)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return new DecisionResult(await SendDecisionAsync(DecidePath, request, cancellationToken).ConfigureAwait(false), null);
            }
            catch (DecisionNodeError error)
            {
                return new DecisionResult(null, error);
            }
            finally
            {
                gate.Release();
            }
        }

        return await Task.WhenAll(list.Select(RunOne)).ConfigureAwait(false);
    }

    /// <summary>Asks one yes-or-no question (id <c>check</c>) and returns its answer.</summary>
    public async Task<TruthAnswer> CheckAsync(
        object? state,
        object? instructions = null,
        object? ifTrue = null,
        object? ifFalse = null,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default)
    {
        var d = await DecideAsync(state, One("check", Question.Truth(instructions, ifTrue, ifFalse)), images, videos, model, boosters, fallback, cancellationToken).ConfigureAwait(false);
        return Single<TruthAnswer>(d, "check");
    }

    /// <summary>Asks one choice question (id <c>classify</c>) with options given as names.</summary>
    public Task<ChoiceAnswer> ClassifyAsync(
        object? state,
        object? instructions,
        IEnumerable<string> options,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default) =>
        ClassifyCore(state, Question.Choice(instructions, options), images, videos, model, boosters, fallback, cancellationToken);

    /// <summary>Asks one choice question (id <c>classify</c>) with options mapped to descriptions.</summary>
    public Task<ChoiceAnswer> ClassifyAsync(
        object? state,
        object? instructions,
        IEnumerable<KeyValuePair<string, string>> options,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default) =>
        ClassifyCore(state, Question.Choice(instructions, options), images, videos, model, boosters, fallback, cancellationToken);

    /// <summary>Asks one choice question (id <c>classify</c>) with options mapped to descriptions of any shape.</summary>
    public Task<ChoiceAnswer> ClassifyAsync(
        object? state,
        object? instructions,
        IEnumerable<KeyValuePair<string, object?>> options,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default) =>
        ClassifyCore(state, Question.Choice(instructions, options), images, videos, model, boosters, fallback, cancellationToken);

    private async Task<ChoiceAnswer> ClassifyCore(object? state, Question q, IEnumerable<Image>? images, IEnumerable<Video>? videos, string? model, IEnumerable<Booster>? boosters, bool? fallback, CancellationToken ct)
    {
        var d = await DecideAsync(state, One("classify", q), images, videos, model, boosters, fallback, ct).ConfigureAwait(false);
        return Single<ChoiceAnswer>(d, "classify");
    }

    /// <summary>Asks one score question (id <c>score</c>); <paramref name="levels"/> run from low to high.</summary>
    public async Task<ScoreAnswer> ScoreAsync(
        object? state,
        object? instructions,
        IEnumerable<object?> levels,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default)
    {
        var d = await DecideAsync(state, One("score", Question.Score(instructions, levels)), images, videos, model, boosters, fallback, cancellationToken).ConfigureAwait(false);
        return Single<ScoreAnswer>(d, "score");
    }

    /// <summary>
    /// Asks one number question (id <c>number</c>): a value on the grid from
    /// <paramref name="min"/> to <paramref name="max"/> in steps of <paramref name="step"/>.
    /// An argument left out is left out of the request, so the server's defaults apply (0, 255, 1).
    /// </summary>
    public async Task<NumberAnswer> NumberAsync(
        object? state,
        object? instructions,
        double? min = null,
        double? max = null,
        double? step = null,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default)
    {
        var d = await DecideAsync(state, One("number", Question.Number(instructions, min, max, step)), images, videos, model, boosters, fallback, cancellationToken).ConfigureAwait(false);
        return Single<NumberAnswer>(d, "number");
    }

    /// <summary>
    /// Judges an action before it runs. Sends the state <c>{"action": ..., "context": ...}</c>
    /// with three questions: <c>risk</c> (a score from none to severe),
    /// <c>touches_production</c> (a truth) and <c>verdict</c> (a choice of <c>allow</c>,
    /// <c>confirm</c> or <c>block</c>). What to do with the answers is your code's decision.
    /// </summary>
    public Task<Decision> GateAsync(
        object action,
        object? context = null,
        IEnumerable<Image>? images = null,
        IEnumerable<Video>? videos = null,
        string? model = null,
        IEnumerable<Booster>? boosters = null,
        bool? fallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var state = new JsonObject { ["action"] = JsonValues.ToNode(action, "the action") };
        if (context is not null)
        {
            state["context"] = JsonValues.ToNode(context, "the context");
        }
        return DecideAsync(state, GateQuestions(), images, videos, model, boosters, fallback, cancellationToken);
    }

    /// <summary>The three questions <see cref="GateAsync"/> sends.</summary>
    public static IReadOnlyDictionary<string, Question> GateQuestions() => new Dictionary<string, Question>
    {
        ["risk"] = Question.Score(
            "How much harm could this action cause if it is wrong?",
            new[] { "none", "low", "moderate", "high", "severe" }),
        ["touches_production"] = Question.Truth(
            "Does this action change a production system, production data or anything customers use?"),
        ["verdict"] = Question.Choice(
            "Should this action run as it is, wait for a person to confirm it, or be blocked?",
            new Dictionary<string, string>
            {
                ["allow"] = "safe to run without asking",
                ["confirm"] = "a person should confirm it first",
                ["block"] = "should not run",
            }),
    };

    /// <summary>Lists the models with their published limits (<c>GET /v1/models</c>).</summary>
    public async Task<IReadOnlyList<ModelInfo>> ModelsAsync(CancellationToken cancellationToken = default)
    {
        var (body, _) = await SendJsonAsync(HttpMethod.Get, "/v1/models", null, null, RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
        if (body is not JsonObject obj || obj["models"] is not JsonArray models)
        {
            throw new DecisionNodeError("the response of GET /v1/models has no 'models' list");
        }
        var list = models.Select(m => m is JsonObject o
            ? new ModelInfo(o)
            : throw new DecisionNodeError("an entry of 'models' is not a JSON object")).ToList();
        // Held for the local checks: a limit only /v1/models states is checked once known.
        var held = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var m in list)
        {
            if (m.Name is { } name)
            {
                held[name] = (JsonObject)m.Raw.DeepClone();
            }
        }
        _held = held;
        return list;
    }

    private volatile Dictionary<string, JsonObject> _held = new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>/v1/models</c> entry of a model, when this client holds it from an earlier
    /// <see cref="ModelsAsync"/>: the entry of that name, else the entry that states the name
    /// as its <c>alias</c> or its pinned <c>version</c>. A name the list does not state holds
    /// nothing, so its checks are left to the server.
    /// </summary>
    internal JsonObject? Held(string? model)
    {
        var name = NonEmpty(model) ?? Model;
        var held = _held;
        if (held.TryGetValue(name, out var entry))
        {
            return entry;
        }
        return held.Values.FirstOrDefault(m => JsonValues.AsString(m["alias"]) == name || JsonValues.AsString(m["version"]) == name);
    }

    /// <summary>
    /// Sends <paramref name="body"/> to <c>POST /v1/decide</c> exactly as given and returns
    /// the response body as parsed JSON. The way to use an API field this version of the
    /// library does not know yet. Nothing is checked locally; retries and errors work as for
    /// every other call.
    /// </summary>
    public async Task<JsonNode?> DecideRawAsync(object body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var node = JsonValues.ToNode(body, "the body");
        var (parsed, _) = await SendJsonAsync(HttpMethod.Post, DecidePath, JsonValues.ToUtf8(node), JsonContentType, RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // ------------------------------------------------------------------ request building

    internal const string DecidePath = "/v1/decide";
    internal const string JsonContentType = "application/json";

    /// <summary>A decision's body without its images, checked, and the images to place at send time.</summary>
    internal sealed class PreparedRequest
    {
        public PreparedRequest(JsonObject body, IReadOnlyList<Image> images)
        {
            Body = body;
            Images = images;
        }

        public JsonObject Body { get; }

        public IReadOnlyList<Image> Images { get; }
    }

    /// <summary>
    /// Builds a decision body and checks it by the API's fixed rules, so a request the server
    /// would refuse fails here, before any network call.
    /// </summary>
    private PreparedRequest Prepare(object? state, IReadOnlyDictionary<string, Question> questions, IEnumerable<Image>? images, IEnumerable<Video>? videos, string? model, IEnumerable<Booster>? boosters, bool? fallback)
    {
        var qs = QuestionMap(questions, nameof(questions));
        var list = ImageList(images, nameof(images));
        var videoList = VideoList(videos, nameof(videos));
        var boosterList = Booster.List(boosters, nameof(boosters));
        var body = new JsonObject
        {
            ["model"] = NonEmpty(model) ?? Model,
            ["state"] = StateNode(state),
            ["questions"] = qs,
        };
        WithVideos(body, videoList);
        // Nothing about boosters is checked here; their list only counts toward the nesting limit.
        Booster.AddTo(body, boosterList);
        WithFallback(body, fallback);
        var held = Held(model);
        LocalChecks.Request(body, list.Select(i => i.Id).ToList(), held, entriesAfter: LocalChecks.CountEntries(list, videoList, held));
        LocalChecks.Images(list, held);
        LocalChecks.Videos(videoList, list.Select(i => i.Id));
        CheckBodySize(body, list);
        return new PreparedRequest(body, list);
    }

    /// <summary>The questions' wire forms, copied. The caller checks them with the request.</summary>
    internal static JsonObject QuestionMap(IReadOnlyDictionary<string, Question>? questions, string argument)
    {
        if (questions is null || questions.Count == 0)
        {
            throw new ArgumentException("questions must hold at least one question", argument);
        }
        var qs = new JsonObject();
        foreach (var (id, question) in questions)
        {
            if (question is null)
            {
                throw new ArgumentException($"question '{id}' is null", argument);
            }
            qs[id] = question.Wire.DeepClone();
        }
        return qs;
    }

    internal static List<Image> ImageList(IEnumerable<Image>? images, string argument)
    {
        var list = new List<Image>();
        if (images is not null)
        {
            foreach (var image in images)
            {
                ArgumentNullException.ThrowIfNull(image, argument);
                list.Add(image);
            }
        }
        return list;
    }

    /// <summary>The videos' wire forms, as given. The caller checks them with the request.</summary>
    internal static List<JsonObject> VideoList(IEnumerable<Video>? videos, string argument)
    {
        var list = new List<JsonObject>();
        if (videos is not null)
        {
            foreach (var video in videos)
            {
                ArgumentNullException.ThrowIfNull(video, argument);
                list.Add(video.Wire);
            }
        }
        return list;
    }

    /// <summary>Adds <c>fallback</c> to a body when it is given; left out, the server's default applies.</summary>
    internal static void WithFallback(JsonObject body, bool? fallback)
    {
        if (fallback is { } f)
        {
            body["fallback"] = f;
        }
    }

    /// <summary>Adds <c>videos</c> to a body when there are any; left out otherwise.</summary>
    internal static void WithVideos(JsonObject body, List<JsonObject> videos)
    {
        if (videos.Count > 0)
        {
            body["videos"] = new JsonArray(videos.Select(v => (JsonNode?)v.DeepClone()).ToArray());
        }
    }

    internal static JsonNode StateNode(object? state) =>
        state is null ? JsonValue.Create(string.Empty) : (JsonValues.ToNode(state, "the state") ?? JsonValue.Create(string.Empty));

    private static Dictionary<string, Question> One(string id, Question q) => new() { [id] = q };

    /// <summary>
    /// The helper's own answer, typed. A helper promises a typed answer, so a missing,
    /// malformed or differently typed one is a <see cref="ServerError"/> carrying the
    /// response's status, request id and <c>x-decisionnode-error</c> reason. (<c>DecideAsync</c>
    /// returns such an answer as an <see cref="UnknownAnswer"/> instead.)
    /// </summary>
    private static T Single<T>(Decision d, string id) where T : Answer
    {
        if (d.Answers.TryGetValue(id, out var a) && a is T typed)
        {
            return typed;
        }
        var what = a is null ? "has no answer" : "has a malformed answer";
        throw new ServerError(new ErrorDetails
        {
            Status = d.Status,
            RequestId = d.RequestId,
            Reason = d.Header("x-decisionnode-error"),
            Message = $"the response {what} '{id}'",
            Body = d.Raw,
        });
    }

    private async Task<Decision> SendDecisionAsync(string path, PreparedRequest request, CancellationToken ct)
    {
        // The ocr booster reads for longer: 30 s an attempt unless the caller set a timeout, and
        // no retry after a timeout, since the server finishes the attempt and may bill it.
        var plan = NamesOcr(request.Body)
            ? new AttemptPlan(_timeoutSet ? Timeout : OcrTimeout, RetryTimeouts: false)
            : AttemptPlan.Default;
        var (parsed, result) = await SendWithImagesAsync(path, request.Body, request.Images, plan, ct).ConfigureAwait(false);
        if (parsed is not JsonObject obj)
        {
            throw new ServerError(new ErrorDetails
            {
                Status = result.Status,
                RequestId = result.Header("x-request-id"),
                Message = "the response body is not a JSON object",
            });
        }
        return new Decision(obj, result.Header, result.Status);
    }

    /// <summary>True when the body's boosters name <c>ocr</c>, by its bare name or in the object form.</summary>
    private static bool NamesOcr(JsonObject body) =>
        body["boosters"] is JsonArray boosters && boosters.Any(b =>
            JsonValues.AsString(b) == Booster.Ocr || (b is JsonObject o && JsonValues.AsString(o["name"]) == Booster.Ocr));

    // ------------------------------------------------------------------ transport

    /// <summary>
    /// How one call's attempts run: the time allowed for each (<c>null</c>: the client's
    /// <see cref="Timeout"/>) and whether a timed-out attempt is retried.
    /// </summary>
    internal sealed record AttemptPlan(TimeSpan? Timeout, bool RetryTimeouts, string? IdempotencyKey = null)
    {
        public static readonly AttemptPlan Default = new(null, true);
    }

    /// <summary>Which failures a call is retried on.</summary>
    internal enum RetryPolicy
    {
        /// <summary>408, 429, 5xx, timeouts and connection errors.</summary>
        Default,

        /// <summary>
        /// Only 429 and 529, which the API answers before applying anything: a batch create,
        /// add or finalize repeated after a lost answer could apply twice.
        /// </summary>
        Mutation,

        /// <summary>As <see cref="Default"/>, except a 429 <c>session_limit_exceeded</c>, which waiting does not fix.</summary>
        SessionOpen,

        /// <summary>
        /// A call that carries its own <c>Idempotency-Key</c>, the same on every attempt, so the
        /// server applies it once however often it is tried: as <see cref="Default"/>, and also a
        /// <c>409</c> <c>idempotency_in_progress</c>, after its wait.
        /// </summary>
        Idempotent,
    }

    /// <summary>Sends a request with retries and parses the JSON body of its 2xx answer.</summary>
    internal async Task<(JsonNode? Body, Attempt Result)> SendJsonAsync(HttpMethod method, string path, byte[]? body, string? contentType, RetryPolicy policy, CancellationToken ct, AttemptPlan? plan = null)
    {
        var result = await ExchangeAsync(method, path, body, contentType, policy, stream: false, ct, plan).ConfigureAwait(false);
        return (ParseJson(result), result);
    }

    internal static JsonNode? ParseJson(Attempt result)
    {
        try
        {
            return JsonNode.Parse(result.Text);
        }
        catch (JsonException ex)
        {
            throw new ServerError(new ErrorDetails
            {
                Status = result.Status,
                RequestId = result.Header("x-request-id"),
                Message = "the response body is not JSON",
                Inner = ex,
            });
        }
    }

    /// <summary>
    /// Sends a request with retries and returns its 2xx answer; any other answer is thrown as
    /// its error. With <paramref name="stream"/> the 2xx body is left unread in
    /// <see cref="Attempt.Response"/>, which the caller disposes.
    /// </summary>
    internal async Task<Attempt> ExchangeAsync(HttpMethod method, string path, byte[]? body, string? contentType, RetryPolicy policy, bool stream, CancellationToken ct, AttemptPlan? plan = null)
    {
        plan ??= AttemptPlan.Default;
        var attemptTimeout = plan.Timeout ?? Timeout;
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new AuthenticationError(new ErrorDetails
            {
                Message = "no API key: pass apiKey or set DECISIONNODE_API_KEY",
            });
        }
        var uri = new Uri(BaseUrl + path, UriKind.Absolute);

        for (var attempt = 0; ; attempt++)
        {
            Attempt result;
            try
            {
                result = await SendOnceAsync(method, uri, path, body, contentType, stream, attemptTimeout, plan.IdempotencyKey, ct).ConfigureAwait(false);
            }
            catch (DecisionNodeError lost) when (lost is TimeoutError or ConnectionError && policy != RetryPolicy.Mutation && attempt < MaxRetries
                && (plan.RetryTimeouts || lost is not TimeoutError))
            {
                await Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            // Only 2xx is a success. A 3xx is an error like any other unmapped status.
            if (result.Status is >= 200 and <= 299)
            {
                return result;
            }

            var error = ErrorFor(result);
            var retryable = policy == RetryPolicy.Mutation
                ? result.Status is 429 or 529
                : (IsRetryable(result.Status) && error is not SessionLimitError)
                    || (policy == RetryPolicy.Idempotent && result.Status == 409 && error.ErrorType == "idempotency_in_progress");
            if (retryable && attempt < MaxRetries)
            {
                await Delay(RetryWait(result.Header) ?? Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            throw error;
        }
    }

    private async Task<Attempt> SendOnceAsync(HttpMethod method, Uri uri, string path, byte[]? body, string? contentType, bool stream, TimeSpan attemptTimeout, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri)
        {
            // HTTP/2 over TLS where the server offers it, so concurrent calls share a connection.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", stream ? "application/x-ndjson" : JsonContentType);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? JsonContentType);
        }
        foreach (var (name, value) in _headers)
        {
            if (request.Content is not null && name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (idempotencyKey is not null)
        {
            // The call's own key, the same on every attempt; never one of the client's headers.
            request.Headers.Remove("Idempotency-Key");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        // One deadline for the whole attempt: sending, the headers and the last byte of the body.
        // A streamed 2xx body is read after it, under the caller's token only.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(attemptTimeout);
        var clock = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(
                request,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && final != uri)
            {
                // A handler followed a redirect after all. Its answer is never used.
                Log(method, path, "redirected", null, clock);
                throw new DecisionNodeError(new ErrorDetails
                {
                    Message = "the HTTP handler followed a redirect; DecisionNode never follows one",
                });
            }
            var status = (int)response.StatusCode;
            var headers = CollectHeaders(response);
            if (stream && status is >= 200 and <= 299)
            {
                timeout.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                var streamed = new Attempt(status, headers, string.Empty, response);
                Log(method, path, status.ToString(CultureInfo.InvariantCulture), streamed.Header("x-request-id"), clock);
                response = null; // the caller owns it now
                return streamed;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var attempt = new Attempt(status, headers, Encoding.UTF8.GetString(bytes));
            Log(method, path, attempt.Status.ToString(CultureInfo.InvariantCulture), attempt.Header("x-request-id"), clock);
            return attempt;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log(method, path, "timeout", null, clock);
            throw new TimeoutError(new ErrorDetails
            {
                Message = $"the request timed out after {attemptTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s",
                Inner = ex,
            });
        }
        catch (HttpRequestException ex)
        {
            Log(method, path, "connection error", null, clock);
            throw new ConnectionError(new ErrorDetails
            {
                Message = "connection error: " + ex.Message,
                Inner = ex,
            });
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            Log(method, path, "connection error", null, clock);
            throw new ConnectionError(new ErrorDetails
            {
                Message = "connection error: " + ex.Message,
                Inner = ex,
            });
        }
        finally
        {
            response?.Dispose();
        }
    }

    private void Log(HttpMethod method, string path, string status, string? requestId, Stopwatch clock)
    {
        _debugLog?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"decisionnode: {method.Method} {path} {status} request_id={requestId ?? "-"} {clock.Elapsed.TotalMilliseconds:0} ms"));
    }

    internal static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.NonValidated)
        {
            headers[name] = string.Join(", ", values);
        }
        foreach (var (name, values) in response.Content.Headers.NonValidated)
        {
            headers[name] = string.Join(", ", values);
        }
        return headers;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ------------------------------------------------------------------ retries

    internal static bool IsRetryable(int status) => status is 408 or 429 or (>= 500 and <= 599);

    /// <summary>Exponential backoff from 0.5 s, with jitter, capped at 8 s.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, 0.5 * Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds * (0.75 + Random.Shared.NextDouble() * 0.25));
    }

    /// <summary>
    /// The wait the server asked for: <c>retry-after-ms</c>, else <c>Retry-After</c> in seconds
    /// or as an HTTP date; capped at 60 seconds.
    /// </summary>
    internal static TimeSpan? RetryWait(Func<string, string?> header) => RetryWait(header, DateTimeOffset.UtcNow);

    internal static TimeSpan? RetryWait(Func<string, string?> header, DateTimeOffset now)
    {
        var wait = ServerWait(header, now);
        return wait is { } w && w > MaxServerWait ? MaxServerWait : wait;
    }

    private static TimeSpan? ServerWait(Func<string, string?> header, DateTimeOffset now)
    {
        if (double.TryParse(header("retry-after-ms")?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && double.IsFinite(ms))
        {
            return TimeSpan.FromMilliseconds(Math.Min(Math.Max(0, ms), MaxServerWait.TotalMilliseconds));
        }
        var after = header("retry-after")?.Trim();
        if (string.IsNullOrEmpty(after))
        {
            return null;
        }
        if (double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s))
        {
            return TimeSpan.FromSeconds(Math.Min(Math.Max(0, s), MaxServerWait.TotalSeconds));
        }
        if (DateTimeOffset.TryParseExact(after, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when)
            || DateTimeOffset.TryParse(after, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when))
        {
            var wait = when - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    // ------------------------------------------------------------------ errors

    internal static DecisionNodeError ErrorFor(Attempt r) => ErrorFor(r.Status, r.Text, r.Header);

    /// <summary>The error class and fields a status, body and headers give.</summary>
    internal static DecisionNodeError ErrorFor(int status, string text, Func<string, string?> header)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return NonJsonErrorFor(status, header);
        }
        return ErrorFor(status, body, header);
    }

    /// <summary>The fixed status texts of an error whose body is not JSON, the same in every SDK.</summary>
    private static readonly IReadOnlyDictionary<int, string> StatusTexts = new Dictionary<int, string>
    {
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [402] = "Payment Required",
        [403] = "Forbidden",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [408] = "Request Timeout",
        [409] = "Conflict",
        [410] = "Gone",
        [413] = "Content Too Large",
        [422] = "Unprocessable Content",
        [429] = "Too Many Requests",
        [500] = "Internal Server Error",
        [502] = "Bad Gateway",
        [503] = "Service Unavailable",
        [504] = "Gateway Timeout",
        [529] = "Overloaded",
    };

    /// <summary>
    /// An error whose body is not JSON (an HTML or text page from the service's edge or a proxy,
    /// or an empty body): classified by its status alone, with no <c>error_type</c>, and described
    /// as <c>HTTP &lt;status&gt; &lt;text&gt;</c> with the request id when there is one. The body
    /// is never read into any field.
    /// </summary>
    internal static DecisionNodeError NonJsonErrorFor(int status, Func<string, string?> header)
    {
        var requestId = header("x-request-id");
        var message = StatusTexts.TryGetValue(status, out var text) ? $"HTTP {status} {text}" : $"HTTP {status}";
        if (requestId is not null)
        {
            message += $" (request id {requestId})";
        }
        var details = new ErrorDetails
        {
            Status = status,
            Message = message,
            RequestId = requestId,
            Reason = header("x-decisionnode-error"),
            Boosters = BoosterStatus.ParseHeader(header("x-decisionnode-boosters")),
        };
        return status switch
        {
            400 => new BadRequestError(details),
            401 => new AuthenticationError(details),
            402 => new InsufficientCreditError(details),
            403 => new PermissionDeniedError(details),
            404 => new NotFoundError(details),
            413 => new RequestTooLargeError(details),
            422 => new ValidationError(details, new JsonArray()),
            429 => new RateLimitError(details, RetryWait(header)),
            529 => new OverloadedError(details, RetryWait(header)),
            >= 500 and <= 599 => new ServerError(details),
            _ => new DecisionNodeError(details),
        };
    }

    internal static DecisionNodeError ErrorFor(int status, JsonNode? body, Func<string, string?> header)
    {
        var detail = (body as JsonObject)?["detail"];
        string? errorType = null, message = null, category = null;
        JsonArray? entries = null;
        switch (detail)
        {
            case JsonObject d:
                errorType = JsonValues.AsString(d["error_type"]);
                message = JsonValues.AsString(d["message"]) ?? string.Empty;
                category = JsonValues.AsString(d["category"]);
                break;
            case JsonArray list:
                entries = list;
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                message = v.GetValue<string>();
                break;
        }
        var details = new ErrorDetails
        {
            Status = status,
            ErrorType = errorType,
            Message = message,
            RequestId = header("x-request-id"),
            Reason = header("x-decisionnode-error"),
            Body = body,
            Boosters = BoosterStatus.ParseHeader(header("x-decisionnode-boosters")),
        };
        return status switch
        {
            400 when errorType == "max_tokens_exceeded" => new ContextLimitError(details),
            400 => new BadRequestError(details),
            401 => new AuthenticationError(details),
            402 => new InsufficientCreditError(details),
            403 when errorType == "refusal_error" => new RefusalError(details, category, Safety.Parse(header("x-decisionnode-safety"))),
            403 => new PermissionDeniedError(details),
            404 => new NotFoundError(details),
            413 => new RequestTooLargeError(details),
            422 => new ValidationError(details, entries),
            429 when errorType == "session_limit_exceeded" => new SessionLimitError(details, RetryWait(header)),
            429 => new RateLimitError(details, RetryWait(header)),
            529 => new OverloadedError(details, RetryWait(header)),
            >= 500 and <= 599 => new ServerError(details),
            // Every other status, 3xx and 410 included, is the base class carrying its status.
            _ => new DecisionNodeError(details),
        };
    }

    internal sealed class Attempt
    {
        private readonly IReadOnlyDictionary<string, string> _headers;

        public Attempt(int status, IReadOnlyDictionary<string, string> headers, string text, HttpResponseMessage? response = null)
        {
            Status = status;
            _headers = headers;
            Text = text;
            Response = response;
            Header = name => _headers.TryGetValue(name, out var v) ? v : null;
        }

        public int Status { get; }

        public string Text { get; }

        /// <summary>The unread response of a streamed 2xx answer; the caller disposes it.</summary>
        public HttpResponseMessage? Response { get; }

        public Func<string, string?> Header { get; }
    }
}
