# Changelog

All notable changes to this library are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - Unreleased

First release. Implements version 1.1 of the DecisionNode SDK specification: every public call of the API.

### Added

- Number questions: `Question.Number(instructions, min, max, step)`, which leaves out what is not given so the server's defaults apply, `NumberAsync`, and `NumberAnswer` with `Number`, `Expected`, `Confidence` and `Probabilities` keyed by each value exactly as the server wrote it.
- `SystemoneAsync` for `POST /v1/systemone`, which always sends a model and writes truth questions with the wire type that path takes. Its yes-or-no answers are read as `TruthAnswer`, as on `/v1/decide`.
- `ModelInfo` accessors for the limits of spec 1.1: `ContextTokens`, `MaxImages`, `UploadMaxBytes`, `UploadTtlSeconds`, `UploadMediaTypes`, the number grid fields, `Sessions` and `Batch`.
- Images from a `byte[]`, a `ReadOnlyMemory<byte>`, a stream, base64 text or an upload id, besides a file and a URL. URLs pass through after a local check (`https`, at most 2,048 characters). Local bytes are checked before any request: JPEG, PNG or WebP by their first bytes, at most 10 MB, at most 40 megapixels read from the JPEG, PNG or WebP header without decoding, at most 4 images, unique ids.
- The inline threshold: a request's local images that total under `DefaultInlineThreshold` (524,288 bytes; `InlineThreshold` overrides it) go inline; at or above it they are uploaded with one `POST /v1/uploads` and parallel `PUT`s that carry exactly the presigned headers, never the key, and never follow a redirect. `UploadAsync` uploads explicitly.
- An in-memory upload cache by content (256 entries, least recently used out first, used until 5 minutes before an upload expires), shared by concurrent requests, so a repeated image goes by its `upload_id` whatever its size. A stale upload the library placed is uploaded again and the request sent once more.
- The API's fixed request rules checked before any request, raising the API's own class with no status: `BadRequestError` for images, choice options, score levels and truth or number instructions; `ValidationError` for number grid faults and a score without levels.
- Sessions on `System.Net.WebSockets.ClientWebSocket`: `OpenSessionAsync`, `Session.ConnectAsync`, `SendAsync` with `seq` counted from 1, `RepliesAsync` of `Reply`, `FrameError` and `SessionClosed` values, `EndAsync`, and `SuperviseAsync`, which delivers each reply as it arrives and reopens on the reopen codes. `SessionLimitError`, a `RateLimitError`, is never retried.
- The Batch API: `Batches.CreateAsync`, `AddAsync` (split into calls of at most 10 MiB), `FinalizeAsync`, `GetAsync`, `ListAsync`, `ResultsAsync` (read line by line) and `CancelAsync`, and `RunBatchAsync` with a fixed poll interval of at least 10 seconds. Create, add and finalize are retried only on 429 and 529.
- HTTP/2 where the server offers it, so concurrent calls share connections.
- `DecisionNodeClient` with `DecideAsync`, `DecideManyAsync`, `CheckAsync`, `ClassifyAsync`, `ScoreAsync`, `GateAsync`, `ModelsAsync` and `DecideRawAsync`, each taking a `CancellationToken`. The helpers take `images` and `model` like `DecideAsync`.
- `GateAsync` with fixed question wording, the same in every official DecisionNode SDK.
- `DecideManyAsync` returning one `DecisionResult` per request; a `concurrency` below 1, or a bad argument in any request, is an `ArgumentException` before anything is sent.
- Question builders `Question.Truth`, `Question.Choice` and `Question.Score`, and wire-form questions from a `JsonObject`.
- Typed answers: `TruthAnswer`, `ChoiceAnswer` and `ScoreAnswer`, with unknown answer types, and known types with a missing or wrongly typed field, kept as an `UnknownAnswer` holding the raw fields. Numbers are returned exactly as received; a missing latency or token count is `null`.
- `ModelInfo` for each entry of `ModelsAsync()`, and the constants `DecisionNodeClient.SpecVersion` (`"1.1"`) and `DecisionNodeClient.DefaultInlineThreshold`.
- The safety check's result on every decision, `RefusalError` for refused requests, and documentation of the `x-decisionnode-safety-body` request header.
- Timeouts of 10 seconds per attempt, covering the whole attempt to the last byte of the body, and 2 retries on 408, 429, 5xx, timeouts and connection errors, honouring `retry-after-ms` and `Retry-After` (seconds or an HTTP date), capped at 60 seconds.
- Typed errors deriving from `DecisionNodeError`, including `PermissionDeniedError` for a 403 that is not a refusal. A status without a class of its own is `DecisionNodeError` itself, and the exception's `Message` is the server's message (empty, never `null`, when the server sent none).
- Only a 2xx status is a success, and redirects are never followed, also with an `HttpClient` passed in. Its handler chain is accepted only when the terminal handler is exactly `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = false`, or Microsoft's `WinHttpHandler` (from its own package, checked by assembly and signature) with `AutomaticRedirection = false`; subclasses of those handlers, any other handler and `HttpClient` subclasses are refused with an `ArgumentException`.
- A `DelegatingHandler` in a passed-in `HttpClient`'s chain is refused, because it can send a request and the key again to another URL, unless `DecisionNodeClientOptions.DelegatingHandlersNeverResendRequests` states that every one passes each request on once and never re-sends it. A client from `IHttpClientFactory` needs that statement.
- An `HttpClient` passed in whose handler sets its own certificate validation callback is refused for an `https` base URL, so the certificate and host name checks always apply.
- `CheckAsync`, `ClassifyAsync` and `ScoreAsync` throw `ServerError`, with the response's status, request id and `x-decisionnode-error` reason, when their own answer is missing or malformed; `DecideAsync` keeps such an answer as an `UnknownAnswer`.
- The base URL must be `https`; plain `http` is accepted only for `localhost`, `127.0.0.1` and `::1`.
- Local argument problems (an empty question map, a value that cannot be serialised, an unreadable image file, a bad base URL, a `timeout` not above zero, a negative `maxRetries`) throw .NET's own `ArgumentException`, never a DecisionNode error class.
- Configuration from arguments, then `DECISIONNODE_API_KEY`, `DECISIONNODE_BASE_URL` and `DECISIONNODE_MODEL`, then defaults. An empty argument counts as not given.
- CI pins every action to a commit SHA, checks out without persisting credentials, and runs the conformance suite at a pinned commit.
- Opt-in debug logging of method, path, status, request id and timing.
