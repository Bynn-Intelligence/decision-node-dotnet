# DecisionNode for .NET

The official .NET client for the DecisionNode API: ask yes-or-no, choice and score questions about any state, and get calibrated probabilities back.

## Install

```sh
dotnet add package Bynn.DecisionNode
```

Requires .NET 8 or later.

## Set your API key

```sh
export DECISIONNODE_API_KEY="dn_..."
```

Or pass it to the client: `new DecisionNodeClient(apiKey: "dn_...")`.

## Quick start

```csharp
using Bynn.DecisionNode;
using static Bynn.DecisionNode.Question;

using var dn = new DecisionNodeClient(); // reads DECISIONNODE_API_KEY

var d = await dn.DecideAsync(
    state: new { ticket = "I was charged twice and nobody has replied for 3 days." },
    questions: new Dictionary<string, Question>
    {
        ["route"] = Choice("Where should this go?", new Dictionary<string, string>
        {
            ["billing"] = "money", ["bug"] = "broken", ["account"] = "login",
        }),
        ["urgency"] = Score("How urgent is this?", new[] { "routine", "today", "urgent", "critical" }),
        ["escalate"] = Truth("Escalate to a human now?"),
    });

var route = d.Choice("route");
Console.WriteLine(route.Choice);                  // billing
Console.WriteLine(route.Probabilities["billing"]); // 0.87
Console.WriteLine(d.Score("urgency").Score);      // 2.31
Console.WriteLine(d.Truth("escalate").Truth);     // 0.94
```

The values you read are the values the API sent: nothing is rounded, renormalised or thresholded. What 0.94 means for your product is your code's decision.

## Questions

| Builder | Asks | Answer |
|---|---|---|
| `Truth(instructions, ifTrue, ifFalse)` | Is this true? `ifTrue` and `ifFalse` optionally say what counts as each. | `TruthAnswer.Truth`: the probability it is true |
| `Choice(instructions, options)` | Which one option fits? `options` is a list of names or a map of name to description. | `ChoiceAnswer.Choice`, `.Confidence`, `.Probabilities` by option name |
| `Score(instructions, levels)` | Where on this scale, from low to high? Up to 10 levels. | `ScoreAnswer.Score`, `.Confidence`, `.Probabilities` and `.Legend` by level index |

`state`, instructions, descriptions and levels take a string or any JSON-serialisable value: an anonymous object, a record, a `JsonNode`. The wire form works too, so a request copied from the API documentation runs unchanged:

```csharp
Question q = new JsonObject { ["type"] = "truth", ["instructions"] = "Is this spam?" };
```

Answers are read by question id: `d["route"]`, or typed with `d.Truth(id)`, `d.Choice(id)` and `d.Score(id)`. An answer of a type this version does not know yet, or of a known type with a missing or wrongly typed field, comes back as an `UnknownAnswer` with its type in `Type` and its fields in `Raw`; the rest of the decision parses as usual. The helpers `CheckAsync`, `ClassifyAsync` and `ScoreAsync` promise a typed answer, so when their own answer is missing or malformed they throw a `ServerError` (with `Status`, `RequestId` and `Reason`) instead.

## The client

| Method | Does |
|---|---|
| `DecideAsync(state, questions, images, model)` | One decision with any number of questions |
| `DecideManyAsync(requests, concurrency = 8)` | Many decisions, at most `concurrency` at a time. One `DecisionResult` per request, in input order, holding a `Decision` or the `Error` that request ended with. One failure does not stop the others. A `concurrency` below 1, or a bad argument in any request, throws an `ArgumentException` before anything is sent. |
| `CheckAsync(state, instructions, ifTrue, ifFalse, images, model)` | One yes-or-no question; returns its `TruthAnswer` |
| `ClassifyAsync(state, instructions, options, images, model)` | One choice question; returns its `ChoiceAnswer` |
| `ScoreAsync(state, instructions, levels, images, model)` | One score question; returns its `ScoreAnswer` |
| `GateAsync(action, context, images, model)` | Judges an action before it runs: a `risk` score, a `touches_production` truth and an `allow`, `confirm` or `block` verdict |
| `ModelsAsync()` | The models and their published limits, one `ModelInfo` each |
| `DecideRawAsync(body)` | Sends a body exactly as given and returns the parsed response. For API fields this version does not know yet. |

Every method takes a `CancellationToken`. A client is safe to share between threads and reuses its connections: create one and keep it.

```csharp
var verdict = await dn.CheckAsync(
    state: "Refund me now or I cancel.",
    instructions: "Is the customer asking for a refund?",
    ifTrue: "an explicit request for money back");

var results = await dn.DecideManyAsync(tickets.Select(t => new DecideRequest
{
    State = t,
    Questions = new Dictionary<string, Question> { ["spam"] = Truth("Is this spam?") },
}));
```

Each `Decision` also carries `Model` (the version that answered), `Usage.InputTokens`, `Usage.OutputTokens`, `RequestId`, `LatencyMs` (the server's), `Safety` and `Raw` (the parsed body). A latency or token count the server did not send is `null`, never 0.

`GateAsync` sends the state `{"action": ..., "context": ...}` and three questions with fixed wording, the same in every official DecisionNode SDK:

| Id | Type | Instructions |
|---|---|---|
| `risk` | score | "How much harm could this action cause if it is wrong?", levels `none`, `low`, `moderate`, `high`, `severe` |
| `touches_production` | truth | "Does this action change a production system, production data or anything customers use?" |
| `verdict` | choice | "Should this action run as it is, wait for a person to confirm it, or be blocked?", options `allow` "safe to run without asking", `confirm` "a person should confirm it first", `block` "should not run" |

`DecisionNodeClient.GateQuestions()` returns the same three questions.

## Images

```csharp
var d = await dn.DecideAsync(
    state: new { claim = new { total = "740.00", currency = "SEK" } },
    questions: new Dictionary<string, Question> { ["matches"] = Truth("Does the receipt total equal the claim?") },
    images: new[] { Image.FromFile("receipt", "receipt.jpg") });
```

`Image.FromFile(id, path)` reads a JPEG, PNG or WebP file when the `Image` is made and takes its media type from the first bytes, so an unreadable file throws `ArgumentException` there. `Image.FromBytes(id, data)` detects the media type from the bytes the same way; pass `mediaType` (`image/jpeg`, `image/png` or `image/webp`) to set it yourself. Bytes of any other format without a media type throw `ArgumentException`. `Image.FromUrl(id, url)` (`https` only) also works. Text in the state or the instructions can refer to an image by its id. The API's limits apply: 4 images a request, 10 MB each.

## Safety

On the hosted API every decision carries the result of a safety check in `decision.Safety`: `Checked`, `Harm`, `SelfHarm`, `Flagged` (the categories above your key's thresholds) and, when the check did not run, `Reason`. A self-hosted server sends none, and `Safety` is `null`. A request refused under the usage policy throws `RefusalError`, which is never retried.

The scores always arrive in the `x-decisionnode-safety` response header. To have them in the response body as well, send the request header `x-decisionnode-safety-body: true` through `headers`; the body then carries a top-level `safety` field, read as `decision.Raw["safety"]`:

```csharp
using var dn = new DecisionNodeClient(headers: new Dictionary<string, string>
{
    ["x-decisionnode-safety-body"] = "true",
});
```

## Errors

Every error derives from `DecisionNodeError` (catch that name to catch them all), which carries `Status`, `ErrorType`, `RequestId` and `Reason`: the server's explanation in words from the `x-decisionnode-error` header, for example "question 'q': a score takes up to 10 levels, got 11". The exception's own `Message` is the server's message exactly (`detail.message`, or `detail` when it is a string; empty when the server sent neither), or the library's description when no response arrived.

| Status | Error | Extra |
|---|---|---|
| 400 | `BadRequestError`; `ContextLimitError` when the request is longer than the model takes | |
| 401, or no key configured | `AuthenticationError` | |
| 402 | `InsufficientCreditError` | |
| 403 | `RefusalError` for a refusal, else `PermissionDeniedError` | `Category` (`harm` or `self_harm`), `Safety` |
| 404 | `NotFoundError` | |
| 422 | `ValidationError` | `Entries` |
| 429 | `RateLimitError` | `RetryAfter` |
| 529 | `OverloadedError` | `RetryAfter` |
| other 5xx | `ServerError` | |
| 2xx with a body that is not JSON | `ServerError` | |
| 2xx where a helper's own answer is missing or malformed | `ServerError` | |
| any other status, 3xx included | `DecisionNodeError` itself, with `Status` | |
| no response | `ConnectionError`, `TimeoutError` | |

```csharp
try
{
    var d = await dn.DecideAsync(state, questions);
}
catch (RefusalError e)
{
    Console.WriteLine($"refused ({e.Category}): {e.Message}");
}
catch (DecisionNodeError e)
{
    Console.WriteLine($"{e.Status} {e.Reason} (request {e.RequestId})");
}
```

The library checks very little before sending. A bad argument throws .NET's own `ArgumentException` (or its subclasses `ArgumentNullException` and `ArgumentOutOfRangeException`), never a DecisionNode error: an empty question map, a value that cannot be serialised, an unreadable or unrecognised image, a base URL that is not `https`, a `timeout` that is not above zero, a negative `maxRetries`, a `concurrency` below 1, an `HttpClient` that cannot be shown to refuse redirects (including one with a `DelegatingHandler` you have not declared safe), and an `HttpClient` that replaces the certificate check for an `https` base URL. `DecideManyAsync` checks every request before it sends any, so one bad request means nothing is sent. A missing key is an `AuthenticationError`, also before anything is sent. Everything else is the server's to judge, because limits differ by model and are published by `ModelsAsync()`.

## Timeouts and retries

Each attempt has 10 seconds, from sending the request to the last byte of the response body. Failed attempts are retried twice by default on 408, 429, every 5xx (529 included), timeouts and connection errors. The wait is the server's `retry-after-ms` when sent, else `Retry-After` in seconds or as an HTTP date, capped at 60 seconds; without either, exponential backoff from 0.5 seconds with jitter, capped at 8 seconds. 400, 401, 402, 403, 404 and 422 are never retried. A decision changes nothing on the server, so a retry is always safe.

Only a 2xx status is a success. Redirects are never followed: a 3xx is a `DecisionNodeError` with its status, so your key is never sent to another URL or over plain HTTP. The same holds for an `HttpClient` you pass in (see Configuration).

Your `CancellationToken` cancels the whole call, retries included, and surfaces as `OperationCanceledException`, not `TimeoutError`.

## Configuration

| Setting | Argument | Environment variable | Default |
|---|---|---|---|
| API key | `apiKey` | `DECISIONNODE_API_KEY` | none |
| Base URL | `baseUrl` | `DECISIONNODE_BASE_URL` | `https://api.decisionnode.com` |
| Model | `model` | `DECISIONNODE_MODEL` | `decisionnode-latest` |
| Timeout per attempt | `timeout` (above zero) | | 10 seconds |
| Retries | `maxRetries` (0 or more) | | 2 |
| Extra headers | `headers` | | none |
| HTTP client | `httpClient` | | one the client creates and owns |

An argument beats the environment, which beats the default. The base URL must be `https`; plain `http` is accepted only for `localhost`, `127.0.0.1` and `::1` (a local or self-hosted server), and anything else throws `ArgumentException` when the client is made, so a key never travels unencrypted. An empty string counts as not given, for the client's arguments and for a call's `model`. Pin a version with `model: "decisionnode-1.0"`, or use `decisionnode-flash-latest` for the faster, lower-cost model. `DecisionNodeClientOptions` holds the same settings, plus `DebugLog`, which receives one line per attempt (method, path, status, request id and timing; never the state, the questions or the key).

`headers` are added to every request; `x-decisionnode-safety-body: true` is the one the API reads (see Safety).

With dependency injection, register one client as a singleton, or pass an `HttpClient` of your own. A client you pass in is not disposed, and its `BaseAddress` and `Timeout` are not used. The constructor inspects its handler chain and throws `ArgumentException` unless every handler in it is of a type known never to follow a redirect, matched by exact type:

- The handler must be exactly `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = false`, or Microsoft's `WinHttpHandler` (from its own package, checked by assembly and signature) with `AutomaticRedirection = false`. `AllowAutoRedirect` is on by default in .NET, so set it.
- A subclass of those handlers is refused, even an empty one, because a subclass can override `SendAsync` and follow a redirect itself. So is any other handler, and an `HttpClient` subclass.
- A `DelegatingHandler` anywhere in the chain is refused, because one can send a request, and the key with it, again to another URL. If every `DelegatingHandler` in the chain passes each request on once, unchanged, and never sends it again (no redirect following, no retries, no re-routing), set `DelegatingHandlersNeverResendRequests = true` in `DecisionNodeClientOptions` to accept it. The terminal handler is checked either way, and a delegating handler without an inner handler is still refused.
- For an `https` base URL, a handler that sets its own certificate validation callback (`SslOptions.RemoteCertificateValidationCallback`, `ServerCertificateCustomValidationCallback` or `ServerCertificateValidationCallback`) is refused, because such a callback can switch off the certificate and host name checks. The platform's checks always apply.

Create it like this:

```csharp
var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
using var dn = new DecisionNodeClient(httpClient: http);
```

`IHttpClientFactory` wraps the primary handler in `DelegatingHandler`s of its own (logging and handler lifetime), and adds any you register, so a client from it is accepted only with the statement above. Check every handler you register first: a resilience or retry handler sends a request again and must not be in this chain.

```csharp
services.AddHttpClient("decisionnode")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

using var dn = new DecisionNodeClient(new DecisionNodeClientOptions
{
    HttpClient = factory.CreateClient("decisionnode"),
    DelegatingHandlersNeverResendRequests = true,
});
```

If a delegating handler re-sends a request to another URL anyway, the key has already gone with it; the call then throws a `DecisionNodeError` and its answer is never used. The client the library creates for itself never follows redirects.

The library sends no telemetry and no request other than to the base URL. Beyond your key and the standard `Accept` and `Content-Type`, the only header it adds is `User-Agent: decisionnode-dotnet/<version>`.

## Versioning

This library implements version 1.0 of the DecisionNode SDK specification (`DecisionNodeClient.SpecVersion`), the same surface every official DecisionNode SDK implements, checked by a shared conformance suite. See [CHANGELOG.md](CHANGELOG.md).

## Development

```sh
dotnet test tests/Bynn.DecisionNode.Tests
```

```sh
DECISIONNODE_CONFORMANCE_DIR=/path/to/decision-node-conformance dotnet test tests/Bynn.DecisionNode.Conformance
```

The conformance tests start the DecisionNode conformance suite's mock server and run every case of its five kinds (requests, responses, errors, behaviour, constants) against it. Error cases that name `catch_as` are caught through a `catch (DecisionNodeError)` clause. They need Python 3.10 or later and `DECISIONNODE_CONFORMANCE_DIR` set to a checkout of the suite (version 1.3); without it every conformance test fails, none is skipped. CI checks the suite out at the commit pinned in `DECISIONNODE_CONFORMANCE_REF` in `.github/workflows/ci.yml`, with the `DECISIONNODE_CONFORMANCE_TOKEN` secret and `persist-credentials: false`, and runs it on every push. Every action in the workflow is pinned to a commit SHA.

## License

MIT. See [LICENSE](LICENSE).
