# Changelog

All notable changes to this library are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.3.0] - Unreleased

Implements version 1.3 of the DecisionNode SDK specification (`SpecVersion` is `"1.3"`): boosters and fine-tuned model names.

### Added

- Boosters: `Booster` (a name converts implicitly, `Booster.Of(name, images, require, options)` builds the object form, `{"name", "images"?, "require"?, "options"?}`, with each argument left out when not given, or the name alone when no other argument is given, `Booster.FromJson` takes the wire form as given), with `Booster.Age` and `Booster.AiGenerated`. `boosters` on `DecideAsync`, `CheckAsync`, `ClassifyAsync`, `ScoreAsync`, `NumberAsync`, `GateAsync`, `DecideRequest` (so `DecideManyAsync` and `CheckRequest`) and `BatchRequest`, sent in the order given and left out of the body when there are none. Nothing about boosters is checked before sending; their `options` count toward the 128-level nesting limit. Sessions take no boosters.
- `Decision.Evidence`: one `Evidence` per booster the request named (`Version`, `Status`, `LatencyMs`, `BilledCalls`, `Results`), by name in the request's order, `null` when it named none. Readings are `AgeReading`, `AiGeneratedReading` (with `TopGenerator`) or, for a booster this version does not know or a reading with a missing or wrongly typed field, `UnknownReading` with its raw fields; the decision does not fail.
- `Usage.BoosterTokens` and `Usage.Boosters` (`null` for a request without boosters), in batch results too.
- `DecisionNodeError.Boosters`: each booster's `BoosterStatus` (`Status`, `LatencyMs`) from the `x-decisionnode-boosters` response header, in order; empty when the header is absent.
- The booster errors use the existing classes: `booster_unknown`, `booster_input_mismatch` and a malformed list (`api_usage_error`) are `BadRequestError`; `booster_not_enabled`, like every 403 that is not a refusal, is `PermissionDeniedError`, never retried; `booster_unavailable` is an `OverloadedError`, retried like any 529.
- `ModelInfo.FineTuned`, `Base`, `AdapterVersion` and `Status` for the fine-tuned models `ModelsAsync` lists; `null` on a catalogue entry.
- `fallback` on `DecideAsync`, `CheckAsync`, `ClassifyAsync`, `ScoreAsync`, `NumberAsync`, `GateAsync`, `DecideRequest` (so `DecideManyAsync` and `CheckRequest`) and `BatchRequest`, sent as `"fallback": true` or `false` only when given. `Decision.Fallback` (a `Fallback` with `ServedModel` and `RequestedModel`) when another model answered for the one asked for, read from the `x-decisionnode-fallback` and `x-decisionnode-requested-model` headers; `null` on an ordinary answer. A `fallback: false` request that no worker can take is an `OverloadedError`, retried like any 529.
- The API's 25 MiB body limit: `DecisionNodeClient.MaxBodyBytes` (26,214,400). A body over it, measured as it would be sent (inline images and frames in it, an image that would be uploaded named by its upload id), throws the new `RequestTooLargeError` before anything is sent or uploaded, on `DecideAsync`, the helpers, `DecideManyAsync` (for the whole call), `CheckRequest` and each batch line. The API's `413` is a `RequestTooLargeError` too, never retried.

- An image id longer than 64 characters (counted as Unicode code points) throws `BadRequestError` (`api_usage_error`, `Image <n>: its id is longer than 64 characters.`) before anything is sent, checked image by image after the empty-id check and before the duplicate check.
- `Usage.Cost`: what each answer cost, as the API sends it (`Usd`, `ModelUsd`, `BoostersUsd`, `FreeTier`, and the raw object), on decisions, batch result lines and session replies; `null` when absent, never zeros, and `Usd` is never computed from the parts.
- A request naming the `ocr` booster has 30 seconds an attempt when no `Timeout` was set (a set `Timeout` is used as given) and is never retried after a timeout, since the server finishes the attempt and may bill it; its other retries stay. `DecideAsync`, the helpers and `DecideManyAsync`; not sessions, batches or `DecideRawAsync`.

### Changed

- The default model is `decisionnode-moe` (it was `decisionnode-latest`). The model ids are the families `decisionnode-moe` and `decisionnode-flash`, which always mean the current release; a pinned id, `decisionnode-<family>-<version>`, is read from `ModelsAsync()`, and a decision's `Model` is the pinned id that answered. `model` stays free text.
- An error whose body is not JSON (an HTML page from a proxy, plain text, an empty body) is classified by its status alone, with `ErrorType` `null`, `RequestId` and `Reason` from their headers, and the message `HTTP <status> <text>` plus the request id when there is one; the body is never read into the error.
- `boosters` and `fallback` sit after `model` in the parameter lists of `DecideAsync` and the helpers, before the `CancellationToken`; callers that pass the token by position pass it by name.
- A fine-tuned model's name (`decisionnode-flash@acme/fraud`, or `...:3` for one version) goes in `model` as given; the decision's `Model` is the pinned name that answered.
- Every `413`, a presign's and a batch add's included, is a `RequestTooLargeError` (the base `DecisionNodeError` before).
- CI runs conformance suite 1.11.

## [0.2.0] - Unreleased

Implements version 1.2 of the DecisionNode SDK specification (`SpecVersion` is `"1.2"`): sixteen images a request, videos sent as frames, and point and box questions.

### Added

- Videos as frames: `Video.Of(id, frames, fps, start, end)` and `VideoFrame.FromBytes(t, data, mediaType)` or `VideoFrame.FromBase64(t, text, mediaType)`, or either in the API's wire form with `Video.FromJson` and `VideoFrame.FromJson`. Sent as the request's `videos` field, frames inline as base64 (never uploaded, cached, resized or counted toward the inline threshold), `fps`, `start` and `end` left out when not given. Only frames are sent, never a video file, a URL or sound.
- `videos` on `DecideAsync`, `CheckAsync`, `ClassifyAsync`, `ScoreAsync`, `NumberAsync`, `GateAsync`, `DecideRequest` (so `DecideManyAsync` and `CheckRequest`) and `BatchRequest`. Sessions take no videos.
- Local video checks before anything is sent, each a `BadRequestError` with `api_usage_error` and the API's message: an id that is a non-empty string and unique across images and videos, at least one frame, frame times that are numbers of at least 0 and increase, a frame media type of JPEG, PNG or WebP (detected from the bytes when not given), `fps` above 0 and at most 4, `start` and `end` of at least 0, and `end` after `start`. Frame counts, frame bytes and the token budget are left to the server.
- `Usage.VideoFrames` and `Usage.VideoSeconds` (`null` for a request without videos) and `Decision.Warnings` (the server's notes as sent, empty when there are none), in batch results too.
- Point and box questions: `Question.Point(instructions, image, units)` and `Question.Box(instructions, image, units)`, `image` and `units` left out when not given. `PointAnswer` (`X`, `Y`, `Present`, `Confidence`, `Image`, `Width`, `Height`) and `BoxAnswer` (`X1`, `Y1`, `X2`, `Y2`, `Present`, `Confidence`, `Image`, `Width`, `Height`), numbers as received; an answer with a missing or wrongly typed field is an `UnknownAnswer`. `Answer.AsPoint`, `AsBox`, `Decision.Point` and `Decision.Box`. They go with `DecideAsync`, `DecideManyAsync`, batch lines and `CheckRequest`.
- Local point and box checks, raised as the API's `ValidationError` with every fault of the request in one list: a request without an image (`image_required`), no `image` in a request with two or more images (`missing`), an `image` that is not a string (`string_type`) or not the id of one of the request's images (`unknown_image`), and `units` other than `pixels` and `normalized` (`literal_error`). Missing instructions are the API's `BadRequestError` `Point question must have instructions: <id>`. A session's open leaves the image rules to its frames.

### Changed

- Up to 16 images a request (4 before) and up to 4 videos. More is refused locally as the API's `422`: a `ValidationError` with a `too_long` entry on `["body", "images"]` or `["body", "videos"]` (`max_length` 16 or 4; a lower image limit held from `ModelsAsync`, the entry's `max_images` else its `images.max_images`, applies instead), listed after the state's and the questions' entries, images before videos. The entry's `input` lists the items in their wire form, a local image as its `id` and `media_type` only.
- One `POST /v1/uploads` takes up to 16 files; `UploadAsync` with more than 16 images raises the API's `BadRequestError` `files: 1 to 16 files, got 17.` before anything is sent.
- `videos` sits beside `images` in the helpers' parameter lists, before `model`; callers that pass `model` by position pass it by name.
- CI runs conformance suite 1.10.

## [0.1.0] - Unreleased

First release. Implements version 1.1 of the DecisionNode SDK specification: every public call of the API.

### Added

- Number questions: `Question.Number(instructions, min, max, step)`, which leaves out what is not given so the server's defaults apply, `NumberAsync`, and `NumberAnswer` with `Number`, `Expected`, `Confidence` and `Probabilities` keyed by each value exactly as the server wrote it.
- `ModelInfo` accessors for the limits of spec 1.1: `ContextTokens`, `MaxImages`, `UploadMaxBytes`, `UploadTtlSeconds`, `UploadMediaTypes`, the number grid fields, `Sessions` and `Batch`.
- Images from a `byte[]`, a `ReadOnlyMemory<byte>`, a stream, base64 text or an upload id, besides a file and a URL. URLs pass through after a local check (`https`, at most 2,048 characters). Local bytes are checked before any request: JPEG, PNG or WebP by their first bytes, at most 10 MB, at most 40 megapixels read from the JPEG, PNG or WebP header without decoding, at most 4 images, unique ids.
- The inline threshold: a request's local images that total under `DefaultInlineThreshold` (524,288 bytes; `InlineThreshold` overrides it) go inline; at or above it they are uploaded with one `POST /v1/uploads` and parallel `PUT`s that carry exactly the presigned headers, never the key, and never follow a redirect. `UploadAsync` uploads explicitly.
- An in-memory upload cache by content (256 entries, least recently used out first, used until 5 minutes before an upload expires), shared by concurrent requests, so a repeated image goes by its `upload_id` whatever its size. A stale upload the library placed is uploaded again and the request sent once more.
- Every fixed rule the API documents checked before anything is sent, in the server's order, raising what the server would answer with no status and no request id: the nesting limit of 128; question types, fields and their types; choice options, score levels and truth or number instructions (`BadRequestError` with the API's message and no `error_type`); the number grid (`ValidationError` with the server's exact `entries`); images (`api_usage_error`), and image URLs with user information, a port other than 443 or a literal blocked address (`image_url_refused`); session `window` and `ttl_seconds`; frames; batch `custom_id`s and an empty add; `ListAsync` paging. Limits only `/v1/models` states (number support, session limits, `requests_per_batch`, `max_custom_id_chars`) are checked once `ModelsAsync` has been called on the client, and left to the server otherwise.
- `CheckRequest(DecideRequest)`, `CheckRequest(SessionOptions)`, `Batches.CheckCreate`, `Batches.CheckAdd` and `Session.CheckFrame`: every local check the call would run, held `/v1/models` limits included, run alone. They throw exactly what the call would throw, send and upload nothing, and are synchronous.
- Limits held from `/v1/models` apply to a model named by an entry's `name`, or by the `alias` or pinned `version` an entry states; a name the list does not state leaves the checks to the server.
- `UploadTimeout` (default 60 seconds) bounds each storage `PUT` attempt, apart from the API's `Timeout`; a `PUT` that outlives it is a `TimeoutError` after its retries.
- The session WebSocket connects through the client's `HttpClient`, so a proxy or certificate setting configured there applies to it as well.
- `Session.SendAsync` refuses a frame that breaks a fixed rule with `BadRequestError` (`api_usage_error`): nothing is written and no `seq` is used.
- A storage `PUT` answered `403` after its URL's lifetime (`expires_in`, 300 seconds, by the client's clock since the presign answered) is presigned again once; a second such `403` raises.
- Sessions on `System.Net.WebSockets.ClientWebSocket`: `OpenSessionAsync`, `Session.ConnectAsync`, `SendAsync` with `seq` counted from 1, `RepliesAsync` of `Reply`, `FrameError` and `SessionClosed` values, `EndAsync`, and `SuperviseAsync`, which delivers each reply as it arrives and reopens on the reopen codes. `SessionLimitError`, a `RateLimitError`, is never retried.
- The Batch API: `Batches.CreateAsync`, `AddAsync` (split into calls of at most 10 MiB), `FinalizeAsync`, `GetAsync`, `ListAsync`, `ResultsAsync` (the whole file, read line by line, with the client's timeout covering each wait for more of the body) and `CancelAsync`, and `RunBatchAsync` with a fixed poll interval of at least 10 seconds. Create, add and finalize are retried only on 429 and 529.
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
- CI pins every action to a commit SHA, checks out without persisting credentials, and runs conformance suite 1.6 at a pinned commit.
- Opt-in debug logging of method, path, status, request id and timing.
