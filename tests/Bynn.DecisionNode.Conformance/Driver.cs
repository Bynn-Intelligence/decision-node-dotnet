using System.Globalization;
using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Conformance;

/// <summary>What <c>supervise</c> returned, with the events its callback collected.</summary>
public sealed record Supervised(List<SessionEvent> Replies, SessionClosed? Closed);

/// <summary>What <c>run_batch</c> returned, its results read.</summary>
public sealed record BatchRunResult(Batch Batch, List<BatchResult> Results);

/// <summary>Maps a case's neutral call onto this library and its results back onto neutral JSON.</summary>
public static class Driver
{
    private static readonly string[] EnvVars = { "DECISIONNODE_API_KEY", "DECISIONNODE_BASE_URL", "DECISIONNODE_MODEL" };

    /// <summary>Builds the client a case describes. The environment is set while it is built, then restored.</summary>
    public static DecisionNodeClient MakeClient(JsonObject? clientSpec, JsonObject? env, string mockUrl, IDictionary<string, string> extraHeaders)
    {
        var saved = EnvVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in EnvVars)
            {
                Environment.SetEnvironmentVariable(v, null);
            }
            var mockPort = new Uri(mockUrl).Port.ToString(CultureInfo.InvariantCulture);
            string Fill(string text) => text.Replace("{mock_url}", mockUrl).Replace("{mock_port}", mockPort);
            foreach (var (name, value) in env ?? new JsonObject())
            {
                Environment.SetEnvironmentVariable(name, Fill((string)value!));
            }
            var spec = clientSpec ?? new JsonObject();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (spec["headers"] is JsonObject h)
            {
                foreach (var (k, v) in h)
                {
                    headers[k] = (string)v!;
                }
            }
            foreach (var (k, v) in extraHeaders)
            {
                headers[k] = v;
            }
            string? baseUrl = mockUrl;
            if (spec.ContainsKey("base_url"))
            {
                baseUrl = (string?)spec["base_url"] is { } given ? Fill(given) : null;
            }
            HttpClient? http = null;
            if ((string?)spec["http_client"] == "caller_following_redirects")
            {
                // A client of the caller's own, left at the .NET default: it follows redirects.
                http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            }
            return new DecisionNodeClient(new DecisionNodeClientOptions
            {
                ApiKey = (string?)spec["api_key"],
                BaseUrl = baseUrl,
                Model = (string?)spec["model"],
                Timeout = spec["timeout"] is { } t ? TimeSpan.FromSeconds((double)t) : null,
                MaxRetries = spec["max_retries"] is { } r ? (int)r : null,
                InlineThreshold = spec["inline_threshold"] is { } it ? (int)it : null,
                Headers = headers,
                HttpClient = http,
            });
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    /// <summary>Runs the call. Errors are returned, not thrown, so the caller can check them.</summary>
    public static async Task<object?> InvokeAsync(DecisionNodeClient client, JsonObject call, Dictionary<string, object?>? ctx = null)
    {
        try
        {
            return await InvokeCoreAsync(client, call, ctx ?? new Dictionary<string, object?>());
        }
        catch (DecisionNodeError error)
        {
            return error;
        }
        catch (ArgumentException error)
        {
            return error;
        }
    }

    /// <summary>
    /// Runs the call and catches only through a clause written against the exported base
    /// name <c>DecisionNodeError</c>, for cases with <c>catch_as</c>. Any other exception
    /// escapes and fails the case.
    /// </summary>
    public static async Task<DecisionNodeError?> InvokeCatchingBaseAsync(DecisionNodeClient client, JsonObject call)
    {
        try
        {
            await InvokeCoreAsync(client, call, new Dictionary<string, object?>());
            return null;
        }
        catch (DecisionNodeError error)
        {
            return error;
        }
    }

    private static Task<object?> InvokeCoreAsync(DecisionNodeClient client, JsonObject call, Dictionary<string, object?> ctx)
    {
        var method = (string)call["method"]!;
        var args = (JsonObject)call["args"]!;
        JsonNode? Arg(string key) => Plain(args[key], ctx);
        string? Text(string key) => (string?)Arg(key);
        double? Number(string key) => Arg(key) is { } n ? (double)n : null;
        return method switch
        {
            "decide" => Wrap(client.DecideAsync(Request(args, ctx))),
            "decide_many" => Wrap(client.DecideManyAsync(
                ((JsonArray)args["requests"]!).Select(r => Request((JsonObject)r!, ctx)).ToList(),
                args["concurrency"] is { } c ? (int)c : 8)),
            "check" => Wrap(client.CheckAsync(State(args, ctx), Arg("instructions"), Arg("true"), Arg("false"), Images(args, ctx), Text("model"))),
            "classify" => args["options"] is JsonArray names
                ? Wrap(client.ClassifyAsync(State(args, ctx), Arg("instructions"), names.Select(n => (string)n!).ToList(), Images(args, ctx), Text("model")))
                : Wrap(client.ClassifyAsync(State(args, ctx), Arg("instructions"), Options((JsonObject)args["options"]!), Images(args, ctx), Text("model"))),
            "score" => Wrap(client.ScoreAsync(State(args, ctx), Arg("instructions"), ((JsonArray)args["levels"]!).Select(l => (object?)l?.DeepClone()).ToList(), Images(args, ctx), Text("model"))),
            "number" => Wrap(client.NumberAsync(State(args, ctx), Arg("instructions"), Number("min"), Number("max"), Number("step"), Images(args, ctx), Text("model"))),
            "gate" => Wrap(client.GateAsync(Arg("action")!, Arg("context"), Images(args, ctx), Text("model"))),
            "models" => Wrap(client.ModelsAsync()),
            "decide_raw" => Wrap(client.DecideRawAsync(args["body"]!.DeepClone())),
            "systemone" => Wrap(client.SystemoneAsync(State(args, ctx), Questions((JsonObject)args["questions"]!, ctx), Images(args, ctx), Text("model"))),
            "upload" => Wrap(client.UploadAsync(Images(args, ctx)!)),
            "open_session" => Wrap(client.OpenSessionAsync(SessionBody(args, ctx))),
            "supervise" => SuperviseAsync(client, args, ctx),
            "run_batch" => RunBatchAsync(client, args, ctx),
            "batches.create" => Wrap(client.Batches.CreateAsync(BatchRequests(args, ctx))),
            "batches.add" => Wrap(client.Batches.AddAsync(Text("batch_id")!, BatchRequests(args, ctx))),
            "batches.finalize" => Wrap(client.Batches.FinalizeAsync(Text("batch_id")!)),
            "batches.get" => Wrap(client.Batches.GetAsync(Text("batch_id")!)),
            "batches.list" => Wrap(client.Batches.ListAsync(args["offset"] is { } o ? (int)o : null, args["limit"] is { } l ? (int)l : null)),
            "batches.results" => ReadAll(client.Batches.ResultsAsync(Text("batch_id")!)),
            "batches.cancel" => Wrap(client.Batches.CancelAsync(Text("batch_id")!)),
            _ => throw new InvalidOperationException("unknown method " + method),
        };
    }

    private static async Task<object?> Wrap<T>(Task<T> task) => await task;

    private static async Task<object?> ReadAll(IAsyncEnumerable<BatchResult> results)
    {
        var list = new List<BatchResult>();
        await foreach (var r in results)
        {
            list.Add(r);
        }
        return list;
    }

    private static async Task<object?> RunBatchAsync(DecisionNodeClient client, JsonObject args, Dictionary<string, object?> ctx)
    {
        var run = await client.RunBatchAsync(
            BatchRequests(args, ctx),
            args["poll_interval"] is { } p ? TimeSpan.FromSeconds((double)p) : null,
            args["timeout"] is { } t ? TimeSpan.FromSeconds((double)t) : null);
        var results = (List<BatchResult>)(await ReadAll(run.Results))!;
        return new BatchRunResult(run.Batch, results);
    }

    private static async Task<object?> SuperviseAsync(DecisionNodeClient client, JsonObject args, Dictionary<string, object?> ctx)
    {
        var body = SessionBody((JsonObject)args["body"]!, ctx);
        var readings = ((JsonArray)args["feed"]!).Select(r => Frame((JsonObject)r!, ctx)).ToList();
        var interval = TimeSpan.FromMilliseconds(args["feed_interval_ms"] is { } ms ? (double)ms : 0);

        async IAsyncEnumerable<SessionFrame> Feed()
        {
            for (var i = 0; i < readings.Count; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(interval);
                }
                yield return readings[i];
            }
        }

        var replies = new List<SessionEvent>();
        var closed = await client.SuperviseAsync(body, Feed(), ev =>
        {
            lock (replies)
            {
                replies.Add(ev);
            }
        });
        return new Supervised(replies, closed);
    }

    // ------------------------------------------------------------------ arguments

    /// <summary>Plain data with <c>$text</c> and <c>$ref</c> resolved.</summary>
    public static JsonNode? Plain(JsonNode? value, Dictionary<string, object?> ctx)
    {
        switch (value)
        {
            case JsonObject o when o.Count == 1 && o.First().Key == "$text":
                var spec = (JsonObject)o.First().Value!;
                return JsonValue.Create(string.Concat(Enumerable.Repeat((string)spec["repeat"]!, (int)spec["times"]!)));
            case JsonObject o when o.Count == 1 && o.First().Key == "$ref":
                return Resolve((string)o.First().Value!, ctx);
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                {
                    copy[k] = Plain(v, ctx);
                }
                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(v => Plain(v, ctx)).ToArray());
            default:
                return value?.DeepClone();
        }
    }

    private static JsonNode? Resolve(string path, Dictionary<string, object?> ctx)
    {
        var parts = path.Split('.');
        JsonNode? cur = Normalise(ctx[parts[0]]);
        foreach (var part in parts.Skip(1))
        {
            cur = cur switch
            {
                JsonObject o => o[part],
                JsonArray a => a[int.Parse(part, CultureInfo.InvariantCulture)],
                _ => throw new InvalidOperationException($"$ref {path}: cannot read {part}"),
            };
        }
        return cur?.DeepClone();
    }

    private static object? State(JsonObject args, Dictionary<string, object?> ctx) => args.ContainsKey("state") ? Plain(args["state"], ctx) : null;

    private static List<KeyValuePair<string, object?>> Options(JsonObject options) =>
        options.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value?.DeepClone())).ToList();

    private static DecideRequest Request(JsonObject args, Dictionary<string, object?> ctx) => new()
    {
        State = State(args, ctx),
        Questions = Questions((JsonObject)args["questions"]!, ctx),
        Images = Images(args, ctx),
        Model = (string?)args["model"],
    };

    private static List<BatchRequest> BatchRequests(JsonObject args, Dictionary<string, object?> ctx) =>
        ((JsonArray)args["requests"]!).Select(r =>
        {
            var o = (JsonObject)r!;
            return new BatchRequest
            {
                CustomId = (string)Plain(o["custom_id"], ctx)!,
                State = State(o, ctx),
                Questions = Questions((JsonObject)o["questions"]!, ctx),
                Images = Images(o, ctx),
                Model = (string?)o["model"],
            };
        }).ToList();

    private static SessionOptions SessionBody(JsonObject args, Dictionary<string, object?> ctx) => new()
    {
        State = args.ContainsKey("state") ? Plain(args["state"], ctx) : null,
        Questions = Questions((JsonObject)args["questions"]!, ctx),
        Instructions = args.ContainsKey("instructions") ? Plain(args["instructions"], ctx) : null,
        Model = (string?)args["model"],
        Window = args["window"] is { } w ? (int)w : null,
        TtlSeconds = args["ttl_seconds"] is { } t ? (int)t : null,
    };

    public static SessionFrame Frame(JsonObject args, Dictionary<string, object?> ctx) => new()
    {
        Frame = args.ContainsKey("frame") ? Plain(args["frame"], ctx) : null,
        Image = args["image"] is JsonObject image ? ImageOf(image, ctx) : null,
        Questions = args["questions"] is JsonArray qs ? qs.Select(q => (string)q!).ToList() : null,
        Reset = args["reset"] is { } r ? (bool)r : null,
        Seq = args["seq"] is { } s ? (long)s : null,
    };

    private static Dictionary<string, Question> Questions(JsonObject map, Dictionary<string, object?> ctx)
    {
        var questions = new Dictionary<string, Question>(StringComparer.Ordinal);
        foreach (var (id, q) in map)
        {
            questions[id] = DecodeQuestion((JsonObject)q!, ctx);
        }
        return questions;
    }

    private static Question DecodeQuestion(JsonObject q, Dictionary<string, object?> ctx)
    {
        if (q.Count == 1)
        {
            var (key, value) = q.First();
            var a = value as JsonObject;
            switch (key)
            {
                case "$truth":
                    return Question.Truth(a!["instructions"]?.DeepClone(), a["true"]?.DeepClone(), a["false"]?.DeepClone());
                case "$choice":
                    return a!["options"] is JsonArray names
                        ? Question.Choice(a["instructions"]?.DeepClone(), names.Select(n => (string)n!).ToList())
                        : Question.Choice(a["instructions"]?.DeepClone(), Options((JsonObject)a["options"]!));
                case "$score":
                    return Question.Score(a!["instructions"]?.DeepClone(), ((JsonArray)a["levels"]!).Select(l => (object?)l?.DeepClone()).ToList());
                case "$number":
                    return Question.Number(
                        a!["instructions"]?.DeepClone(),
                        a["min"] is { } lo ? (double)lo : null,
                        a["max"] is { } hi ? (double)hi : null,
                        a["step"] is { } st ? (double)st : null);
            }
        }
        return Question.FromJson((JsonObject)Plain(q, ctx)!);
    }

    private static List<Image>? Images(JsonObject args, Dictionary<string, object?> ctx) =>
        args["images"] is JsonArray list ? list.Select(i => ImageOf((JsonObject)i!, ctx)).ToList() : null;

    private static Image ImageOf(JsonObject wrapper, Dictionary<string, object?> ctx)
    {
        var spec = (JsonObject)Plain(wrapper["$image"], ctx)!;
        var id = (string)spec["id"]!;
        var mediaType = (string?)spec["media_type"];
        if (spec["url"] is { } url)
        {
            return Image.FromUrl(id, (string)url!);
        }
        if (spec["upload_id"] is { } uploadId)
        {
            return Image.FromUploadId(id, (string)uploadId!, mediaType);
        }
        if (spec["path"] is { } path && spec["pad_to"] is null)
        {
            return Image.FromFile(id, Path.Combine(Suite.Directory, (string)path!));
        }
        var data = spec["path"] is { } p
            ? File.ReadAllBytes(Path.Combine(Suite.Directory, (string)p!))
            : Convert.FromBase64String((string)spec["data_base64"]!);
        if (spec["pad_to"] is { } padTo && (int)padTo > data.Length)
        {
            // The image's own bytes followed by zeros: the header (type, pixels) is unchanged.
            Array.Resize(ref data, (int)padTo);
        }
        return Image.FromBytes(id, data, mediaType);
    }

    // ------------------------------------------------------------------ results as neutral JSON

    /// <summary>Any result as JSON, for <c>$ref</c> and the comparisons.</summary>
    public static JsonNode? Normalise(object? value) => value switch
    {
        null => null,
        Decision d => Decision(d),
        Answer a => Answer(a),
        Batch b => Batch(b),
        BatchPage p => Page(p),
        Session s => Session(s),
        IReadOnlyList<string> ids => new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()),
        JsonNode n => n,
        _ => throw new InvalidOperationException("no neutral form for " + value.GetType().Name),
    };

    public static JsonNode? Safety(Safety? s)
    {
        if (s is null)
        {
            return null;
        }
        if (!s.Checked)
        {
            return new JsonObject { ["checked"] = false, ["reason"] = s.Reason };
        }
        var o = new JsonObject { ["checked"] = true, ["harm"] = s.Harm, ["self_harm"] = s.SelfHarm };
        foreach (var (k, v) in s.Scores)
        {
            o[k] = v;
        }
        o["flagged"] = new JsonArray(s.Flagged.Select(f => (JsonNode?)f).ToArray());
        if (s.Blocked is not null)
        {
            o["blocked"] = s.Blocked;
        }
        return o;
    }

    public static JsonObject Answer(Answer a)
    {
        switch (a)
        {
            case TruthAnswer t:
                return new JsonObject { ["kind"] = "truth", ["truth"] = t.Truth };
            case ChoiceAnswer c:
                return new JsonObject { ["kind"] = "choice", ["choice"] = c.Choice, ["confidence"] = c.Confidence, ["probabilities"] = Map(c.Probabilities) };
            case ScoreAnswer s:
                var sp = new JsonObject();
                foreach (var (k, v) in s.Probabilities)
                {
                    sp[k.ToString(CultureInfo.InvariantCulture)] = v;
                }
                var legend = new JsonObject();
                foreach (var (k, v) in s.Legend)
                {
                    legend[k.ToString(CultureInfo.InvariantCulture)] = v?.DeepClone();
                }
                return new JsonObject { ["kind"] = "score", ["score"] = s.Score, ["confidence"] = s.Confidence, ["probabilities"] = sp, ["legend"] = legend };
            case NumberAnswer n:
                return new JsonObject { ["kind"] = "number", ["number"] = n.Number, ["expected"] = n.Expected, ["confidence"] = n.Confidence, ["probabilities"] = Map(n.Probabilities) };
            default:
                return new JsonObject { ["kind"] = "unknown", ["type"] = a.Type, ["raw"] = a.Raw.DeepClone() };
        }
    }

    private static JsonObject Map(IReadOnlyDictionary<string, double> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values)
        {
            o[k] = v;
        }
        return o;
    }

    private static JsonObject Answers(IReadOnlyDictionary<string, Answer> answers)
    {
        var o = new JsonObject();
        foreach (var (k, v) in answers)
        {
            o[k] = Answer(v);
        }
        return o;
    }

    private static JsonObject? Usage(Usage? u) => u is null ? null : new JsonObject { ["input_tokens"] = u.InputTokens, ["output_tokens"] = u.OutputTokens };

    public static JsonObject Decision(Decision d) => new()
    {
        ["model"] = d.Model,
        ["request_id"] = d.RequestId,
        ["latency_ms"] = d.LatencyMs,
        ["usage"] = Usage(d.Usage),
        ["safety"] = Safety(d.Safety),
        ["answers"] = Answers(d.Answers),
        ["raw"] = d.Raw.DeepClone(),
    };

    public static JsonObject Error(DecisionNodeError e) => new()
    {
        ["status"] = e.Status,
        ["error_type"] = e.ErrorType,
        // The error's own message; an empty one reads as empty.
        ["message"] = e.Message,
        ["request_id"] = e.RequestId,
        ["reason"] = e.Reason,
        ["category"] = (e as RefusalError)?.Category,
        ["safety"] = Safety((e as RefusalError)?.Safety),
        ["entries"] = (e as ValidationError)?.Entries?.DeepClone(),
        ["retry_after"] = e switch
        {
            RateLimitError r => r.RetryAfter?.TotalSeconds,
            OverloadedError o => o.RetryAfter?.TotalSeconds,
            _ => null,
        },
    };

    public static JsonObject Model(ModelInfo m)
    {
        var limits = new JsonObject();
        foreach (var (k, v) in m.Limits)
        {
            limits[k] = v?.DeepClone();
        }
        return new JsonObject { ["name"] = m.Name, ["description"] = m.Description, ["release_date"] = m.ReleaseDate, ["limits"] = limits };
    }

    /// <summary>A batch from its typed fields, so the fields are what is checked.</summary>
    public static JsonObject Batch(Batch b) => new()
    {
        ["id"] = b.Id,
        ["state"] = b.State,
        ["counters"] = new JsonObject
        {
            ["requests"] = b.Counters.Requests,
            ["queued"] = b.Counters.Queued,
            ["in_progress"] = b.Counters.InProgress,
            ["completed"] = b.Counters.Completed,
            ["failed"] = b.Counters.Failed,
            ["expired"] = b.Counters.Expired,
            ["cancelled"] = b.Counters.Cancelled,
        },
        ["created_at"] = b.CreatedAt,
        ["finalized_at"] = b.FinalizedAt,
        ["expires_at"] = b.ExpiresAt,
        ["ended_at"] = b.EndedAt,
        ["results_url"] = b.ResultsUrl,
        ["results_expire_at"] = b.ResultsExpireAt,
        ["price_factor"] = b.PriceFactor,
        ["usage"] = Usage(b.Usage),
        ["error"] = b.ErrorType is null && b.ErrorMessage is null ? null : new JsonObject { ["error_type"] = b.ErrorType, ["message"] = b.ErrorMessage },
    };

    public static JsonObject Page(BatchPage p) => new()
    {
        ["batches"] = new JsonArray(p.Batches.Select(b => (JsonNode?)Batch(b)).ToArray()),
        ["offset"] = p.Offset,
        ["limit"] = p.Limit,
        ["total"] = p.Total,
        ["next_offset"] = p.NextOffset,
    };

    /// <summary>The open response's fields, with its answers typed.</summary>
    public static JsonObject Session(Session s)
    {
        var o = (JsonObject)s.Raw.DeepClone();
        o["session_id"] = s.SessionId;
        o["stream_url"] = s.StreamUrl;
        o["answers"] = Answers(s.Answers);
        return o;
    }

    public static JsonObject Event(SessionEvent ev)
    {
        switch (ev)
        {
            case Reply r:
                var reply = new JsonObject
                {
                    ["seq"] = r.Seq,
                    ["answers"] = Answers(r.Answers),
                    ["usage"] = Usage(r.Usage),
                    ["latency_ms"] = r.LatencyMs,
                    ["safety"] = Safety(r.Safety),
                };
                if (r.Dropped is not null)
                {
                    reply["dropped"] = new JsonArray(r.Dropped.Select(d => (JsonNode?)d).ToArray());
                }
                return new JsonObject { ["reply"] = reply };
            case FrameError f:
                var error = new JsonObject { ["seq"] = f.Seq, ["error_type"] = f.ErrorType, ["message"] = f.Message };
                if (f.Category is not null)
                {
                    error["category"] = f.Category;
                }
                if (f.RetryAfterMs is not null)
                {
                    error["retry_after_ms"] = f.RetryAfterMs;
                }
                if (f.Usage is not null)
                {
                    error["usage"] = Usage(f.Usage);
                }
                if (f.Safety is not null)
                {
                    error["safety"] = Safety(f.Safety);
                }
                return new JsonObject { ["frame_error"] = error };
            case SessionClosed c:
                return new JsonObject { ["closed"] = new JsonObject { ["code"] = c.Code, ["reopen"] = c.Reopen } };
            default:
                throw new InvalidOperationException("unknown session event " + ev.GetType().Name);
        }
    }
}
