using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Conformance;

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
            var mockPort = new Uri(mockUrl).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    public static async Task<object?> InvokeAsync(DecisionNodeClient client, JsonObject call)
    {
        try
        {
            return await InvokeCoreAsync(client, call);
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
            await InvokeCoreAsync(client, call);
            return null;
        }
        catch (DecisionNodeError error)
        {
            return error;
        }
    }

    private static Task<object?> InvokeCoreAsync(DecisionNodeClient client, JsonObject call)
    {
        var method = (string)call["method"]!;
        var args = (JsonObject)call["args"]!;
        return method switch
        {
            "decide" => Wrap(client.DecideAsync(Request(args))),
            "decide_many" => Wrap(client.DecideManyAsync(
                ((JsonArray)args["requests"]!).Select(r => Request((JsonObject)r!)).ToList(),
                args["concurrency"] is { } c ? (int)c : 8)),
            "check" => Wrap(client.CheckAsync(State(args), Value(args, "instructions"), Value(args, "true"), Value(args, "false"), Images(args), (string?)args["model"])),
            "classify" => args["options"] is JsonArray names
                ? Wrap(client.ClassifyAsync(State(args), Value(args, "instructions"), names.Select(n => (string)n!).ToList(), Images(args), (string?)args["model"]))
                : Wrap(client.ClassifyAsync(State(args), Value(args, "instructions"), Options((JsonObject)args["options"]!), Images(args), (string?)args["model"])),
            "score" => Wrap(client.ScoreAsync(State(args), Value(args, "instructions"), ((JsonArray)args["levels"]!).Select(l => (object?)l?.DeepClone()).ToList(), Images(args), (string?)args["model"])),
            "gate" => Wrap(client.GateAsync(Value(args, "action")!, Value(args, "context"), Images(args), (string?)args["model"])),
            "models" => Wrap(client.ModelsAsync()),
            "decide_raw" => Wrap(client.DecideRawAsync(args["body"]!.DeepClone())),
            _ => throw new InvalidOperationException("unknown method " + method),
        };
    }

    private static async Task<object?> Wrap<T>(Task<T> task) => await task;

    private static object? Value(JsonObject args, string key) => args[key]?.DeepClone();

    private static object? State(JsonObject args) => args.ContainsKey("state") ? Value(args, "state") : null;

    private static List<KeyValuePair<string, object?>> Options(JsonObject options) =>
        options.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value?.DeepClone())).ToList();

    private static DecideRequest Request(JsonObject args)
    {
        var questions = new Dictionary<string, Question>(StringComparer.Ordinal);
        foreach (var (id, q) in (JsonObject)args["questions"]!)
        {
            questions[id] = DecodeQuestion((JsonObject)q!);
        }
        return new DecideRequest
        {
            State = State(args),
            Questions = questions,
            Images = Images(args),
            Model = (string?)args["model"],
        };
    }

    private static Question DecodeQuestion(JsonObject q)
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
            }
        }
        return Question.FromJson(q);
    }

    private static List<Image>? Images(JsonObject args)
    {
        if (args["images"] is not JsonArray list)
        {
            return null;
        }
        return list.Select(i =>
        {
            var spec = (JsonObject)((JsonObject)i!)["$image"]!;
            var id = (string)spec["id"]!;
            if (spec["path"] is { } path)
            {
                return Image.FromFile(id, Path.Combine(Suite.Directory, (string)path!));
            }
            if (spec["url"] is { } url)
            {
                return Image.FromUrl(id, (string)url!);
            }
            return Image.FromBytes(id, Convert.FromBase64String((string)spec["data_base64"]!), (string?)spec["media_type"]);
        }).ToList();
    }

    // ------------------------------------------------------------------ results as neutral JSON

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
        return o;
    }

    public static JsonObject Answer(Answer a)
    {
        switch (a)
        {
            case TruthAnswer t:
                return new JsonObject { ["kind"] = "truth", ["truth"] = t.Truth };
            case ChoiceAnswer c:
                var cp = new JsonObject();
                foreach (var (k, v) in c.Probabilities)
                {
                    cp[k] = v;
                }
                return new JsonObject { ["kind"] = "choice", ["choice"] = c.Choice, ["confidence"] = c.Confidence, ["probabilities"] = cp };
            case ScoreAnswer s:
                var sp = new JsonObject();
                foreach (var (k, v) in s.Probabilities)
                {
                    sp[k.ToString(System.Globalization.CultureInfo.InvariantCulture)] = v;
                }
                var legend = new JsonObject();
                foreach (var (k, v) in s.Legend)
                {
                    legend[k.ToString(System.Globalization.CultureInfo.InvariantCulture)] = v?.DeepClone();
                }
                return new JsonObject { ["kind"] = "score", ["score"] = s.Score, ["confidence"] = s.Confidence, ["probabilities"] = sp, ["legend"] = legend };
            default:
                return new JsonObject { ["kind"] = "unknown", ["type"] = a.Type, ["raw"] = a.Raw.DeepClone() };
        }
    }

    public static JsonObject Decision(Decision d)
    {
        var answers = new JsonObject();
        foreach (var (k, v) in d.Answers)
        {
            answers[k] = Answer(v);
        }
        return new JsonObject
        {
            ["model"] = d.Model,
            ["request_id"] = d.RequestId,
            ["latency_ms"] = d.LatencyMs,
            ["usage"] = new JsonObject { ["input_tokens"] = d.Usage.InputTokens, ["output_tokens"] = d.Usage.OutputTokens },
            ["safety"] = Safety(d.Safety),
            ["answers"] = answers,
            ["raw"] = d.Raw.DeepClone(),
        };
    }

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
}
