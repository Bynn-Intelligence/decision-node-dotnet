# Changelog

All notable changes to this library are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - Unreleased

First release. Implements version 1.0 of the DecisionNode SDK specification.

### Added

- `DecisionNodeClient` with `DecideAsync`, `DecideManyAsync`, `CheckAsync`, `ClassifyAsync`, `ScoreAsync`, `GateAsync`, `ModelsAsync` and `DecideRawAsync`, each taking a `CancellationToken`. The helpers take `images` and `model` like `DecideAsync`.
- `GateAsync` with fixed question wording, the same in every official DecisionNode SDK.
- `DecideManyAsync` returning one `DecisionResult` per request; a `concurrency` below 1, or a bad argument in any request, is an `ArgumentException` before anything is sent.
- Question builders `Question.Truth`, `Question.Choice` and `Question.Score`, and wire-form questions from a `JsonObject`.
- Typed answers: `TruthAnswer`, `ChoiceAnswer` and `ScoreAnswer`, with unknown answer types, and known types with a missing or wrongly typed field, kept as an `UnknownAnswer` holding the raw fields. Numbers are returned exactly as received; a missing latency or token count is `null`.
- `ModelInfo` for each entry of `ModelsAsync()`, and the constant `DecisionNodeClient.SpecVersion` (`"1.0"`).
- Images from a file (read when the `Image` is made, media type detected from the bytes), from bytes (media type detected when not given), or from an `https` URL.
- The safety check's result on every decision, `RefusalError` for refused requests, and documentation of the `x-decisionnode-safety-body` request header.
- Timeouts of 10 seconds per attempt, covering the whole attempt to the last byte of the body, and 2 retries on 408, 429, 5xx, timeouts and connection errors, honouring `retry-after-ms` and `Retry-After` (seconds or an HTTP date), capped at 60 seconds.
- Typed errors deriving from `DecisionNodeError`, including `PermissionDeniedError` for a 403 that is not a refusal. A status without a class of its own is `DecisionNodeError` itself, and the exception's `Message` is the server's message (empty, never `null`, when the server sent none).
- Only a 2xx status is a success, and redirects are never followed, also with an `HttpClient` passed in. Its handler chain is accepted only when the terminal handler is exactly `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = false`, or Microsoft's `WinHttpHandler` (from its own package, checked by assembly and signature) with `AutomaticRedirection = false`; subclasses of those handlers, any other handler and `HttpClient` subclasses are refused with an `ArgumentException`.
- A `DelegatingHandler` in a passed-in `HttpClient`'s chain is refused, because it can send a request and the key again to another URL, unless `DecisionNodeClientOptions.DelegatingHandlersNeverResendRequests` states that every one passes each request on once and never re-sends it. A client from `IHttpClientFactory` needs that statement.
- An `HttpClient` passed in whose handler sets its own certificate validation callback is refused for an `https` base URL, so the certificate and host name checks always apply.
- `CheckAsync`, `ClassifyAsync` and `ScoreAsync` throw `ServerError`, with the response's status, request id and `x-decisionnode-error` reason, when their own answer is missing or malformed; `DecideAsync` keeps such an answer as an `UnknownAnswer`.
- The base URL must be `https`; plain `http` is accepted only for `localhost`, `127.0.0.1` and `::1`.
- Local argument problems (an empty question map, a value that cannot be serialised, an unreadable or unrecognised image, a bad base URL, a `timeout` not above zero, a negative `maxRetries`) throw .NET's own `ArgumentException`, never a DecisionNode error class.
- Configuration from arguments, then `DECISIONNODE_API_KEY`, `DECISIONNODE_BASE_URL` and `DECISIONNODE_MODEL`, then defaults. An empty argument counts as not given.
- CI pins every action to a commit SHA, checks out without persisting credentials, and runs conformance suite 1.3 at a pinned commit.
- Opt-in debug logging of method, path, status, request id and timing.
