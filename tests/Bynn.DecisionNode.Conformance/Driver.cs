using System.Globalization;
using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Conformance;

/// <summary>What <c>supervise</c> returned, with the events its callback collected.</summary>
public sealed record Supervised(List<SessionEvent> Replies, SessionClosed? Closed);

/// <summary>What a check returned when it found nothing.</summary>
public sealed record Checked;

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
            var client = new DecisionNodeClient(new DecisionNodeClientOptions
            {
                ApiKey = (string?)spec["api_key"],
                BaseUrl = baseUrl,
                Model = (string?)spec["model"],
                Timeout = spec["timeout"] is { } t ? TimeSpan.FromSeconds((double)t) : null,
                MaxRetries = spec["max_retries"] is { } r ? (int)r : null,
                InlineThreshold = spec["inline_threshold"] is { } it ? (int)it : null,
                UploadTimeout = spec["upload_timeout"] is { } ut ? TimeSpan.FromSeconds((double)ut) : null,
                Headers = headers,
                HttpClient = http,
            });
            if ((string?)spec["clock"] == "mock")
            {
                // The SDK's clock reads the mock's, so the case can move the time it judges uploads by.
                var session = extraHeaders["x-mock-session"];
                client.UtcNow = () => MockClock(mockUrl, session);
            }
            return client;
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    private static readonly HttpClient ClockHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static DateTimeOffset MockClock(string mockUrl, string session)
    {
        var text = ClockHttp.GetStringAsync($"{mockUrl}/_mock/clock?session={Uri.EscapeDataString(session)}").GetAwaiter().GetResult();
        var seconds = (double)JsonNode.Parse(text)!["epoch_seconds"]!;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
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
        if ((bool?)call["check"] == true)
        {
            // The same request handed to the library's check instead of being sent.
            Check(client, method, args, ctx);
            return Task.FromResult<object?>(new Checked());
        }
        JsonNode? Arg(string key) => Plain(args[key], ctx);
        string? Text(string key) => (string?)Arg(key);
        double? Number(string key) => Arg(key) is { } n ? (double)n : null;
        return method switch
        {
            "decide" => Wrap(client.DecideAsync(Request(args, ctx))),
            "decide_many" => Wrap(client.DecideManyAsync(
                ((JsonArray)args["requests"]!).Select(r => Request((JsonObject)r!, ctx)).ToList(),
                args["concurrency"] is { } c ? (int)c : 8)),
            "check" => Wrap(client.CheckAsync(State(args, ctx), Arg("instructions"), Arg("true"), Arg("false"), Images(args, ctx), Videos(args, ctx), Text("model"), Boosters(args, ctx), Fallback(args, ctx))),
            "classify" => args["options"] is JsonArray names
                ? Wrap(client.ClassifyAsync(State(args, ctx), Arg("instructions"), names.Select(n => (string)n!).ToList(), Images(args, ctx), Videos(args, ctx), Text("model"), Boosters(args, ctx), Fallback(args, ctx)))
                : Wrap(client.ClassifyAsync(State(args, ctx), Arg("instructions"), Options((JsonObject)args["options"]!), Images(args, ctx), Videos(args, ctx), Text("model"), Boosters(args, ctx), Fallback(args, ctx))),
            "score" => Wrap(client.ScoreAsync(State(args, ctx), Arg("instructions"), ((JsonArray)args["levels"]!).Select(l => (object?)l?.DeepClone()).ToList(), Images(args, ctx), Videos(args, ctx), Text("model"), Boosters(args, ctx), Fallback(args, ctx))),
            "number" => Wrap(client.NumberAsync(State(args, ctx), Arg("instructions"), Number("min"), Number("max"), Number("step"), Images(args, ctx), Videos(args, ctx), Text("model"), Boosters(args, ctx), Fallback(args, ctx))),
            "gate" => Wrap(client.GateAsync(Arg("action")!, Arg("context"), Images(args, ctx), Videos(args, ctx), Text("model"), Boosters(args, ctx), Fallback(args, ctx))),
            "models" => Wrap(client.ModelsAsync()),
            "decide_raw" => Wrap(client.DecideRawAsync(args["body"]!.DeepClone())),
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
            "collections.create" => Wrap(client.Collections.CreateAsync(Text("name")!, args.ContainsKey("type") ? Text("type")! : "face")),
            "collections.list" => Wrap(client.Collections.ListAsync()),
            "collections.get" => Wrap(client.Collections.GetAsync(Text("collection_id")!)),
            "collections.delete" => Wrap(client.Collections.DeleteAsync(Text("collection_id")!)),
            "collections.enroll" => Wrap(client.Collections.EnrollAsync(
                Text("collection_id")!,
                args["image"] is JsonObject image ? ImageOf(image, ctx) : null!,
                Text("reference")!,
                Text("label"),
                args["metadata"] is JsonObject md ? (JsonObject)Plain(md, ctx)! : null)),
            "collections.update_item" => Wrap(client.Collections.UpdateItemAsync(
                Text("collection_id")!,
                Text("item_id")!,
                new CollectionItemUpdate
                {
                    Reference = Text("reference"),
                    Label = Text("label"),
                    ClearLabel = args.ContainsKey("label") && args["label"] is null,
                    Metadata = args["metadata"] is JsonObject um ? (JsonObject)Plain(um, ctx)! : null,
                })),
            "collections.list_items" => Wrap(client.Collections.ListItemsAsync(
                Text("collection_id")!,
                args["offset"] is { } io ? (int)io : null,
                args["limit"] is { } il ? (int)il : null,
                Text("reference"),
                Text("reference_prefix"))),
            "keys.create" => Wrap(client.Keys.CreateAsync(
                new KeyCreate
                {
                    Name = Text("name")!,
                    DeviceId = Text("device_id"),
                    Labels = LabelsOf(args["labels"]),
                    Limits = LimitsOf(args["limits"]),
                    DailySpendCapUsd = args["daily_spend_cap"] is JsonObject cap ? (decimal)cap["usd"]! : null,
                    ExpiresAt = Text("expires_at"),
                    MaySearchCollections = args["may_search_collections"] is { } msc ? (bool)msc : null,
                },
                Text("idempotency_key"))),
            "keys.list" => Wrap(client.Keys.ListAsync(KeyListOf(args, ctx), Text("cursor"))),
            "keys.all" => Wrap(client.Keys.AllAsync(KeyListOf(args, ctx))),
            "keys.update" => Wrap(client.Keys.UpdateAsync(Text("key_id")!, new KeyUpdate
            {
                Name = Text("name"),
                Labels = LabelsOf(args["labels"]),
                ClearLabels = args.ContainsKey("labels") && args["labels"] is null,
                Limits = LimitsOf(args["limits"]),
                ClearLimits = args.ContainsKey("limits") && args["limits"] is null,
                DailySpendCapUsd = args["daily_spend_cap"] is JsonObject ucap ? (decimal)ucap["usd"]! : null,
                ClearDailySpendCap = args.ContainsKey("daily_spend_cap") && args["daily_spend_cap"] is null,
                ExpiresAt = Text("expires_at"),
                ClearExpiresAt = args.ContainsKey("expires_at") && args["expires_at"] is null,
                MaySearchCollections = args["may_search_collections"] is { } umsc ? (bool)umsc : null,
            })),
            "keys.revoke" => Wrap(client.Keys.RevokeAsync(Text("key_id")!)),
            "keys.revoke_by_labels" => Wrap(client.Keys.RevokeByLabelsAsync(
                LabelsOf(args["labels"]) ?? new Dictionary<string, string>(),
                Plain(args["device_ids"], ctx) is JsonArray devices ? devices.Select(d => (string)d!).ToList() : null,
                args["dry_run"] is { } dry ? (bool)dry : null)),
            "usage.key" => Wrap(client.Usage.KeyAsync(Text("key_id")!, Text("start"), Text("end"), Text("granularity"), Text("group_by"))),
            "usage.list" => Wrap(client.Usage.ListAsync(UsageQueryOf(args, ctx), Text("cursor"))),
            "usage.all" => Wrap(client.Usage.AllAsync(UsageQueryOf(args, ctx))),
            "collections.delete_item" => Wrap(client.Collections.DeleteItemAsync(Text("collection_id")!, Text("item_id")!)),
            _ => throw new InvalidOperationException("unknown method " + method),
        };
    }

    private static void Check(DecisionNodeClient client, string method, JsonObject args, Dictionary<string, object?> ctx)
    {
        switch (method)
        {
            case "decide":
                client.CheckRequest(Request(args, ctx));
                break;
            case "open_session":
                client.CheckRequest(SessionBody(args, ctx));
                break;
            case "batches.create":
                client.Batches.CheckCreate(BatchRequests(args, ctx));
                break;
            case "batches.add":
                client.Batches.CheckAdd(BatchRequests(args, ctx));
                break;
            default:
                throw new InvalidOperationException("no check for " + method);
        }
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
            case JsonObject o when o.Count == 1 && o.First().Key == "$list":
                var listSpec = (JsonObject)o.First().Value!;
                var count = (int)listSpec["count"]!;
                return new JsonArray(Enumerable.Range(0, count).Select(i => Plain(WithIndex(listSpec["item"], i), ctx)).ToArray());
            case JsonObject o when o.Count == 1 && o.First().Key == "$nest":
                var nest = (JsonObject)o.First().Value!;
                var inner = Plain(nest["value"], ctx);
                for (var i = 0; i < (int)nest["depth"]!; i++)
                {
                    inner = new JsonArray(inner);
                }
                return inner;
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

    /// <summary>A copy with <c>{i}</c> in every string replaced by the index.</summary>
    private static JsonNode? WithIndex(JsonNode? value, int i) => value switch
    {
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String =>
            JsonValue.Create(((string)v!).Replace("{i}", i.ToString(CultureInfo.InvariantCulture))),
        JsonObject o => new JsonObject(o.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, WithIndex(kv.Value, i)))),
        JsonArray a => new JsonArray(a.Select(x => WithIndex(x, i)).ToArray()),
        _ => value?.DeepClone(),
    };

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

    private static Dictionary<string, string>? LabelsOf(JsonNode? labels) =>
        labels is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => (string)kv.Value!) : null;

    private static KeyLimits? LimitsOf(JsonNode? limits) =>
        limits is JsonObject o ? new KeyLimits(o["requests_per_second"] is { } r ? (int)r : null, o["tokens_per_second"] is { } t ? (int)t : null) : null;

    private static KeyListQuery KeyListOf(JsonObject args, Dictionary<string, object?> ctx) => new()
    {
        Labels = LabelsOf(args["labels"]),
        DeviceId = (string?)Plain(args["device_id"], ctx),
        Status = (string?)Plain(args["status"], ctx),
        Limit = args["limit"] is { } l ? (int)l : null,
    };

    private static UsageQuery UsageQueryOf(JsonObject args, Dictionary<string, object?> ctx)
    {
        string? Text(string key) => (string?)Plain(args[key], ctx);
        return new UsageQuery
        {
            Start = Text("start")!,
            End = Text("end")!,
            Granularity = Text("granularity"),
            GroupBy = Text("group_by"),
            Labels = args["labels"] is JsonObject labels ? labels.ToDictionary(kv => kv.Key, kv => (string)kv.Value!) : null,
            DeviceId = Text("device_id"),
            KeyIds = Plain(args["key_ids"], ctx) is JsonArray ids ? ids.Select(i => (string)i!).ToList() : null,
            Scope = Text("scope"),
            Status = Text("status"),
            IncludeEmpty = args["include_empty"] is { } e ? (bool)e : null,
            Limit = args["limit"] is { } l ? (int)l : null,
        };
    }

    private static DecideRequest Request(JsonObject args, Dictionary<string, object?> ctx) => new()
    {
        State = State(args, ctx),
        Questions = Questions((JsonObject)args["questions"]!, ctx),
        Images = Images(args, ctx),
        Videos = Videos(args, ctx),
        Model = (string?)args["model"],
        Boosters = Boosters(args, ctx),
        Fallback = Fallback(args, ctx),
    };

    private static List<BatchRequest> BatchRequests(JsonObject args, Dictionary<string, object?> ctx) =>
        ((JsonArray)Plain(args["requests"], ctx)!).Select(r =>
        {
            var o = (JsonObject)r!;
            return new BatchRequest
            {
                // A missing custom_id is passed as missing: the library refuses it locally.
                CustomId = ((string?)Plain(o["custom_id"], ctx))!,
                State = State(o, ctx),
                Questions = Questions((JsonObject)o["questions"]!, ctx),
                Images = Images(o, ctx),
                Videos = Videos(o, ctx),
                Model = (string?)o["model"],
                Boosters = Boosters(o, ctx),
                Fallback = Fallback(o, ctx),
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
                case "$point" or "$box":
                    var type = key[1..];
                    var image = a!["image"];
                    var units = a["units"];
                    var most = a["max"];
                    var typedMax = most is JsonValue mv && mv.GetValueKind() == System.Text.Json.JsonValueKind.Number && mv.TryGetValue(out System.Text.Json.JsonElement me) && me.TryGetInt32(out var m) && !me.GetRawText().Contains('.') ? m : (int?)null;
                    if ((image is not null && image.GetValueKind() != System.Text.Json.JsonValueKind.String) ||
                        (units is not null && units.GetValueKind() != System.Text.Json.JsonValueKind.String) ||
                        (most is not null && typedMax is null))
                    {
                        // Not writable with the typed builder: passed in its wire form, checked by the same rules.
                        var wire = new JsonObject { ["type"] = type };
                        foreach (var (field, given) in a)
                        {
                            wire[field] = given?.DeepClone();
                        }
                        return Question.FromJson(wire);
                    }
                    return type == "point"
                        ? Question.Point(a["instructions"]?.DeepClone(), (string?)image, (string?)units, typedMax)
                        : Question.Box(a["instructions"]?.DeepClone(), (string?)image, (string?)units, typedMax);
            }
        }
        return Question.FromJson((JsonObject)Plain(q, ctx)!);
    }

    /// <summary>
    /// <c>$booster</c> builders, plain names and plain wire-form objects, in order. A builder
    /// whose <c>images</c> or <c>require</c> the typed <see cref="Booster.Of"/> cannot take is
    /// passed in its wire form, sent as given.
    /// </summary>
    /// <summary>The <c>fallback</c> argument, passed only when the case gives it.</summary>
    private static bool? Fallback(JsonObject args, Dictionary<string, object?> ctx) =>
        Plain(args["fallback"], ctx) is { } f ? (bool)f : null;

    private static List<Booster>? Boosters(JsonObject args, Dictionary<string, object?> ctx) =>
        Plain(args["boosters"], ctx) is JsonArray list ? list.Select(b => BoosterOf(b!)).ToList() : null;

    private static Booster BoosterOf(JsonNode item)
    {
        if (item is not JsonObject { Count: 1 } wrapper || wrapper["$booster"] is not JsonObject spec)
        {
            return Booster.FromJson(item);
        }
        var name = (string)spec["name"]!;
        var images = spec["images"];
        var require = spec["require"];
        var typedImages = images is null || (images is JsonArray ids && ids.All(i => i is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String));
        var typedRequire = require is null || require.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
        // A null options is sent as null, which the typed builder leaves out.
        var typedOptions = !spec.ContainsKey("options") || spec["options"] is not null;
        if (!typedImages || !typedRequire || !typedOptions)
        {
            var wire = new JsonObject { ["name"] = name };
            foreach (var key in new[] { "images", "require", "options" })
            {
                if (spec.ContainsKey(key))
                {
                    wire[key] = spec[key]?.DeepClone();
                }
            }
            return Booster.FromJson(wire);
        }
        return Booster.Of(
            name,
            (images as JsonArray)?.Select(i => (string)i!).ToList(),
            require is null ? null : (bool)require,
            spec["options"]?.DeepClone());
    }

    private static List<Image>? Images(JsonObject args, Dictionary<string, object?> ctx) =>
        Plain(args["images"], ctx) is JsonArray list ? list.Select(i => ImageOf((JsonObject)i!, ctx)).ToList() : null;

    /// <summary>
    /// <c>$video</c> builders and plain wire-form videos. A builder whose <c>fps</c>, <c>start</c>,
    /// <c>end</c> or frame <c>t</c> is not a number cannot be written with the typed factories, so
    /// it is passed in its wire form, which the library checks by the same rules.
    /// </summary>
    private static List<Video>? Videos(JsonObject args, Dictionary<string, object?> ctx) =>
        Plain(args["videos"], ctx) is JsonArray list ? list.Select(v => VideoOf((JsonObject)v!)).ToList() : null;

    private static Video VideoOf(JsonObject item)
    {
        if (item["$video"] is not JsonObject spec)
        {
            return Video.FromJson(item);
        }
        var frames = ((JsonArray)spec["frames"]!).Select(f => FrameOf((JsonObject)f!)).ToList();
        if (!Numeric(spec["fps"], out var fps) || !Numeric(spec["start"], out var start) || !Numeric(spec["end"], out var end))
        {
            var wire = new JsonObject { ["id"] = spec["id"]?.DeepClone() };
            foreach (var name in new[] { "fps", "start", "end" })
            {
                if (spec.ContainsKey(name))
                {
                    wire[name] = spec[name]?.DeepClone();
                }
            }
            wire["frames"] = new JsonArray(frames.Select(f => (JsonNode?)f.ToJson()).ToArray());
            return Video.FromJson(wire);
        }
        return Video.Of((string)spec["id"]!, frames, fps, start, end);
    }

    private static VideoFrame FrameOf(JsonObject item)
    {
        if (item["$frame"] is not JsonObject spec)
        {
            return VideoFrame.FromJson(item);
        }
        var mediaType = (string?)spec["media_type"];
        if (!Numeric(spec["t"], out var t) || t is null)
        {
            var data = spec["data_text"] is { } text
                ? (string)text!
                : Convert.ToBase64String(spec["path"] is { } p ? File.ReadAllBytes(Path.Combine(Suite.Directory, (string)p!)) : Convert.FromBase64String((string)spec["data_base64"]!));
            return VideoFrame.FromJson(new JsonObject { ["t"] = spec["t"]?.DeepClone(), ["media_type"] = mediaType, ["data"] = data });
        }
        if (spec["data_text"] is { } base64)
        {
            return VideoFrame.FromBase64(t.Value, (string)base64!, mediaType);
        }
        var bytes = spec["path"] is { } path
            ? File.ReadAllBytes(Path.Combine(Suite.Directory, (string)path!))
            : Convert.FromBase64String((string)spec["data_base64"]!);
        return VideoFrame.FromBytes(t.Value, bytes, mediaType);
    }

    /// <summary>An absent value, or a JSON number: what a typed factory can take.</summary>
    private static bool Numeric(JsonNode? node, out double? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }
        if (node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            value = (double)v;
            return true;
        }
        return false;
    }

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
        IReadOnlyList<JsonObject> collections => new JsonArray(collections.Select(c => (JsonNode?)c.DeepClone()).ToArray()),
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
            case PointAnswer p:
                return new JsonObject
                {
                    ["kind"] = "point",
                    ["x"] = p.X,
                    ["y"] = p.Y,
                    ["present"] = p.Present,
                    ["confidence"] = p.Confidence,
                    ["image"] = p.Image,
                    ["width"] = p.Width,
                    ["height"] = p.Height,
                };
            case PointsAnswer ps:
                return new JsonObject
                {
                    ["kind"] = "points",
                    ["count"] = ps.Count,
                    ["present"] = ps.Present,
                    ["complete"] = ps.Complete,
                    ["points"] = new JsonArray(ps.Points.Select(p => (JsonNode)new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["confidence"] = p.Confidence }).ToArray()),
                    ["image"] = ps.Image,
                    ["width"] = ps.Width,
                    ["height"] = ps.Height,
                };
            case BoxesAnswer bs:
                return new JsonObject
                {
                    ["kind"] = "boxes",
                    ["count"] = bs.Count,
                    ["present"] = bs.Present,
                    ["complete"] = bs.Complete,
                    ["boxes"] = new JsonArray(bs.Boxes.Select(b => (JsonNode)new JsonObject { ["x1"] = b.X1, ["y1"] = b.Y1, ["x2"] = b.X2, ["y2"] = b.Y2, ["confidence"] = b.Confidence }).ToArray()),
                    ["image"] = bs.Image,
                    ["width"] = bs.Width,
                    ["height"] = bs.Height,
                };
            case BoxAnswer b:
                return new JsonObject
                {
                    ["kind"] = "box",
                    ["x1"] = b.X1,
                    ["y1"] = b.Y1,
                    ["x2"] = b.X2,
                    ["y2"] = b.Y2,
                    ["present"] = b.Present,
                    ["confidence"] = b.Confidence,
                    ["image"] = b.Image,
                    ["width"] = b.Width,
                    ["height"] = b.Height,
                };
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

    /// <summary>A decision's usage: the token counts and what was read of its videos.</summary>
    private static JsonObject DecisionUsage(Usage u) => new()
    {
        ["input_tokens"] = u.InputTokens,
        ["output_tokens"] = u.OutputTokens,
        ["video_frames"] = u.VideoFrames,
        ["video_seconds"] = u.VideoSeconds,
        ["booster_tokens"] = u.BoosterTokens,
        ["boosters"] = u.Boosters,
        ["cost"] = u.Cost is { } c
            ? new JsonObject { ["usd"] = c.Usd, ["model_usd"] = c.ModelUsd, ["boosters_usd"] = c.BoostersUsd, ["free_tier"] = c.FreeTier }
            : null,
    };

    /// <summary>A decision's evidence by booster name, in order; <c>null</c> when it has none.</summary>
    private static JsonObject? Evidence(IReadOnlyDictionary<string, Evidence>? evidence)
    {
        if (evidence is null)
        {
            return null;
        }
        var o = new JsonObject();
        foreach (var (name, e) in evidence)
        {
            o[name] = new JsonObject
            {
                ["version"] = e.Version,
                ["status"] = e.Status,
                ["latency_ms"] = e.LatencyMs,
                ["billed_calls"] = e.BilledCalls,
                ["results"] = new JsonArray(e.Results.Select(r => (JsonNode?)Reading(r)).ToArray()),
            };
        }
        return o;
    }

    public static JsonObject Reading(Reading r) => r switch
    {
        AgeReading a => new JsonObject
        {
            ["kind"] = "age",
            ["image"] = a.Image,
            ["faces"] = a.Faces,
            ["age_years"] = a.AgeYears,
            ["low"] = a.Low,
            ["high"] = a.High,
            ["confidence"] = a.Confidence,
        },
        AiGeneratedReading g => new JsonObject
        {
            ["kind"] = "ai_generated",
            ["image"] = g.Image,
            ["generated"] = g.Generated,
            ["confidence"] = g.Confidence,
            ["method"] = g.Method,
            ["top_generator"] = g.TopGenerator is { } t ? new JsonObject { ["name"] = t.Name, ["probability"] = t.Probability } : null,
            ["c2pa_signer"] = g.C2paSigner,
            ["c2pa"] = g.C2pa,
        },
        OcrReading o => new JsonObject
        {
            ["kind"] = "ocr",
            ["image"] = o.Image,
            ["text"] = o.Text,
            ["truncated"] = o.Truncated,
        },
        FaceDetectionReading f => new JsonObject
        {
            ["kind"] = "face_detection",
            ["image"] = f.Image,
            ["faces"] = f.Faces,
            ["detections"] = new JsonArray(f.Detections.Select(d => (JsonNode)new JsonObject { ["box"] = Box(d.Box), ["confidence"] = d.Confidence }).ToArray()),
        },
        FaceRecognitionReading w => new JsonObject
        {
            ["kind"] = "face_recognition",
            ["image"] = w.Image,
            ["faces"] = w.Faces,
            ["collection_name"] = w.CollectionName,
            ["matches"] = new JsonArray(w.Matches.Select(m => (JsonNode)new JsonObject
            {
                ["box"] = Box(m.Box),
                ["score"] = m.Score,
                ["collection"] = m.Collection,
                ["reference"] = m.Reference,
                ["name"] = m.Name,
                ["label"] = m.Label,
                ["collection_name"] = m.CollectionName,
                ["metadata"] = m.Metadata?.DeepClone(),
            }).ToArray()),
        },
        _ => new JsonObject { ["kind"] = "unknown", ["raw"] = r.Raw?.DeepClone() },
    };

    private static JsonObject Box(FaceBox b) => new() { ["x1"] = b.X1, ["y1"] = b.Y1, ["x2"] = b.X2, ["y2"] = b.Y2 };

    public static JsonObject Decision(Decision d) => new()
    {
        ["model"] = d.Model,
        ["request_id"] = d.RequestId,
        ["latency_ms"] = d.LatencyMs,
        ["usage"] = DecisionUsage(d.Usage),
        ["warnings"] = new JsonArray(d.Warnings.Select(w => (JsonNode?)w).ToArray()),
        ["safety"] = Safety(d.Safety),
        ["answers"] = Answers(d.Answers),
        ["evidence"] = Evidence(d.Evidence),
        ["fallback"] = d.Fallback is { } f ? new JsonObject { ["served_model"] = f.ServedModel, ["requested_model"] = f.RequestedModel } : null,
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
        ["boosters"] = BoosterStatuses(e.Boosters),
        ["details"] = e.Details?.DeepClone(),
    };

    private static JsonObject BoosterStatuses(IReadOnlyDictionary<string, BoosterStatus> boosters)
    {
        var o = new JsonObject();
        foreach (var (name, status) in boosters)
        {
            o[name] = new JsonObject { ["status"] = status.Status, ["latency_ms"] = status.LatencyMs };
        }
        return o;
    }

    /// <summary>
    /// A model entry; the four fine-tuned fields only where <paramref name="expected"/> lists
    /// them, since a case checks each one it lists (suite 1.11).
    /// </summary>
    public static JsonObject Model(ModelInfo m, JsonObject? expected = null)
    {
        var limits = new JsonObject();
        foreach (var (k, v) in m.Limits)
        {
            limits[k] = v?.DeepClone();
        }
        var o = new JsonObject { ["name"] = m.Name, ["description"] = m.Description, ["release_date"] = m.ReleaseDate, ["limits"] = limits };
        var typed = new (string Key, JsonNode? Value)[]
        {
            ("fine_tuned", m.FineTuned),
            ("base", m.Base),
            ("adapter_version", m.AdapterVersion),
            ("status", m.Status),
        };
        foreach (var (key, value) in typed)
        {
            if (expected?.ContainsKey(key) == true)
            {
                o[key] = value;
            }
        }
        return o;
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
