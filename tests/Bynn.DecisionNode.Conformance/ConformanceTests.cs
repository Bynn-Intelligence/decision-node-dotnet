using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Bynn.DecisionNode.Conformance.Compare;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bynn.DecisionNode.Conformance;

/// <summary>
/// Runs every case of the conformance suite against the suite's mock server: one test
/// per case file, named by its id. No case is skipped.
/// </summary>
[Collection("mock")]
public sealed class ConformanceTests
{
    private readonly MockServer _mock;

    public ConformanceTests(MockServer mock)
    {
        _mock = mock;
    }

    public static IEnumerable<object[]> RequestCases() => Suite.Cases("requests");

    public static IEnumerable<object[]> ResponseCases() => Suite.Cases("responses");

    public static IEnumerable<object[]> ErrorCases() => Suite.Cases("errors");

    public static IEnumerable<object[]> BehaviourCases() => Suite.Cases("behaviour");

    public static IEnumerable<object[]> ConstantCases() => Suite.Cases("constants");

    private static string NewSession() => Guid.NewGuid().ToString("N");

    // ------------------------------------------------------------------ requests

    [Theory]
    [MemberData(nameof(RequestCases))]
    public async Task Requests(string id)
    {
        var c = Suite.Load("requests", id);
        var session = NewSession();
        var extra = new Dictionary<string, string> { ["x-mock-session"] = session };
        if (c["mock"] is JsonObject)
        {
            extra["x-mock-scenario"] = "request/" + id;
        }
        using var client = Driver.MakeClient(c["client"] as JsonObject, c["env"] as JsonObject, _mock.Url, extra);
        var result = await Driver.InvokeAsync(client, (JsonObject)c["call"]!);
        await DisposeResult(result);
        if (result is Exception error)
        {
            throw Fail($"the call failed: {error.GetType().Name}: {error.Message}");
        }
        await Task.Delay(20);

        var log = (JsonArray)(await _mock.RequestsAsync(session))["requests"]!;
        var exp = (JsonObject)c["expect"]!;
        if (exp["request"] is JsonObject one)
        {
            if (log.Count != 1)
            {
                throw Fail($"expected 1 request, the mock received {log.Count}: {Describe(log)}");
            }
            CheckRequest(one, (JsonObject)log[0]!, "request");
        }
        else
        {
            CheckRequestList((JsonArray)exp["requests"]!, log);
        }
    }

    private static string Describe(JsonArray log) => string.Join(", ", log.Select(r => $"{r!["method"]} {r["path"]}"));

    private static void CheckRequestList(JsonArray expected, JsonArray log)
    {
        if (log.Count != expected.Count)
        {
            throw Fail($"expected {expected.Count} requests, the mock received {log.Count}: {Describe(log)}");
        }
        var i = 0;
        while (i < expected.Count)
        {
            if ((bool?)expected[i]!["parallel"] != true)
            {
                CheckRequest((JsonObject)expected[i]!, (JsonObject)log[i]!, $"requests[{i}]");
                i++;
                continue;
            }
            var j = i;
            while (j < expected.Count && (bool?)expected[j]!["parallel"] == true)
            {
                j++;
            }
            // Requests sent in parallel may arrive in any order: one assignment must hold.
            // Each expected request is paired with a received one it matches, every received
            // request used once (a bipartite matching, so sixteen parallel PUTs stay cheap).
            var size = j - i;
            var fits = new List<int>[size];
            string? reason = null;
            for (var k = 0; k < size; k++)
            {
                fits[k] = new List<int>();
                for (var g = i; g < j; g++)
                {
                    try
                    {
                        CheckRequest((JsonObject)expected[i + k]!, (JsonObject)log[g]!, $"requests[{i + k}]");
                        fits[k].Add(g);
                    }
                    catch (Xunit.Sdk.XunitException miss)
                    {
                        reason ??= miss.Message;
                    }
                }
            }
            var owner = new Dictionary<int, int>();
            bool Assign(int k, HashSet<int> seen)
            {
                foreach (var g in fits[k])
                {
                    if (seen.Add(g) && (!owner.TryGetValue(g, out var other) || Assign(other, seen)))
                    {
                        owner[g] = k;
                        return true;
                    }
                }
                return false;
            }
            for (var k = 0; k < size; k++)
            {
                if (!Assign(k, new HashSet<int>()))
                {
                    throw Fail("no order of the parallel requests matches: " + (reason ?? $"requests[{i + k}] matches no request"));
                }
            }
            i = j;
        }
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (query ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            q.TryAdd(key, value);
        }
        return q;
    }

    private static List<JsonNode?> ParseJsonLines(string text)
    {
        var stripped = text.Trim();
        if (stripped.Length == 0)
        {
            return new List<JsonNode?>();
        }
        if (stripped.StartsWith('['))
        {
            return ((JsonArray)JsonNode.Parse(stripped)!).ToList();
        }
        return text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)).ToList();
    }

    /// <summary>One recorded request against one expectation, as the suite's README describes.</summary>
    private static void CheckRequest(JsonObject exp, JsonObject got, string where, bool anyBody = false)
    {
        var method = (string)got["method"]!;
        var path = (string)got["path"]!;
        if (method != (string)exp["method"]!)
        {
            throw Fail($"{where} method: expected {exp["method"]}, got {method} {path}");
        }
        if (exp["path"] is { } p && (string)p! != path)
        {
            throw Fail($"{where} path: expected {p}, got {path}");
        }
        if (exp["path_pattern"] is { } pattern && !Regex.IsMatch(path, (string)pattern!))
        {
            throw Fail($"{where} path: {path} does not match {pattern}");
        }
        if (exp["query"] is JsonObject query)
        {
            var have = ParseQuery((string?)got["query"]);
            var want = query.ToDictionary(kv => kv.Key, kv => (string)kv.Value!);
            if (have.Count != want.Count || want.Any(kv => !have.TryGetValue(kv.Key, out var v) || v != kv.Value))
            {
                throw Fail($"{where} query: expected {query.ToJsonString()}, got {string.Join("&", have.Select(kv => kv.Key + "=" + kv.Value))}");
            }
        }
        if (exp["status"] is { } status && (int?)got["status"] != (int)status)
        {
            throw Fail($"{where} status: the mock answered {got["status"]}, expected {status}");
        }
        var headers = (JsonObject)got["headers"]!;
        foreach (var (name, value) in exp["headers"] as JsonObject ?? new JsonObject())
        {
            var actual = (string?)headers[name.ToLowerInvariant()];
            if (actual != (string)value!)
            {
                throw Fail($"{where} header {name}: expected '{value}', got '{actual}'");
            }
        }
        foreach (var (name, pattern2) in exp["header_patterns"] as JsonObject ?? new JsonObject())
        {
            var actual = (string?)headers[name.ToLowerInvariant()];
            if (actual is null || !Regex.IsMatch(actual, (string)pattern2!))
            {
                throw Fail($"{where} header {name}: '{actual}' does not match {pattern2}");
            }
        }
        foreach (var name in exp["absent_headers"] as JsonArray ?? new JsonArray())
        {
            if (headers.ContainsKey(((string)name!).ToLowerInvariant()))
            {
                throw Fail($"{where} header {name} must not be sent");
            }
        }
        if (exp["body_base64"] is { } b64)
        {
            if ((string?)got["body_base64"] != (string)b64!)
            {
                throw Fail($"{where} body: the bytes differ from the expected bytes");
            }
            return;
        }
        if (exp["body_sha256"] is { } sha)
        {
            var bytes = Convert.FromBase64String((string?)got["body_base64"] ?? string.Empty);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (digest != (string)sha!)
            {
                throw Fail($"{where} body: sha256 {digest}, expected {sha}");
            }
            return;
        }
        var bodyText = (string?)got["body_text"] ?? string.Empty;
        if (exp["jsonl"] is JsonArray jsonl)
        {
            var lines = new JsonArray(ParseJsonLines(bodyText).Select(l => l?.DeepClone()).ToArray());
            if (!Same(Canon(jsonl), Canon(lines)))
            {
                throw Fail($"{where} body lines: expected {Short(jsonl)}, got {Short(lines)}");
            }
            return;
        }
        if (exp.ContainsKey("jsonl_count") || exp.ContainsKey("jsonl_checks"))
        {
            var lines = ParseJsonLines(bodyText);
            if (exp["jsonl_count"] is { } count && lines.Count != (int)count)
            {
                throw Fail($"{where} body: expected {count} lines, got {lines.Count}");
            }
            foreach (var check in (exp["jsonl_checks"] as JsonArray ?? new JsonArray()).Cast<JsonObject>())
            {
                var line = (int)check["line"]!;
                if (line >= lines.Count)
                {
                    throw Fail($"{where} body: no line {line}");
                }
                CheckValue(check, lines[line], null, $"{where} line {line}");
            }
            return;
        }
        var needsBody = exp.ContainsKey("body") || exp.ContainsKey("body_checks") || exp.ContainsKey("ordered_keys");
        var body = needsBody && bodyText.Length > 0 ? JsonNode.Parse(bodyText, documentOptions: Deep) : null;
        if (exp.ContainsKey("body"))
        {
            if (!Same(Canon(exp["body"]), body))
            {
                throw Fail($"{where} body: expected {Short(exp["body"])}, got {Short(body)}");
            }
        }
        else if (!exp.ContainsKey("body_checks") && bodyText.Length > 0 && !anyBody)
        {
            throw Fail($"{where} expected no body, got {(bodyText.Length < 200 ? bodyText : bodyText[..197] + "...")}");
        }
        foreach (var check in (exp["body_checks"] as JsonArray ?? new JsonArray()).Cast<JsonObject>())
        {
            CheckValue(check, body, bodyText, where);
        }
        foreach (var ptrNode in exp["ordered_keys"] as JsonArray ?? new JsonArray())
        {
            var ptr = (string)ptrNode!;
            var want = KeyOrder(exp["body"]!.ToJsonString(), ptr);
            var have = KeyOrder(bodyText, ptr);
            if (!want.SequenceEqual(have))
            {
                throw Fail($"{where} body{ptr}: key order expected [{string.Join(", ", want)}], got [{string.Join(", ", have)}]");
            }
        }
    }

    private static void CheckValue(JsonObject check, JsonNode? body, string? bodyText, string where)
    {
        var ptr = (string)check["pointer"]!;
        var (found, value) = Pointer(body, ptr);
        if ((bool?)check["absent"] == true)
        {
            if (found)
            {
                throw Fail($"{where} body{ptr}: must be absent, got {Short(value)}");
            }
            return;
        }
        if (!found)
        {
            throw Fail($"{where} body{ptr}: missing");
        }
        if (check.ContainsKey("equals") && !Same(Canon(check["equals"]), Canon(value)))
        {
            throw Fail($"{where} body{ptr}: expected {Short(check["equals"])}, got {Short(value)}");
        }
        if (check["type"] is { } type && !IsType(value, (string)type!))
        {
            throw Fail($"{where} body{ptr}: expected a {type}");
        }
        if (check["pattern"] is { } pattern
            && !(value is JsonValue v && v.GetValueKind() == JsonValueKind.String && Regex.IsMatch((string)v!, (string)pattern!)))
        {
            throw Fail($"{where} body{ptr}: {Short(value)} does not match {pattern}");
        }
        if (check["keys"] is JsonArray keys)
        {
            var want = keys.Select(k => (string)k!).ToList();
            var have = bodyText is not null ? KeyOrder(bodyText, ptr) : (value as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
            if (!want.SequenceEqual(have))
            {
                throw Fail($"{where} body{ptr}: keys expected [{string.Join(", ", want)}], got [{string.Join(", ", have)}]");
            }
        }
    }

    // ------------------------------------------------------------------ responses

    [Theory]
    [MemberData(nameof(ResponseCases))]
    public async Task Responses(string id)
    {
        var c = Suite.Load("responses", id);
        var spec = new JsonObject { ["api_key"] = "dn_test_conformance" };
        using var client = Driver.MakeClient(spec, null, _mock.Url, new Dictionary<string, string>
        {
            ["x-mock-session"] = NewSession(),
            ["x-mock-scenario"] = "response/" + id,
        });
        var result = await Driver.InvokeAsync(client, (JsonObject)c["call"]!);
        await DisposeResult(result);
        if (result is Exception error)
        {
            throw Fail($"the call failed: {error.GetType().Name}: {error.Message}");
        }
        var exp = (JsonObject)c["expect"]!;
        if (exp["decision"] is { } decision)
        {
            Match(Canon(decision), Canon(Driver.Decision((Decision)result!)), "decision");
        }
        else if (exp["answer"] is { } answer)
        {
            Match(Canon(answer), Canon(Driver.Answer((Answer)result!)), "answer");
        }
        else if (exp["models"] is { } models)
        {
            var list = (IReadOnlyList<ModelInfo>)result!;
            var got = new JsonArray(list.Select((m, i) => (JsonNode?)Driver.Model(m, (models as JsonArray)?.ElementAtOrDefault(i) as JsonObject)).ToArray());
            if (!Same(Canon(models), Canon(got)))
            {
                throw Fail($"models: expected {Short(models)}, got {Short(got)}");
            }
        }
        else if (exp.ContainsKey("raw"))
        {
            var got = (JsonNode?)result;
            if (!Same(Canon(exp["raw"]), Canon(got)))
            {
                throw Fail($"raw: expected {Short(exp["raw"])}, got {Short(got)}");
            }
        }
        else if (exp["batch"] is { } batch)
        {
            Match(Canon(batch), Canon(Driver.Batch((Batch)result!)), "batch");
        }
        else if (exp["batch_page"] is { } page)
        {
            Match(Canon(page), Canon(Driver.Page((BatchPage)result!)), "batch_page");
        }
        else if (CollectionKinds.FirstOrDefault(exp.ContainsKey) is { } collectionKind)
        {
            CheckCollectionResult(collectionKind, exp[collectionKind], result, collectionKind);
        }
        else if (exp["batch_results"] is JsonArray results)
        {
            CheckBatchResults(results, (List<BatchResult>)result!, "batch_results");
        }
        else if (exp["session"] is { } sessionExp)
        {
            Match(Canon(sessionExp), Canon(Driver.Session((Session)result!)), "session");
        }
        else if (exp["upload_ids"] is { } ids)
        {
            var got = Driver.Normalise(result);
            if (!Same(Canon(ids), Canon(got)))
            {
                throw Fail($"upload_ids: expected {Short(ids)}, got {Short(got)}");
            }
        }
        else
        {
            throw Fail("the case has no expectation this runner knows");
        }
    }

    /// <summary><c>batch_results</c> whole, or <c>count</c> with <c>batch_results_sample</c> by index.</summary>
    private static void CheckResultsSpec(JsonObject spec, List<BatchResult> results, string path)
    {
        if (spec["batch_results"] is JsonArray whole)
        {
            CheckBatchResults(whole, results, path + ".batch_results");
        }
        if (spec["count"] is { } count && results.Count != (int)count)
        {
            throw Fail($"{path}: expected {count} results, got {results.Count}");
        }
        foreach (var sample in (spec["batch_results_sample"] as JsonArray ?? new JsonArray()).Cast<JsonObject>())
        {
            var i = (int)sample["index"]!;
            if (i >= results.Count)
            {
                throw Fail($"{path}: no result {i} of {results.Count}");
            }
            var one = (JsonObject)sample.DeepClone();
            one.Remove("index");
            CheckBatchResult(one, results[i], $"{path}.batch_results[{i}]");
        }
    }

    /// <summary>The collections calls' results (suite 1.13), each the API's object as sent; <c>collections</c> is the list.</summary>
    private static readonly string[] CollectionKinds = { "collection", "collections", "item", "item_page", "deleted" };

    private static void CheckCollectionResult(string kind, JsonNode? expected, object? result, string path)
    {
        if (kind == "collections")
        {
            if (result is not IReadOnlyList<JsonObject> list)
            {
                throw Fail($"{path}: expected a list of collections, got {Describe(result)}");
            }
            var wanted = (JsonArray)expected!;
            if (wanted.Count != list.Count)
            {
                throw Fail($"{path}: expected {wanted.Count} collections, got {list.Count}");
            }
            for (var i = 0; i < list.Count; i++)
            {
                Match(Canon(wanted[i]), Canon(list[i]), $"{path}[{i}]");
            }
            return;
        }
        if (result is not JsonObject obj)
        {
            throw Fail($"{path}: expected the API's object, got {Describe(result)}");
        }
        Match(Canon(expected), Canon(obj), path);
    }

    private static void CheckBatchResults(JsonArray expected, List<BatchResult> results, string path)
    {
        if (results.Count != expected.Count)
        {
            throw Fail($"{path}: expected {expected.Count} results, got {results.Count}");
        }
        for (var i = 0; i < results.Count; i++)
        {
            CheckBatchResult((JsonObject)expected[i]!, results[i], $"{path}[{i}]");
        }
    }

    private static void CheckBatchResult(JsonObject spec, BatchResult r, string where)
    {
        {
            if (spec.ContainsKey("custom_id") && !Same(Canon(spec["custom_id"]), JsonValue.Create(r.CustomId)))
            {
                throw Fail($"{where}.custom_id: expected {Short(spec["custom_id"])}, got {r.CustomId}");
            }
            if (spec.ContainsKey("status"))
            {
                JsonNode? status = r.Status is { } code ? JsonValue.Create(code) : JsonValue.Create(r.Outcome);
                if (!Same(Canon(spec["status"]), Canon(status)))
                {
                    throw Fail($"{where}.status: expected {Short(spec["status"])}, got {Short(status)}");
                }
            }
            if (spec["headers"] is JsonObject headers)
            {
                var got = new JsonObject();
                foreach (var (k, v) in r.Headers)
                {
                    got[k] = v;
                }
                Match(Canon(headers), got, where + ".headers");
            }
            if (spec.ContainsKey("decision"))
            {
                if (r.Decision is null)
                {
                    throw Fail($"{where}: expected a decision, got none");
                }
                Match(Canon(spec["decision"]), Canon(Driver.Decision(r.Decision)), where + ".decision");
            }
            if (spec["error"] is JsonObject error)
            {
                if (r.Error is null)
                {
                    throw Fail($"{where}: expected an error value, got none");
                }
                CheckError(error, r.Error, where + ".error");
            }
            if ((bool?)spec["no_decision"] == true && r.Decision is not null)
            {
                throw Fail($"{where}: expected no decision");
            }
        }
    }

    // ------------------------------------------------------------------ errors

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Errors(string id)
    {
        var c = Suite.Load("errors", id);
        var session = NewSession();
        using var client = Driver.MakeClient(c["client"] as JsonObject, c["env"] as JsonObject, _mock.Url, new Dictionary<string, string>
        {
            ["x-mock-session"] = session,
            ["x-mock-scenario"] = "error/" + id,
        });
        var expected = (JsonObject)c["expect"]!["error"]!;
        object? result;
        if ((string?)expected["catch_as"] is { } catchAs)
        {
            // The error must be caught by a clause naming the exported base, DecisionNodeError.
            Assert.Equal(nameof(DecisionNodeError), catchAs);
            result = await Driver.InvokeCatchingBaseAsync(client, (JsonObject)c["call"]!);
        }
        else
        {
            result = await Driver.InvokeAsync(client, (JsonObject)c["call"]!);
        }
        await DisposeResult(result);
        if (result is not DecisionNodeError error)
        {
            throw Fail("no error was raised" + (result is Exception other ? $" ({other.GetType().Name}: {other.Message})" : string.Empty));
        }
        CheckError(expected, error, "error");
        var log = (JsonArray)(await _mock.RequestsAsync(session))["requests"]!;
        if (log.Count != 1)
        {
            throw Fail($"expected 1 attempt with max_retries 0, got {log.Count}: {Describe(log)}");
        }
    }

    private static void CheckError(JsonObject spec, DecisionNodeError error, string path)
    {
        var cls = (string?)spec["error"] ?? (string)spec["class"]!;
        var names = new List<string>();
        for (var t = error.GetType(); t is not null; t = t.BaseType)
        {
            names.Add(t.Name);
        }
        if ((bool?)spec["subclass_ok"] == true)
        {
            if (!names.Contains(cls))
            {
                throw Fail($"{path}: expected {cls} or a subclass, got {error.GetType().Name} ({error.Message})");
            }
        }
        else if (error.GetType().Name != cls)
        {
            throw Fail($"{path}: expected {cls}, got {error.GetType().Name} ({error.Message})");
        }
        Match(Canon(spec["fields"] ?? new JsonObject()), Canon(Driver.Error(error)), path + ".fields");
    }

    // ------------------------------------------------------------------ behaviour

    [Theory]
    [MemberData(nameof(BehaviourCases))]
    public async Task Behaviour(string id)
    {
        var c = Suite.Load("behaviour", id);
        var session = NewSession();
        var extra = new Dictionary<string, string> { ["x-mock-session"] = session };
        if (c["mock"] is JsonObject)
        {
            extra["x-mock-scenario"] = "behaviour/" + id;
        }
        var exp = (JsonObject)c["expect"]!;
        DecisionNodeClient? made = null;
        ArgumentException? constructionError = null;
        try
        {
            made = Driver.MakeClient(c["client"] as JsonObject, c["env"] as JsonObject, _mock.Url, extra);
        }
        catch (ArgumentException error)
        {
            // A bad client argument (a plain-http base URL, a timeout of 0) fails here,
            // before any request; the case's outcome checks it.
            constructionError = error;
        }
        using var client = made;
        var scheduled = new List<double>();
        if (client is not null && exp.ContainsKey("scheduled_waits_ms"))
        {
            // Record each wait (between attempts, PUT retries, polls, reopens) and return at
            // once, so a long wait is checked without spending it.
            client.Delay = (wait, _) =>
            {
                lock (scheduled)
                {
                    scheduled.Add(wait.TotalMilliseconds);
                }
                return Task.CompletedTask;
            };
        }
        var events = new List<JsonObject>();
        var ctx = new Dictionary<string, object?>();
        var clock = Stopwatch.StartNew();
        object? result;
        if (client is null)
        {
            result = constructionError;
        }
        else if (c["steps"] is JsonArray steps)
        {
            try
            {
                await RunSteps(steps, client, session, events, ctx);
            }
            finally
            {
                foreach (var s in ctx.Values.OfType<Session>())
                {
                    await s.DisposeAsync();
                }
            }
            result = null;
        }
        else
        {
            result = await Driver.InvokeAsync(client, (JsonObject)c["call"]!);
            await DisposeResult(result);
        }
        var elapsed = clock.Elapsed.TotalMilliseconds;
        await Task.Delay(50); // let the mock finish recording dropped connections

        var info = await _mock.RequestsAsync(session);
        var log = (JsonArray)info["requests"]!;
        if (exp["one_of"] is JsonArray alternatives)
        {
            // The rule allows more than one way to comply: one alternative must hold whole.
            var misses = new List<string>();
            foreach (var alt in alternatives.Cast<JsonObject>())
            {
                try
                {
                    CheckAttempts(alt, log);
                    CheckOutcome((JsonObject)alt["outcome"]!, result, "outcome");
                    misses.Clear();
                    break;
                }
                catch (Xunit.Sdk.XunitException miss)
                {
                    misses.Add(miss.Message);
                }
            }
            if (misses.Count > 0)
            {
                throw Fail("no alternative of one_of holds: " + string.Join("; ", misses));
            }
        }
        else if (exp.ContainsKey("attempts"))
        {
            CheckAttempts(exp, log);
        }
        if (exp["request_sequence"] is JsonArray sequence)
        {
            CheckSequence(sequence, log);
        }
        if (exp["request_counts"] is JsonArray counts)
        {
            CheckCounts(counts, log);
        }
        if (exp["session_events"] is JsonArray sessionEvents)
        {
            CheckEvents(sessionEvents, events);
        }
        if (exp["pongs"] is JsonObject pongs)
        {
            InRange(pongs, info["pongs"]!.GetValue<int>(), "pongs");
        }
        if (exp["one_of"] is null && exp["outcome"] is JsonObject outcome)
        {
            CheckOutcome(outcome, result, "outcome");
        }
        if (exp["results"] is JsonArray results)
        {
            if (result is not IReadOnlyList<DecisionResult> list || list.Count != results.Count)
            {
                throw Fail($"results: expected {results.Count} results, got {result}");
            }
            for (var i = 0; i < list.Count; i++)
            {
                CheckOutcome((JsonObject)results[i]!, (object?)list[i].Decision ?? list[i].Error, $"results[{i}]");
            }
        }
        if (exp["gaps_ms"] is JsonArray gaps)
        {
            var times = log.Select(r => (double)r!["time"]!).ToList();
            for (var i = 0; i < gaps.Count; i++)
            {
                InRange((JsonObject)gaps[i]!, (times[i + 1] - times[i]) * 1000, $"gap {i + 1} (ms)");
            }
        }
        if (exp["elapsed_ms"] is JsonObject elapsedRange)
        {
            InRange(elapsedRange, elapsed, "elapsed (ms)");
        }
        if (exp["max_in_flight"] is JsonObject flight)
        {
            InRange(flight, (double)info["max_in_flight"]!, "max in flight");
        }
        if (exp["scheduled_waits_ms"] is JsonArray waits)
        {
            if (scheduled.Count != waits.Count)
            {
                throw Fail($"scheduled waits: expected {waits.Count}, the client scheduled {scheduled.Count} ({string.Join(", ", scheduled.Select(w => w.ToString("0")))})");
            }
            for (var i = 0; i < waits.Count; i++)
            {
                InRange((JsonObject)waits[i]!, scheduled[i], $"scheduled wait {i + 1} (ms)");
            }
        }
        foreach (var pathNode in exp["unrequested_paths"] as JsonArray ?? new JsonArray())
        {
            var path = (string)pathNode!;
            var hits = (JsonArray)(await _mock.RequestsAsync("*", path))["requests"]!;
            if (hits.Count > 0)
            {
                throw Fail($"{path} was requested {hits.Count} time(s); it must not be requested at all");
            }
        }
    }

    private async Task RunSteps(JsonArray steps, DecisionNodeClient client, string session, List<JsonObject> events, Dictionary<string, object?> ctx)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = (JsonObject)steps[i]!;
            var where = $"steps[{i}]";
            if (step["call"] is JsonObject call)
            {
                var result = await Driver.InvokeAsync(client, call, ctx);
                if (step["outcome"] is JsonObject outcome)
                {
                    CheckOutcome(outcome, result, where + ".outcome");
                }
                else if (result is Exception error)
                {
                    throw Fail($"{where}: the call raised {error.GetType().Name}: {error.Message}");
                }
                if ((string?)step["save_as"] is { } name)
                {
                    ctx[name] = result;
                }
            }
            else if ((string?)step["connect"] is { } connect)
            {
                await ((Session)ctx[connect]!).ConnectAsync();
            }
            else if (step["send"] is JsonObject send)
            {
                var s = (Session)ctx[(string)send["session"]!]!;
                object? sent = null;
                var checkOnly = (bool?)step["check"] == true;
                try
                {
                    if (checkOnly)
                    {
                        // The frame handed to the library's check instead of being sent.
                        s.CheckFrame(Driver.Frame(send, ctx));
                        sent = new Checked();
                    }
                    else
                    {
                        await s.SendAsync(Driver.Frame(send, ctx));
                    }
                }
                catch (Exception error) when (error is DecisionNodeError or ArgumentException)
                {
                    sent = error;
                }
                if (step["outcome"] is JsonObject sendOutcome)
                {
                    // The frame must be refused locally: send raises and writes nothing.
                    CheckOutcome(sendOutcome, sent, where + ".outcome");
                }
                else if (sent is Exception error && !checkOnly)
                {
                    throw Fail($"{where}: send raised {error.GetType().Name}: {error.Message}");
                }
            }
            else if (step["read"] is JsonObject read)
            {
                var s = (Session)ctx[(string)read["session"]!]!;
                var count = (int?)read["count"];
                var timeout = TimeSpan.FromMilliseconds((int?)read["timeout_ms"] ?? 10000);
                while (true)
                {
                    SessionEvent ev;
                    try
                    {
                        using var cts = new CancellationTokenSource(timeout);
                        ev = await s.ReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw Fail($"{where}: no session event arrived in time ({events.Count} read so far)");
                    }
                    events.Add(Driver.Event(ev));
                    if (ev is SessionClosed)
                    {
                        break;
                    }
                    if (count is not null && --count == 0)
                    {
                        break;
                    }
                }
            }
            else if ((string?)step["end"] is { } end)
            {
                try
                {
                    await ((Session)ctx[end]!).EndAsync();
                    events.Add(new JsonObject { ["ended"] = true });
                }
                catch (DecisionNodeError error)
                {
                    events.Add(new JsonObject { ["end_error"] = new JsonObject { ["class"] = error.GetType().Name, ["status"] = error.Status } });
                }
            }
            else if ((string?)step["close"] is { } close)
            {
                await ((Session)ctx[close]!).CloseAsync();
            }
            else if (step["mock_clock"] is JsonObject mockClock)
            {
                await _mock.ClockAsync(session, mockClock);
            }
            else if (step["sleep_ms"] is { } sleep)
            {
                await Task.Delay((int)sleep);
            }
            else
            {
                throw Fail($"{where}: unknown step {string.Join(", ", step.Select(kv => kv.Key))}");
            }
        }
    }

    private static void CheckAttempts(JsonObject exp, JsonArray log)
    {
        var attempts = (int)exp["attempts"]!;
        if (log.Count != attempts)
        {
            throw Fail($"attempts: expected {attempts}, the mock received {log.Count}: {Describe(log)}");
        }
    }

    private static void CheckSequence(JsonArray expected, JsonArray log)
    {
        if (expected.Count != log.Count)
        {
            throw Fail($"request sequence: expected {expected.Count} requests, the mock received {log.Count}: {Describe(log)}");
        }
        for (var i = 0; i < expected.Count; i++)
        {
            var spec = (JsonObject)expected[i]!;
            var rec = (JsonObject)log[i]!;
            var checks = new JsonObject { ["method"] = spec["method"]!.DeepClone() };
            foreach (var key in new[] { "path", "path_pattern", "status", "headers", "header_patterns", "absent_headers", "body_checks", "jsonl_count", "jsonl_checks", "body_base64", "body_sha256", "query" })
            {
                if (spec[key] is { } v)
                {
                    checks[key] = v.DeepClone();
                }
            }
            CheckRequest(checks, rec, $"request sequence[{i}]", anyBody: true);
        }
    }

    private static void CheckCounts(JsonArray expected, JsonArray log)
    {
        foreach (var spec in expected.Cast<JsonObject>())
        {
            var n = log.Count(r =>
                (string?)r!["method"] == (string)spec["method"]!
                && (spec["path"] is not { } p || (string?)r["path"] == (string)p!)
                && (spec["path_pattern"] is not { } pp || Regex.IsMatch((string)r["path"]!, (string)pp!)));
            var what = $"{spec["method"]} {spec["path"] ?? spec["path_pattern"]}";
            if (spec["count"] is { } count && n != (int)count)
            {
                throw Fail($"{what}: expected {count} requests, got {n}");
            }
            InRange(spec, n, what);
        }
    }

    private static void CheckEvents(JsonArray expected, List<JsonObject> events)
    {
        if (expected.Count != events.Count)
        {
            throw Fail($"session events: expected {expected.Count}, got {events.Count}: {string.Join(", ", events.Select(e => Short(e)))}");
        }
        for (var i = 0; i < expected.Count; i++)
        {
            var spec = (JsonObject)expected[i]!;
            var ev = events[i];
            var key = spec.First().Key;
            if (ev.First().Key != key)
            {
                throw Fail($"session events[{i}]: expected {key}, got {Short(ev)}");
            }
            if (spec[key] is JsonObject fields)
            {
                Match(Canon(fields), Canon(ev[key]), $"session events[{i}].{key}");
            }
            else if (!Same(Canon(spec[key]), Canon(ev[key])))
            {
                throw Fail($"session events[{i}]: expected {Short(spec)}, got {Short(ev)}");
            }
        }
    }

    // ------------------------------------------------------------------ constants

    [Theory]
    [MemberData(nameof(ConstantCases))]
    public void Constants(string id)
    {
        var c = Suite.Load("constants", id);
        var name = (string?)(c["names"] as JsonObject)?["dotnet"] ?? (string)c["constant"]!;
        var field = typeof(DecisionNodeClient).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (field is null || !field.IsLiteral)
        {
            throw Fail($"the library has no public constant {name}");
        }
        var value = JsonValue.Create(field.GetRawConstantValue());
        if (!Same(Canon(c["equals"]), Canon(value)))
        {
            throw Fail($"{name}: expected {Short(c["equals"])}, got {Short(value)}");
        }
    }

    // ------------------------------------------------------------------ outcomes

    private static void CheckOutcome(JsonObject spec, object? result, string path)
    {
        if ((bool?)spec["argument_error"] == true)
        {
            if (result is not ArgumentException)
            {
                throw Fail($"{path}: expected an argument error, got {Describe(result)}");
            }
            return;
        }
        if (result is ArgumentException unexpected)
        {
            throw Fail($"{path}: unexpected argument error: {unexpected.Message}");
        }
        if (spec["result"] is { } kindNode)
        {
            if (result is Exception error)
            {
                throw Fail($"{path}: expected {kindNode}, got {error.GetType().Name}: {error.Message}");
            }
            switch ((string)kindNode!)
            {
                case "decision":
                    if (result is not Decision d)
                    {
                        throw Fail($"{path}: expected a decision, got {Describe(result)}");
                    }
                    if (spec["decision"] is { } decision)
                    {
                        Match(Canon(decision), Canon(Driver.Decision(d)), path + ".decision");
                    }
                    break;
                case "answer":
                    Match(Canon(spec["answer"] ?? new JsonObject()), Canon(Driver.Answer((Answer)result!)), path + ".answer");
                    break;
                case "batch":
                    Match(Canon(spec["batch"] ?? new JsonObject()), Canon(Driver.Batch((Batch)result!)), path + ".batch");
                    break;
                case "batch_page":
                    Match(Canon(spec["batch_page"] ?? new JsonObject()), Canon(Driver.Page((BatchPage)result!)), path + ".batch_page");
                    break;
                case "batch_results":
                    CheckResultsSpec(spec, (List<BatchResult>)result!, path);
                    break;
                case var kind when CollectionKinds.Contains(kind):
                    CheckCollectionResult(kind, spec[kind] ?? new JsonObject(), result, path + "." + kind);
                    break;
                case "batch_run":
                    var run = (BatchRunResult)result!;
                    Match(Canon(spec["batch"] ?? new JsonObject()), Canon(Driver.Batch(run.Batch)), path + ".batch");
                    CheckResultsSpec(spec, run.Results, path);
                    break;
                case "checked":
                    if (result is not Checked)
                    {
                        throw Fail($"{path}: expected a clean check, got {Describe(result)}");
                    }
                    break;
                case "session":
                    Match(Canon(spec["session"] ?? new JsonObject()), Canon(Driver.Session((Session)result!)), path + ".session");
                    break;
                case "upload_ids":
                    if (result is not IReadOnlyList<string> ids || (spec["count"] is { } count && ids.Count != (int)count))
                    {
                        throw Fail($"{path}: expected {spec["count"]} upload ids, got {Describe(result)}");
                    }
                    foreach (var uid in ids)
                    {
                        if (!Regex.IsMatch(uid, "^upl_[0-9a-f]{32}$"))
                        {
                            throw Fail($"{path}: '{uid}' is not an upload id");
                        }
                    }
                    if (spec["distinct"] is { } distinct && (ids.Distinct().Count() == ids.Count) != (bool)distinct)
                    {
                        throw Fail($"{path}: upload ids {string.Join(", ", ids)}");
                    }
                    break;
                case "supervised":
                    if (result is not Supervised supervised)
                    {
                        throw Fail($"{path}: expected supervise to return, got {Describe(result)}");
                    }
                    InRange(spec["replies"] as JsonObject ?? new JsonObject(), supervised.Replies.OfType<Reply>().Count(), "replies");
                    if (spec.ContainsKey("closed"))
                    {
                        JsonNode? got = supervised.Closed is null ? null : new JsonObject { ["code"] = supervised.Closed.Code, ["reopen"] = supervised.Closed.Reopen };
                        if (!Same(Canon(spec["closed"]), Canon(got)))
                        {
                            throw Fail($"{path}.closed: expected {Short(spec["closed"])}, got {Short(got)}");
                        }
                    }
                    break;
                default:
                    throw Fail($"{path}: unknown result kind {kindNode}");
            }
            return;
        }
        if (result is not DecisionNodeError dnError)
        {
            throw Fail($"{path}: expected {spec["error"]}, got {Describe(result)}");
        }
        CheckError(spec, dnError, path);
    }

    private static string Describe(object? result) => result switch
    {
        null => "null",
        Exception e => $"{e.GetType().Name}: {e.Message}",
        _ => result.GetType().Name,
    };

    private static async Task DisposeResult(object? result)
    {
        if (result is Session s)
        {
            await s.DisposeAsync();
        }
    }

    private static void InRange(JsonObject range, double value, string what)
    {
        if (range["min"] is { } min && value < (double)min)
        {
            throw Fail($"{what} {value:0} is below {min}");
        }
        if (range["max"] is { } max && value > (double)max)
        {
            throw Fail($"{what} {value:0} is above {max}");
        }
    }
}
