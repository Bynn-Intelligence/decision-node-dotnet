using System.Diagnostics;
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
        using var client = Driver.MakeClient(c["client"] as JsonObject, c["env"] as JsonObject, _mock.Url, new Dictionary<string, string> { ["x-mock-session"] = session });
        var result = await Driver.InvokeAsync(client, (JsonObject)c["call"]!);
        if (result is DecisionNodeError error)
        {
            throw Fail($"the call failed: {error.GetType().Name}: {error.Message}");
        }

        var log = (JsonArray)(await _mock.RequestsAsync(session))["requests"]!;
        if (log.Count != 1)
        {
            throw Fail($"expected 1 request, the mock received {log.Count}");
        }
        var got = (JsonObject)log[0]!;
        var exp = (JsonObject)c["expect"]!["request"]!;
        Assert.Equal((string)exp["method"]!, (string)got["method"]!);
        Assert.Equal((string)exp["path"]!, (string)got["path"]!);
        var headers = (JsonObject)got["headers"]!;
        foreach (var (name, value) in exp["headers"] as JsonObject ?? new JsonObject())
        {
            var actual = (string?)headers[name.ToLowerInvariant()];
            if (actual != (string)value!)
            {
                throw Fail($"header {name}: expected '{value}', got '{actual}'");
            }
        }
        foreach (var (name, pattern) in exp["header_patterns"] as JsonObject ?? new JsonObject())
        {
            var actual = (string?)headers[name.ToLowerInvariant()];
            if (actual is null || !Regex.IsMatch(actual, (string)pattern!))
            {
                throw Fail($"header {name}: '{actual}' does not match {pattern}");
            }
        }
        foreach (var name in exp["absent_headers"] as JsonArray ?? new JsonArray())
        {
            if (headers.ContainsKey(((string)name!).ToLowerInvariant()))
            {
                throw Fail($"header {name} must not be sent");
            }
        }

        var bodyText = (string?)got["body_text"] ?? string.Empty;
        var body = bodyText.Length > 0 ? JsonNode.Parse(bodyText) : null;
        if (exp.ContainsKey("body"))
        {
            if (!Same(Canon(exp["body"]), body))
            {
                throw Fail($"body: expected {Short(exp["body"])}, got {Short(body)}");
            }
        }
        else if (!exp.ContainsKey("body_checks") && bodyText.Length > 0)
        {
            throw Fail($"expected no body, got {bodyText}");
        }
        foreach (var checkNode in exp["body_checks"] as JsonArray ?? new JsonArray())
        {
            var check = (JsonObject)checkNode!;
            var ptr = (string)check["pointer"]!;
            var (found, value) = Pointer(body, ptr);
            if (!found)
            {
                throw Fail($"body{ptr}: missing");
            }
            if (check.ContainsKey("equals") && !Same(Canon(check["equals"]), value))
            {
                throw Fail($"body{ptr}: expected {Short(check["equals"])}, got {Short(value)}");
            }
            if (check["type"] is { } type && !IsType(value, (string)type!))
            {
                throw Fail($"body{ptr}: expected a {type}");
            }
            if (check["keys"] is JsonArray keys)
            {
                var want = keys.Select(k => (string)k!).ToList();
                var have = KeyOrder(bodyText, ptr);
                if (!want.SequenceEqual(have))
                {
                    throw Fail($"body{ptr}: keys expected [{string.Join(", ", want)}], got [{string.Join(", ", have)}]");
                }
            }
        }
        foreach (var ptrNode in exp["ordered_keys"] as JsonArray ?? new JsonArray())
        {
            var ptr = (string)ptrNode!;
            var want = KeyOrder(exp["body"]!.ToJsonString(), ptr);
            var have = KeyOrder(bodyText, ptr);
            if (!want.SequenceEqual(have))
            {
                throw Fail($"body{ptr}: key order expected [{string.Join(", ", want)}], got [{string.Join(", ", have)}]");
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
        if (result is DecisionNodeError error)
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
            var got = new JsonArray(((IReadOnlyList<ModelInfo>)result!).Select(m => (JsonNode?)Driver.Model(m)).ToArray());
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
        else
        {
            throw Fail("the case has no expectation this runner knows");
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
        if (result is not DecisionNodeError error)
        {
            throw Fail("no error was raised");
        }
        CheckError(expected, error, "error");
        var log = (JsonArray)(await _mock.RequestsAsync(session))["requests"]!;
        if (log.Count != 1)
        {
            throw Fail($"expected 1 attempt with max_retries 0, got {log.Count}");
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
                throw Fail($"{path}: expected {cls} or a subclass, got {error.GetType().Name}");
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
            // Record each wait between attempts and return at once, so a capped 60 s wait
            // is checked without spending it.
            client.Delay = (wait, _) =>
            {
                lock (scheduled)
                {
                    scheduled.Add(wait.TotalMilliseconds);
                }
                return Task.CompletedTask;
            };
        }
        var clock = Stopwatch.StartNew();
        var result = client is null ? constructionError : await Driver.InvokeAsync(client, (JsonObject)c["call"]!);
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
                    CheckAttemptsAndOutcome(alt, log.Count, result);
                    misses.Clear();
                    break;
                }
                catch (Exception miss) when (miss is Xunit.Sdk.XunitException)
                {
                    misses.Add(miss.Message);
                }
            }
            if (misses.Count > 0)
            {
                throw Fail("no alternative of one_of holds: " + string.Join("; ", misses));
            }
        }
        else
        {
            CheckAttemptsAndOutcome(exp, log.Count, result);
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
                throw Fail($"scheduled waits: expected {waits.Count}, the client scheduled {scheduled.Count}");
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

    private static void CheckAttemptsAndOutcome(JsonObject exp, int received, object? result)
    {
        var attempts = (int)exp["attempts"]!;
        if (received != attempts)
        {
            throw Fail($"attempts: expected {attempts}, the mock received {received}");
        }
        if (exp["outcome"] is JsonObject outcome)
        {
            CheckOutcome(outcome, result, "outcome");
        }
    }

    private static void CheckOutcome(JsonObject spec, object? result, string path)
    {
        if ((bool?)spec["argument_error"] == true)
        {
            if (result is not ArgumentException)
            {
                throw Fail($"{path}: expected an argument error, got {result?.GetType().Name ?? "null"}");
            }
            return;
        }
        if (spec.ContainsKey("result"))
        {
            if (result is not Decision d)
            {
                throw Fail($"{path}: expected a decision, got {result}");
            }
            if (spec["decision"] is { } decision)
            {
                Match(Canon(decision), Canon(Driver.Decision(d)), path + ".decision");
            }
            return;
        }
        if (result is not DecisionNodeError error)
        {
            throw Fail($"{path}: expected {spec["error"]}, got a result");
        }
        CheckError(spec, error, path);
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
