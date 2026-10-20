# DecisionNode for .NET

[DecisionNode](https://decisionnode.com) is a decision engine: send it text, JSON or images with a question, and it answers with calibrated probabilities.

The official .NET client for the DecisionNode API: ask yes-or-no, choice, score and number questions about any state, and get calibrated probabilities back. One request at a time, many in parallel, a live stream of readings in a session, or a batch at a lower price than live calls.

## Install

```sh
dotnet add package Bynn.DecisionNode
```

Requires .NET 8 or later. No dependencies beyond the .NET libraries.

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
| `Number(instructions, min, max, step)` | Which value on this grid? Defaults 0, 255 and 1 when left out; at most 4,096 values. | `NumberAnswer.Number`, `.Expected`, `.Confidence`, `.Probabilities` by value as the server wrote it (`"7"`, `"3.5"`) |
| `Point(instructions, image, units)` | Where is it in the image? One spot. | `PointAnswer.X`, `.Y`, `.Present`, `.Confidence`, `.Image`, `.Width`, `.Height` (see Points and boxes) |
| `Box(instructions, image, units)` | Where is it in the image? A rectangle. | `BoxAnswer.X1`, `.Y1`, `.X2`, `.Y2`, `.Present`, `.Confidence`, `.Image`, `.Width`, `.Height` |

`state`, instructions, descriptions and levels take a string or any JSON-serialisable value: an anonymous object, a record, a `JsonNode`. The wire form works too, so a request copied from the API documentation runs unchanged:

```csharp
Question q = new JsonObject { ["type"] = "truth", ["instructions"] = "Is this spam?" };
```

```csharp
var cars = await dn.NumberAsync("Drone frame over row B4 of the north car park.", "How many cars are visible?", min: 0, max: 50,
    images: new[] { Image.FromUrl("frame", "https://files.example.com/frames/b4.jpg") });
Console.WriteLine(cars.Number);     // 7
Console.WriteLine(cars.Confidence); // 0.78
```

Answers are read by question id: `d["route"]`, or typed with `d.Truth(id)`, `d.Choice(id)`, `d.Score(id)`, `d.Number(id)`, `d.Point(id)` and `d.Box(id)`. An answer of a type this version does not know yet, or of a known type with a missing or wrongly typed field, comes back as an `UnknownAnswer` with its type in `Type` and its fields in `Raw`; the rest of the decision parses as usual. The helpers `CheckAsync`, `ClassifyAsync`, `ScoreAsync` and `NumberAsync` promise a typed answer, so when their own answer is missing or malformed they throw a `ServerError` (with `Status`, `RequestId` and `Reason`) instead.

## The client

| Method | Does |
|---|---|
| `DecideAsync(state, questions, images, videos, model, boosters, fallback)` | One decision with any number of questions, up to 16 images, up to 4 videos and up to 4 boosters |
| `DecideManyAsync(requests, concurrency = 8)` | Many decisions, at most `concurrency` at a time over shared connections. One `DecisionResult` per request, in input order, holding a `Decision` or the `Error` that request ended with. One failure does not stop the others. Every request is checked before any is sent: a `concurrency` below 1 or a bad argument throws `ArgumentException`, and a request that breaks one of the API's fixed rules throws its `BadRequestError` or `ValidationError`, with nothing sent. An image shared by several requests is uploaded once. |
| `CheckAsync(state, instructions, ifTrue, ifFalse, images, videos, model, boosters, fallback)` | One yes-or-no question; returns its `TruthAnswer` |
| `ClassifyAsync(state, instructions, options, images, videos, model, boosters, fallback)` | One choice question; returns its `ChoiceAnswer` |
| `ScoreAsync(state, instructions, levels, images, videos, model, boosters, fallback)` | One score question; returns its `ScoreAnswer` |
| `NumberAsync(state, instructions, min, max, step, images, videos, model, boosters, fallback)` | One number question; returns its `NumberAnswer` |
| `UploadAsync(images)` | Uploads images whatever their size and returns one `upload_id` each (see Images) |
| `OpenSessionAsync(state, questions, instructions, model, window, ttlSeconds)` | Opens a session (see Sessions) |
| `SuperviseAsync(body, feed, onReply)` | Keeps a feed of readings answered, reopening sessions as needed (see Sessions) |
| `Batches.*`, `RunBatchAsync(requests, pollInterval, timeout)` | The Batch API (see Batches) |
| `GateAsync(action, context, images, videos, model, boosters, fallback)` | Judges an action before it runs: a `risk` score, a `touches_production` truth and an `allow`, `confirm` or `block` verdict |
| `ModelsAsync()` | The models and their published limits, one `ModelInfo` each, your workspace's fine-tuned models included |
| `DecideRawAsync(body)` | Sends a body exactly as given and returns the parsed response. For API fields this version does not know yet. |

Every method takes a `CancellationToken`. A client is safe to share between threads and reuses its connections (HTTP/2 where the server offers it, so concurrent calls share one connection) and its image uploads: create one and keep it.

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

Each `Decision` also carries `Model` (the version that answered), `Usage.InputTokens`, `Usage.OutputTokens`, `RequestId`, `LatencyMs` (the server's), `Safety`, `Warnings`, `Evidence`, `Fallback` and `Raw` (the parsed body). A latency or token count the server did not send is `null`, never 0. For a request with videos, `Usage.VideoFrames` and `Usage.VideoSeconds` say how much of the videos was read (see Videos); for a request with boosters, `Evidence`, `Usage.BoosterTokens` and `Usage.Boosters` say what the boosters read (see Boosters).

`Usage.Cost` is what the answer cost, in USD exactly as the API booked it: `Usd` (the amount debited for this answer), `ModelUsd` (the model's part, 0 when the free tier covered it), `BoostersUsd` (the boosters' part) and `FreeTier` (`true` when the free tier covered the model's part). Read `Usd` as given rather than adding the parts, since the parts summed as doubles can differ from the booked amount in the last digit. Batch result lines and session replies carry their own `Usage.Cost`. It is `null` when the response does not carry it.

`GateAsync` sends the state `{"action": ..., "context": ...}` and three questions with fixed wording, the same in every official DecisionNode SDK:

| Id | Type | Instructions |
|---|---|---|
| `risk` | score | "How much harm could this action cause if it is wrong?", levels `none`, `low`, `moderate`, `high`, `severe` |
| `touches_production` | truth | "Does this action change a production system, production data or anything customers use?" |
| `verdict` | choice | "Should this action run as it is, wait for a person to confirm it, or be blocked?", options `allow` "safe to run without asking", `confirm` "a person should confirm it first", `block` "should not run" |

`DecisionNodeClient.GateQuestions()` returns the same three questions.

## Images

Send an image by URL when you can. Nothing is uploaded, the request stays small, and the server fetches the image while it reads the rest of the request:

```csharp
var d = await dn.DecideAsync(
    state: new { claim = new { total = "740.00", currency = "SEK" } },
    questions: new Dictionary<string, Question> { ["matches"] = Truth("Does the receipt total equal the claim?") },
    images: new[] { Image.FromUrl("receipt", "https://files.example.com/claims/4411/receipt.jpg") });
```

| Made with | Sent |
|---|---|
| `Image.FromUrl(id, url)` | As `{id, url}`. `https` and at most 2,048 characters; never fetched by the library |
| `Image.FromFile(id, path)`, `FromFileAsync` | Read when the `Image` is made (an unreadable file throws `ArgumentException` there), then as local bytes |
| `Image.FromBytes(id, data)` with a `byte[]` or `ReadOnlyMemory<byte>`, `FromStream`, `FromStreamAsync`, `FromBase64` | As local bytes. The bytes are not copied |
| `Image.FromUploadId(id, uploadId)` | As given, an upload from `UploadAsync` |

Local bytes are checked on your machine before any request: JPEG, PNG or WebP by their first bytes (a `mediaType` you give must match them), at most 10 MB, at most 40 megapixels read from the image header without decoding it. Every image needs an id of at most 64 characters (counted as Unicode code points, so 64 characters outside the Basic Multilingual Plane are taken), unique within the request. A request that breaks one of these rules throws `BadRequestError` with nothing sent. A request takes up to 16 images (URLs included); more is the API's `ValidationError`, a `too_long` entry on `["body", "images"]` listed with the request's other entries. No image is resized or re-encoded.

Then the library picks the fastest way to send them. When the request's local images total under the inline threshold (`DecisionNodeClient.DefaultInlineThreshold`, 512 KiB, set with `inlineThreshold`), they travel inline as base64. At or above it they are uploaded first: one `POST /v1/uploads` for all of them, one `PUT` each to storage in parallel, then the request names them by `upload_id`. The `PUT` carries exactly the headers the upload answer names and the bytes: never your key, never your own headers, and it never follows a redirect. A `PUT` refused because its URL's five minutes ran out is presigned again once. Each `PUT` attempt has its own timeout, `uploadTimeout` (60 seconds by default), apart from the API's `timeout`.

A request body may be at most 25 MiB (`DecisionNodeClient.MaxBodyBytes`, 26,214,400 bytes). Inline images and video frames count toward it; uploaded images and URLs do not, so a request whose large images are uploaded stays far under it. The library measures each body as it would be sent (each batch line on its own) and throws `RequestTooLargeError` before anything is sent or uploaded when it is over; the API's own `413` is the same class. `DecideRawAsync` sends its body as given.

Uploads are remembered by content in the client's memory (at most 256, least recently used out first) until 5 minutes before they expire, so an image you send again, in a later call, in `DecideManyAsync` or in a session frame, goes by its `upload_id` whatever its size. If the server answers that an upload the library placed has expired or was used up, the library uploads it again and sends the request once more. `UploadAsync(images)` uploads up to 16 images whatever their size and returns each `upload_id`, which any key of the workspace can name for an hour.

### Referring to images and videos

There are two ways to say which image a question is about:

1. **By id (recommended).** Give each image a short, meaningful id and use it in the instructions, in backticks: "Does the total on `receipt` match the amount in the claim?"
2. **By number.** Images are numbered from 1 in the order of `images`: "image 2", "the second image", "Picture 2" and "the last image" all work.

The model sees each image as `Picture 1 (receipt):`, `Picture 2 (claim_photo):`, so both ways point to the same image. Prefer ids: they stay right when images are added or reordered. Use names, not digits, as ids (`front`, not `2`). Videos work the same way, by id or by number ("video 1"), numbered separately in the order of `videos`. The `image` of a point or box question takes the id.

```csharp
var d = await dn.DecideAsync(
    state: "Expense claim 4411: 740.00 EUR.",
    questions: new Dictionary<string, Question> { ["matches"] = Truth("Does the total on `receipt` match the amount in the claim?") },
    images: new[]
    {
        Image.FromUrl("receipt", "https://files.example.com/claims/4411/receipt.jpg"),
        Image.FromUrl("claim_photo", "https://files.example.com/claims/4411/photo.jpg"),
    });
```

## Points and boxes

A `point` question finds one spot in an image and a `box` question draws a rectangle around something, each with the probability that the thing is there at all:

```csharp
var d = await dn.DecideAsync(
    state: "Line 3, camera 2.",
    questions: new Dictionary<string, Question>
    {
        ["pick"] = Point("Point to the yellow package closest to the robot arm."),
        ["damage"] = Box("Draw a box around the damaged package.", image: "belt"),
        ["any_damage"] = Truth("Is any package on the belt damaged?"),
    },
    images: new[] { Image.FromUrl("belt", "https://files.example.com/line-3/cam-2/0917.jpg") });

var pick = d.Point("pick");
Console.WriteLine($"{pick.X}, {pick.Y}");     // 812, 455 (pixels from the top-left corner)
Console.WriteLine(pick.Present);              // 0.97: the package is there
var damage = d.Box("damage");
Console.WriteLine($"{damage.X1}, {damage.Y1} to {damage.X2}, {damage.Y2}");
Console.WriteLine(d.Truth("any_damage").Truth);
```

| Field | Means |
|---|---|
| `X`, `Y` (point) | The spot, from the top-left corner of the image, `X` to the right and `Y` down |
| `X1`, `Y1`, `X2`, `Y2` (box) | The top-left and bottom-right corners |
| `Present` | The probability that what the instructions describe is in the image at all. Under a low value the place is only the best guess for something that is probably absent |
| `Confidence` | The probability that the place is right if the thing is there: for a point, that it lies on the thing; for a box, that it overlaps the true box by an intersection over union of 0.5 or more |
| `Image`, `Width`, `Height` | The image looked in and its size in pixels, so the answer can be drawn without another lookup |

Coordinates are whole pixels of the image as sent (after its EXIF orientation), or fractions of the width and height from 0 to 1 with `units: "normalized"`. Every number is as received. `image` names the image to look in by its id, a string, and is needed when the request carries two or more images; with one image it can be left out. A reference by number ("Picture 2") goes in the instructions.

Before anything is sent the library checks that the request carries an image, that `image` names one of the request's images (a video's id is not an image's), that `units` is `pixels` or `normalized`, and that the question has instructions. A fault throws the `ValidationError` the API would answer, with every fault of the request in its `Entries`, or the `BadRequestError` for missing instructions. Points and boxes go with `DecideAsync`, `DecideManyAsync` and batch lines.

## Videos

Ask about a video by sending the frames you sampled, each with its time in seconds. Only frames leave your machine, never a video file, a video URL or its sound. Every question sees every video, the images and the state, in order and in time.

Build a `Video` and its `VideoFrame`s from frames you already hold, such as the JPEGs a camera writes:

```csharp
var frames = Directory.GetFiles("dock/frames", "*.jpg")
    .Order()
    .Select((path, i) => VideoFrame.FromBytes(t: i * 0.5, File.ReadAllBytes(path)));

var d = await dn.DecideAsync(
    state: "Warehouse camera 4, loading dock, shift B.",
    questions: new Dictionary<string, Question>
    {
        ["fall"] = Truth("Does a person fall in the video?"),
        ["forklifts"] = Number("How many forklifts pass the door?", min: 0, max: 50),
    },
    videos: new[] { Video.Of("dock", frames, fps: 2) });

Console.WriteLine(d.Truth("fall").Truth);
Console.WriteLine(d.Usage.VideoFrames);  // the frames read
Console.WriteLine(d.Usage.VideoSeconds); // the seconds of video read
```

| Made with | Sent |
|---|---|
| `VideoFrame.FromBytes(t, data, mediaType)` | The bytes as base64. Without `mediaType` the type is detected from the first bytes (JPEG, PNG or WebP) |
| `VideoFrame.FromBase64(t, text, mediaType)` | The text exactly as given, with its media type |
| `Video.Of(id, frames, fps, start, end)` | `{id, fps, start, end, frames}`. `fps` caps the frames read a second (above 0, at most 4; the server reads up to 2 a second when it is left out), and `start` and `end` cut a window on the frames' own times. Each is left out when not given |
| `Video.FromJson(...)`, `VideoFrame.FromJson(...)` | The API's wire form, as given |

Frames always go inline: they are never uploaded, never cached and never resized or re-encoded, and they do not count toward the inline threshold for images. Up to 4 videos go with up to 16 images in one request (more is the API's `ValidationError`, a `too_long` entry on `["body", "videos"]`), and a video's id must differ from every image's id. Before anything is sent the library checks each video's id, its frames (at least one; each time a number of at least 0, larger than the one before it; JPEG, PNG or WebP), `fps`, `start` and `end`, and throws the `BadRequestError` the API would answer, with nothing sent. How many frames a request may carry, the frames' bytes and sizes, and the token budget are the server's to judge. When the server reads a video at a lower rate or resolution than asked, it says so in `Decision.Warnings`.

Videos go with `DecideAsync`, the helpers, `DecideManyAsync` and batch lines (`BatchRequest.Videos`). Sessions take no videos.

To turn a video file into frames, use the `dn` command line: `dn truth "Does a person fall?" --video dock.mp4` samples the file on your machine and sends only the frames.

## Boosters

A booster is a specialist model that reads the request's images (or, for `knowledge`, your documents) before the decision. Its reading is added to what the model sees as evidence and comes back in `Decision.Evidence`. The model still answers every question: a booster never answers one or adds one, and a request without boosters is answered exactly as before. Eight boosters are available:

| Booster | Reads | Tells the model |
|---|---|---|
| `age` | A picture of a person | How old the person is, with a range and a confidence (the largest face is read) |
| `ai_generated` | Any picture | How likely the picture was made by an image generator, which generator most likely, and whether the file carries Content Credentials (C2PA) and who signed them |
| `face_detection` (`Booster.FaceDetection`) | Any picture | How many faces it shows and where, a box and a confidence per face; a picture with no face reads 0 faces |
| `face_recognition` (`Booster.FaceRecognition`) | A picture of a person | Whether a face matches an item of the collection the request names, your workspace's own (`dn.Collections`) or `celebrities`: per match a box, a score and the item's reference, with its label or the person's name |
| `ocr` (`Booster.Ocr`) | Any picture | The text it shows, all of it by default, or what a `prompt` asks for: a formula in LaTeX, a table in HTML or Markdown, the fields of a JSON schema the prompt gives. Full support for English and Chinese; other languages need a prompt that names them |
| `bynn_depth` (Depth Vision, `Booster.BynnDepth`) | Any picture | The distance in meters to what is in the picture: the nearest obstacle straight ahead (the floor left out), seven directions from left to right, and a grid of up to 16 by 16 cells |
| `bynn_segment` (Segment Vision, `Booster.BynnSegment`) | Any picture, and up to 4 phrases naming what to find | Each match's outline, box and centre, and with depth on, its distance and size in meters |
| `knowledge` (`Booster.Knowledge(...)`) | The request's state and questions, never a picture | The passages of your own documents that apply, pinned rules first, each cited by document, chunk and byte offsets (see Knowledge) |

Depth Vision's and Segment Vision's distances and sizes are estimates from one picture, for decisions and planning, never a safety stop.

Name the boosters a request should use:

```csharp
var d = await dn.DecideAsync(
    state: "Age check for a new account.",
    questions: new Dictionary<string, Question>
    {
        ["adult"] = Truth("Is the person in the first picture an adult?"),
        ["synthetic"] = Truth("Is the second picture AI-generated?"),
    },
    images: new[]
    {
        Image.FromFile("a1b2", "selfie.jpg"),
        Image.FromUrl("c3d4", "https://files.example.com/c3d4.jpg"),
    },
    boosters: new Booster[] { "age", "ai_generated" });

Console.WriteLine(d.Truth("adult").Truth);

foreach (var (name, evidence) in d.Evidence!)
{
    Console.WriteLine($"{name}: {evidence.Status} in {evidence.LatencyMs} ms");
    foreach (var reading in evidence.Results)
    {
        switch (reading)
        {
            case AgeReading age when age.Faces > 0:
                Console.WriteLine($"  {age.Image}: {age.AgeYears} ({age.Low} to {age.High})");
                break;
            case AiGeneratedReading ai:
                Console.WriteLine($"  {ai.Image}: generated {ai.Generated}, signed by {ai.C2paSigner ?? "nobody"}");
                break;
        }
    }
}

Console.WriteLine(d.Usage.BoosterTokens); // the readings' share of the input tokens
Console.WriteLine(d.Usage.Boosters);      // the booster calls billed
```

A booster that needs a setting is built with `Booster.Of(name, images, require, options)`. Here the AI-generated check reads only the second picture, and the request fails if it cannot answer:

```csharp
boosters: new Booster[]
{
    "age",
    Booster.Of("ai_generated", images: new[] { "c3d4" }, require: true),
}
```

| `Booster.Of` argument | Sent |
|---|---|
| `name` | `name`, the booster's name |
| `images` | The ids of the request's images it reads; left out, it reads every image |
| `require` | `true`: a booster that fails or does not answer in time fails the request with an `OverloadedError` (`booster_unavailable`), which is retried like any 529. Left out, the decision runs without that reading and `Evidence` says why |
| `options` | The booster's settings, sent as given (below); `age` and `ai_generated` take none |

A name alone is sent as the name (`"age"`), and so is `Booster.Of("age")` with no other argument; `Booster.Of(...)` with a setting is sent as an object, `{"name": "ai_generated", "images": ["c3d4"], "require": true}`; `Booster.FromJson(...)` takes the API's wire form as given. Up to 4 boosters go with a request, each once, in the order given, and the `boosters` field is left out when there are none. Boosters read images in every form (URL, inline or uploaded), every question sees every reading, and they go with `DecideAsync`, the helpers, `DecideManyAsync`, `CheckRequest` and batch lines (`BatchRequest.Boosters`). Sessions take no boosters. Names are not a closed list, so a booster added to the API works with this version; the library checks nothing about boosters before sending, because which boosters a workspace may use is the server's to know.

The options each booster takes, sent as given; `age`, `ai_generated` and `face_detection` take none. Nothing is checked before sending, and the API answers a value out of range or an unknown key with a `BadRequestError` (`api_usage_error`):

| Booster | Option | Value |
|---|---|---|
| `face_recognition` | `collection` | Required: the id of one of your workspace's own face collections, or `celebrities` (a match gives the person's name). An unknown collection is a `NotFoundError` (`collection_not_found`) |
| `face_recognition` | `threshold` | The lowest score a match may have, 0 to 1; default 0.5 |
| `ocr` | `prompt` | What to read, a string of at most 2,000 characters. Left out, `"Text Recognition:"` (all visible text, plain); `"Formula Recognition:"` and `"Table Recognition:"` are the other built-in prompts and give the most predictable output; a custom instruction or a JSON schema is allowed |
| `bynn_depth` | `grid_size` | The grid's rows and columns, 2 to 16; default 8 |
| `bynn_segment` | `prompt` | Required: what to find, one phrase as a string, or an array of 1 to 4 phrases of at most 120 characters each |
| `bynn_segment` | `max_instances` | The most matches returned, 1 to 16; default 8 |
| `bynn_segment` | `min_score` | The lowest score a match may have, 0 to 1; default 0.25 |
| `bynn_segment` | `polygon_points` | The most points in an outline, 3 to 64; default 24 |
| `bynn_segment` | `depth` | `true` for each match's distance and size in meters, read in the same call; default `true` |

```csharp
var d = await dn.DecideAsync(
    state: "A drone lining up with a race gate.",
    questions: new Dictionary<string, Question>
    {
        ["clear"] = Truth("Is the way through the gate clear?"),
    },
    images: new[] { Image.FromFile("f001", "frame.jpg") },
    boosters: new Booster[]
    {
        Booster.Of(Booster.BynnDepth, options: new { grid_size = 8 }),
        Booster.Of(Booster.BynnSegment, options: new { prompt = new[] { "gate", "landing pad" }, max_instances = 4 }),
    });

foreach (var reading in d.Evidence![Booster.BynnDepth].Results)
{
    if (reading is DepthReading depth)
    {
        Console.WriteLine($"{depth.Image}: {depth.Ahead?.ToString() ?? "nothing"} m straight ahead");
    }
}
foreach (var reading in d.Evidence![Booster.BynnSegment].Results)
{
    if (reading is SegmentReading segment)
    {
        foreach (var match in segment.Instances)
        {
            Console.WriteLine($"{match.Label} {match.Score}: centre ({match.Centroid.X}, {match.Centroid.Y}), {match.Depth?.Median?.ToString() ?? "no distance"} m away");
        }
    }
}
```

`Decision.Evidence` holds one `Evidence` per booster the request named, by name in the request's order, and is `null` when it named none. Each has `Version` (for example `age-v3`), `Status` (`ok`, `timeout`, `error` or `skipped` when none of the request's images is one it reads), `LatencyMs`, `BilledCalls` (the calls of that booster billed on this request) and `Results`, one entry per picture; a picture the booster did not read (it timed out or failed, or could not decode the picture) is an `UnknownReading` holding only its `image` (and `readable: false`), the status saying why:

| Reading | Fields |
|---|---|
| `AgeReading` | `Image`, `Faces`, and `AgeYears`, `Low`, `High`, `Confidence` (each `null` when `Faces` is 0) |
| `AiGeneratedReading` | `Image`, `Generated` (the probability the picture was made by a generator), `Confidence`, `Method` (`inference` from the pixels, or `c2pa` from the file's Content Credentials), `TopGenerator` (`Name`, `Probability`; `null` unless the picture is likely generated), `C2paSigner` (`null` when the file has no Content Credentials), `C2pa` (`untrusted` when the file's Content Credentials validate but their signer is not trusted, else `null`) |
| `FaceDetectionReading` | `Image`, `Faces` (the count) and `Detections`, each a `FaceDetection` with `Box` (`X1`, `Y1`, `X2`, `Y2`, pixels, or fractions when every place question asks for normalized units) and `Confidence` |
| `FaceRecognitionReading` | `Image`, `Faces` (the faces searched), `CollectionName` (an own collection's name, else `null`) and `Matches`, by score, each a `FaceMatch` with `Box`, `Score`, `Collection`, `Reference`, `Name` (a celebrity's), `Label` (an own item's), `CollectionName` and `Metadata` (an own item's, else `null`) |
| `OcrReading` | `Image`, `Text` (the reading in the format the prompt asks for, empty when the picture shows no text) and `Truncated` (`true` when the reading was too long for the request, so the model read it cut at its end; `Text` stays whole) |
| `DepthReading` | `Image`, `Ahead` (meters to the nearest obstacle straight ahead, the floor left out; `null` when none is found), `Directions` (always 7, left to right, each meters or `null`), `Ground` (whether a floor was found and left out; when `false`, `Ahead` and `Directions` include everything), `GridRows`, `GridCols`, `Grid` (meters, row by row from the top left), `DepthMin`, `DepthMax`, `DepthMedian` (meters), `Width`, `Height` (pixels) |
| `SegmentReading` | `Image`, `Width`, `Height` (pixels), `Prompts` (the phrases as read), `Instances` (in the order received, empty when nothing was found), `DepthSource` (`bynn_depth`, or `null` with depth off), `AssumedHfovDeg` (the field of view the sizes assume, or `null`). Each `SegmentInstance` has `Label` (the phrase it matched), `Score`, `Box` (`X1`, `Y1`, `X2`, `Y2`), `Polygon` and `Centroid` (`SegmentPoint`s, `X` and `Y`), `Area` (its share of the picture), `Opening` (`null`, or the largest hole it encloses: `Polygon`, `Centroid`, `Width`, `Height`, `Offset` (its centroid minus the centre of the match's box, `[dx, dy]`), `Area`), `Depth` (`null`, or `Median`, `OpeningMedian`, `OpeningRimMedian` in meters, each `null` when none) and `SizeM` (`null`, or `Width`, `Height` and `Opening` in meters). Coordinates are fractions of the picture from 0 to 1, from its top left |
| `KnowledgeReading` | One per request, not per picture: the passages of a knowledge base and their citations (see Knowledge) |
| `UnknownReading` | A reading of a booster this version does not know, or one with a missing or wrongly typed field, with its `Raw` fields; the decision does not fail |

Every booster call billed is charged at that booster's price, listed on the [pricing page](https://decisionnode.com/docs/pricing#boosters), and drawn from your prepaid balance with the request's tokens; a booster that timed out or failed is free, and a refused request bills no booster call. The readings' tokens count as input tokens at the model's price.

| Error | When |
|---|---|
| `BadRequestError` `booster_unknown` | A name that is not a booster |
| `BadRequestError` `booster_input_mismatch` | A booster that reads images on a request without any, or an `images` entry that is not an image of the request |
| `BadRequestError` `api_usage_error` | A malformed `boosters` list: more than 4, a booster named twice, an unknown key in `options` or a value out of its range (the reason is in `Reason`) |
| `PermissionDeniedError` `booster_not_enabled` | The booster is turned off for your workspace |
| `OverloadedError` `booster_unavailable` | A booster with `require: true` failed or did not answer in time; retried |

Every error of a request that named boosters carries `Boosters`: each booster's `Status` and `LatencyMs` from the `x-decisionnode-boosters` response header, in the request's order, so you can see which required booster failed.

## Knowledge

Knowledge gives a decision your own documents: a claims policy, house rules, a mission's special instructions. You keep them in a knowledge base (a collection of type `text`), and the `knowledge` booster retrieves the passages that apply to a request and gives them to the model as facts and rules to apply, with a citation for each.

### Create a knowledge base and add documents

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Bynn.DecisionNode;

using var dn = new DecisionNodeClient();

// A scope key is optional: it names the one metadata key whose value scopes a document,
// here to one mission. It is fixed for the knowledge base's life.
var kb = await dn.Collections.CreateAsync("Claims policy", type: "text", scopeKey: "mission");
var col = (string)kb["id"]!;

// Markdown inline, pinned: a rule that must always apply is pinned.
await dn.Collections.AddDocumentAsync(col,
    Document.FromText("# Receipts\nReject any claim over 500 SEK without a receipt."),
    title: "Claims policy 2026", reference: "policy-2026", pinned: true);

// A PDF from a path, scoped to mission M-42, waiting up to 10 seconds for it to be ready.
var item = await dn.Collections.AddDocumentAsync(col, Document.FromFile("m42-brief.pdf"),
    metadata: new JsonObject { ["mission"] = "M-42" }, wait: true);

// Or wait on the client, for one document or for the whole knowledge base.
item = await dn.Collections.WaitUntilReadyAsync(col, (string)item["id"]!);
Console.WriteLine(item["status"]); // ready, or failed with its "error"
```

A document is text (`Document.FromText(text, format)`, Markdown by default or `Document.PlainText`), a file (`Document.FromFile(path)`, `Document.FromBytes(data, contentType, filename)` or `Document.FromStream(stream, contentType, filename)`), or an upload made before (`Document.FromUploadId(id)`, from `dn.UploadDocumentsAsync(documents)`). Text goes inline. A text or Markdown file under the inline threshold (512 KiB) that is UTF-8 goes inline too, titled by its file name without the extension unless you give a title. Every other file is uploaded first, with the upload's knowledge purpose, and the server converts it to text; once the text is ready the original file is deleted and only its text is kept. A file is read when the call is made; a stream is read to its end, into memory, before anything is sent.

| File | Content type | Limit |
|---|---|---|
| Text, Markdown | `text/plain`, `text/markdown` | 512 KiB of text |
| PDF | `application/pdf` | 50 MB, 100 pages |
| Word, PowerPoint, Excel | DOCX, PPTX, XLSX | 50 MB |
| HTML | `text/html` | 50 MB |
| Images | `image/png`, `image/jpeg`, `image/tiff`, `image/webp` | 50 MB |

Every file is at most 50 MB, and its text after extraction at most 512 KiB. The content type comes from `contentType`, else the file name's extension, else the first bytes (PDF and the image types); DOCX, PPTX, XLSX and text cannot be told from their bytes, so give bytes and streams of those a file name or a content type.

Ingestion is asynchronous: a document is `queued`, then `processing` (`stage` `extracting` for a converted file, `chunking`, `embedding`), then `ready` or `failed`. `wait: true` holds the call on the server up to 10 seconds (20 seconds an attempt unless you set a timeout). `WaitUntilReadyAsync(collectionId, itemId, timeout, pollInterval)` polls: the item until it is `ready` or `failed`, or without an item the knowledge base until nothing is queued or processing. It waits `pollInterval` (default 1 second), 1.5 times longer after each read up to 10 seconds, and throws `TimeoutException` once `timeout` (default 300 seconds, a wall-clock deadline from the call) has passed. A failed document is returned, not thrown: its `error` says why.

The other calls: `ReplaceDocumentAsync` (a new version, the old one serving until it is ready), `UpdateItemAsync` with `Pinned`, `PinRank` and `Title` beside `Reference`, `Label` and `Metadata`, `GetItemAsync(col, itemId, includeText: true)`, `ListItemsAsync`, `ListChunksAsync`, `DeleteItemAsync`, `DeleteFailedAsync` (every failed document at once, answering `{"deleted": n}`) and `DeleteAsync`. Collections and items are returned as the API sends them, as `JsonObject`s. Adding, replacing and updating carry an `Idempotency-Key`, the same on every attempt.

### Pins and scope

A pinned document is always given to a request that names its knowledge base, never subject to ranking: pin a rule that must always apply, ordered by `pinRank`. A document whose metadata holds the scope key's value (`{"mission": "M-42"}`) is read only by a request that names that value in `scope`; a document without it is global and read by every request.

### A decision with knowledge

```csharp
var d = await dn.DecideAsync(
    state: new { claim = new { amount = "740.00", receipt = false }, mission = "M-42" },
    questions: new Dictionary<string, Question> { ["approve"] = Question.Truth("Should the claim be approved under our policy?") },
    boosters: new[] { Booster.Knowledge(col, scope: "M-42", require: true) });

var reading = (KnowledgeReading)d.Evidence![Booster.KnowledgeName].Results[0];
foreach (var p in reading.Passages)
{
    Console.WriteLine($"{p.N}{(p.Pinned ? " (pinned)" : "")} {p.Title} > {p.Heading}: item {p.Item}, chunk {p.Chunk}, bytes {p.ByteStart} to {p.ByteEnd}");
}
```

`Booster.Knowledge(collection, query, scope, match, filter, topK, maxTokens, pinned, minScore, includeText, require)` sends each option only when given, as given; the API judges them. `query` replaces the query built from the questions and the state; `match` and `filter` select documents by metadata; `topK` (0 to 20, default 4; 0 reads the pins only) and `maxTokens` (`"auto"` or 128 to 8,192) bound the passages; `includeText` adds each passage's text (a full-scope key). With `require: true` a reading that cannot run over a ready knowledge base fails the request instead of deciding without it.

The reading is a `KnowledgeReading`: `Collection`, `CollectionName`, `Corpus`, `Query`, `Budget`, `Scope`, `Warnings` (`scope_not_named`, `over_budget`, `budget_floor`, `pinned_filtered`), `Passages` (pinned first), `Omitted`, `PendingItems`, `Search` and `Truncated`. Each `KnowledgePassage` has `N`, `Pinned`, `Item`, `Chunk`, `ByteStart`, `ByteEnd`, `Reference`, `Title`, `Heading`, `Tokens`, `Score` (`null` for a pin), `Lexical`, `Rendered` and `Text`. The byte offsets are UTF-8 byte offsets, end exclusive, into the document's stored text from `GetItemAsync(col, itemId, includeText: true)`. A C# string is UTF-16, so encode the text to UTF-8 before slicing it:

```csharp
var stored = await dn.Collections.GetItemAsync(col, p.Item, includeText: true);
var bytes = Encoding.UTF8.GetBytes((string)stored["text"]!);
var quote = Encoding.UTF8.GetString(bytes, (int)p.ByteStart, (int)(p.ByteEnd - p.ByteStart));
```

### Search

`SearchAsync(col, query, scope, match, filter, topK, pinned, maxTokens)` runs the retrieval without a decision, to try a query, and returns a `KnowledgeReading` with every passage's text. It needs a full-scope key.

```csharp
var found = await dn.Collections.SearchAsync(col, "receipts for small claims", scope: "M-42", topK: 3);
```

A knowledge call, a decision naming the booster or a search, costs USD 0.001, one a request whatever it retrieves; ingesting and storing documents are priced per token and per chunk, on the [pricing page](https://decisionnode.com/docs/pricing).

### Errors you may meet

| Error | When |
|---|---|
| `DecisionNodeError` 409 `knowledge_collection_empty` | `require: true` on a knowledge base with nothing ready and nothing pending; `Details` holds `items` and `failed` |
| `OverloadedError` `booster_unavailable` | `require: true` while no document is ready yet but some are being ingested; retried after its `Retry-After` (5 seconds) |
| `OverloadedError` with `Details["reason"]` `ingestion_delayed` | Ingestion has been held up for 15 minutes: raised at once, never retried, with a `RetryAfter` of 60 seconds, so you decide whether to wait |
| `RateLimitError` with `Details["reason"]` `ingest_queue_full` or `ingest_tokens_per_hour` | The workspace's ingestion rate; retried after its `Retry-After` |
| `RequestTooLargeError` `item_too_large` | A text over 512 KiB once normalised |
| `ValidationError` `invalid_text` | A text that is not UTF-8 or holds a NUL |
| `DecisionNodeError` 409 `reference_taken`, `pinned_budget_exceeded`, `chunks_limit_reached` | Another document holds the reference; the pinned documents are over their budget; the workspace's chunks are spent |
| `BadRequestError` `upload_purpose_mismatch` | An image upload named as a document, or a document upload named as an image |
| `PermissionDeniedError` `key_scope_denied` | A search, `includeText` or a document upload from a key that is not full-scope |

The library checks what it can before sending, as the API would answer it, with nothing sent: a missing or out-of-bounds title (1 to 200 characters), an empty text, a format other than `markdown` or `text`, a pin rank outside 0 to 1,000, a reference, label or metadata out of bounds and a scope key not of `^[a-z0-9_]{1,40}$` (`ValidationError`); a file that is empty, over 50 MB, or of a type that is not a document's (`BadRequestError` `api_usage_error`). A file whose type cannot be told from its name or its first bytes is an `ArgumentException`.

## Fine-tuned models

A fine-tuned model is your workspace's own version of a model, trained in the console on decisions you labelled. Call it by its name in `model`, as given: `decisionnode-flash@acme/fraud` means the version deployed most recently, and `decisionnode-flash@acme/fraud:3` pins version 3. The decision's `Model` always names the pinned version that answered. `ModelsAsync()` lists your workspace's fine-tuned models beside the catalogue's, with `FineTuned`, `Base` (the model it was trained from), `AdapterVersion` and `Status` (`deployed` answers decisions; `ready` is trained but not deployed). A name that does not exist, is not deployed or belongs to another workspace is the API's `BadRequestError` "Unknown model: <name>".

## Fallback

When `decisionnode-moe` has no worker ready to take a request at once, the API answers it with `decisionnode-flash` instead of making you wait. The decision says so: `Decision.Fallback` is `null` on an ordinary answer, and on a fallback answer holds `ServedModel` (the model that answered) and `RequestedModel` (the model you asked for). `Decision.Model` is always the model that answered.

```csharp
var d = await dn.DecideAsync("I was charged twice.", questions, model: "decisionnode-moe");
if (d.Fallback is { } f)
{
    Console.WriteLine($"{f.ServedModel} answered for {f.RequestedModel}");
}
```

Pass `fallback: false` (or `Fallback = false` on a `DecideRequest` or `BatchRequest`) when only the model you asked for may answer: such a request gets an `OverloadedError` instead, retried like any 529. Left out, the API's default applies and the field is not sent. `DecideAsync`, the helpers, `DecideManyAsync`, `CheckRequest` and batch lines take it.

## Sessions

A session holds a state and questions on the server and answers them again for every frame you send on its WebSocket: a drone's telemetry, a camera, a live call. The newest frame wins: a frame that arrives while another waits replaces it, so a fast feed gets the newest answer, never a backlog.

```csharp
await using var session = await dn.OpenSessionAsync(
    state: new { mission = "Survey the north field" },
    questions: new Dictionary<string, Question>
    {
        ["abort"] = Truth("Should the mission be aborted now?"),
        ["mode"] = Choice("Which flight mode now?", new[] { "continue", "hold", "return", "land" }),
    });
await session.ConnectAsync();
await session.SendAsync(new { battery = 0.31, wind_mps = 11.4 });

await foreach (var ev in session.RepliesAsync())
{
    switch (ev)
    {
        case Reply r: Console.WriteLine($"{r.Seq}: abort {r["abort"].AsTruth().Truth}"); break;
        case FrameError e: Console.WriteLine($"{e.Seq}: {e.ErrorType} {e.Message}"); break;
        case SessionClosed c: Console.WriteLine($"closed {c.Code}, reopen: {c.Reopen}"); break;
    }
}
```

| Member | Does |
|---|---|
| `ConnectAsync()` | Opens the WebSocket with your key in the `Authorization` header (never in the URL) and your `headers`. A refused handshake arrives as the first event, a `SessionClosed` |
| `SendAsync(frame, image, questions, reset, seq)` | Sends one frame and returns its `seq`, counted from 1 when not given. Never waits for a reply. A frame's image follows the inline threshold; frames take no URL. A frame that breaks a fixed rule (no frame value and no image, an empty frame, an empty or unknown question list, a `seq` out of range) throws `BadRequestError` and nothing is sent; it uses no `seq` |
| `RepliesAsync()`, `ReadAsync()` | `Reply` and `FrameError` values in order, ending with the `SessionClosed`. A frame error is a value, never thrown. A reply that superseded frames lists their `seq` in `Dropped` |
| `EndAsync()` | Ends the session (`DELETE`); one already ended counts as success |

The WebSocket connects through the client's `HttpClient`, so a proxy or certificate setting you configured on it applies to sessions too. The library answers the server's pings itself. `SessionClosed.Reopen` is true for the codes after which opening the session again is right (`1001`, `1006`, `1011`, `4404`, `4408`, `4409`, `4410`). A key that already holds its most open sessions gets `SessionLimitError`, a `RateLimitError` that is never retried.

`SuperviseAsync(body, feed, onReply)` runs all of that for a long-lived feed: it opens and connects, sends each reading of an `IAsyncEnumerable<SessionFrame>`, calls `onReply` as each reply arrives, reopens on the reopen codes (`4410` at once, the others after a capped backoff with jitter), connects again to the same session on `1013`, and stops on any other code. When the feed ends it waits up to 3 seconds for outstanding replies, ends the session and returns the last `SessionClosed`, or `null`.

## Batches

The Batch API answers many decisions for less than live calls. The batch price is each model's: `ModelsAsync()` states it as the model's `BatchPriceFactor` (DecisionNode MoE 0.7 and DecisionNode Flash 0.5 of the live price; none on a fine-tuned model, which takes no batches), and each line is billed at its own model's, its charge in the line's usage ([pricing](https://decisionnode.com/docs/pricing#batch-jobs)). A batch has no set finishing time, so poll it; requests still unanswered 24 hours after it is finalized expire, unbilled.

```csharp
var run = await dn.RunBatchAsync(tickets.Select((t, i) => new BatchRequest
{
    CustomId = "ticket-" + i,
    State = t,
    Questions = new Dictionary<string, Question> { ["refund"] = Truth("Is a refund asked for?") },
}));

await foreach (var r in run.Results)
{
    Console.WriteLine(r.Decision is { } d ? $"{r.CustomId}: {d.Truth("refund").Truth}" : $"{r.CustomId}: {r.Status?.ToString() ?? r.Outcome} {r.Error?.Message}");
}
```

`RunBatchAsync(requests, pollInterval, timeout)` creates an empty draft, adds the requests in calls of at most 10 MiB, finalizes the batch, polls it every `pollInterval` (default 60 seconds, at least 10; the server's `Retry-After` when it asks for longer) until it ends, and returns the ended `Batch` with its results. Past `timeout` (default 26 hours) it throws `TimeoutException`. If adding fails, the draft is cancelled and the error thrown.

`dn.Batches` has each call on its own: `CreateAsync(requests)`, `AddAsync(id, requests)`, `FinalizeAsync(id)`, `GetAsync(id)`, `ListAsync(offset, limit)`, `ResultsAsync(id)` and `CancelAsync(id)`. Requests go as JSON Lines, each with its `custom_id`, its model and its state; a request's images follow the inline threshold for that line alone. `ResultsAsync` reads the whole results file line by line as it arrives, never holding it whole; the client's `timeout` covers each wait for more of it. Each `BatchResult` holds a `Decision` for a `200` line, the `Error` its status and body give (a value, never thrown) for any other, or neither for a line that never ran (`Outcome` `expired` or `cancelled`).

Create, add and finalize change the batch, so they are retried only on `429` and `529`, which the API answers before applying anything, and never after a timeout or a lost answer. A finalize answered `409` is read back with `GetAsync`: when the batch is no longer a draft, an earlier finalize went through and the batch is returned.

## Keys

A provisioning server makes, lists, changes and revokes the Decisions-only keys of its devices with a Manage device keys key, made in the console and kept on the server; any other kind of key gets a `PermissionDeniedError` (`key_scope_denied`).

```csharp
var made = await dn.Keys.CreateAsync(new KeyCreate
{
    Name = "Kiosk 0412",
    DeviceId = "kiosk-0412",
    Labels = new Dictionary<string, string> { ["store"] = "118" },
    DailySpendCapUsd = 2m,
}, idempotencyKey: "provisioning-kiosk-0412");
var secret = (string)made["secret"]!; // shown once: hand it to the device, never log it

await dn.Keys.UpdateAsync((string)made["key"]!["id"]!, new KeyUpdate { ClearDailySpendCap = true });
await dn.Keys.RevokeByLabelsAsync(new Dictionary<string, string> { ["store"] = "118" }, dryRun: true);
```

`CreateAsync(key, idempotencyKey)` sends only the fields given and returns `{"key", "secret"}`; the secret is returned this once. It always sends an `Idempotency-Key`, yours or a new one kept across its attempts, so a call repeated after a lost answer returns the same key and makes no second one. `ListAsync(query, cursor)` reads one page, newest first (`Labels`, `DeviceId`, `Status`, `Limit` from 1 to 200), and `AllAsync(query)` every page. `UpdateAsync` (only the fields given; a `Clear` flag removes labels, limits, the spend cap or the expiry) and `RevokeAsync` take the key's id; `RevokeByLabelsAsync(labels, deviceIds, dryRun)` revokes every active key whose labels match, up to 10,000 a call, while `more` is `true`. A revocation takes effect at once and cannot be undone.

## Usage by key

What each key asked and cost over a period: requests, tokens, booster calls and the charge, as totals or by hour, day or month. Call it with a Full access or Manage device keys key, on your server; a Decisions-only or Publishable key gets a `PermissionDeniedError` (`key_scope_denied`).

```csharp
// One key: a device's month so far, or a period by the day.
var key = await dn.Usage.KeyAsync("key_3b8f0d6e2a914c7b9e5f1a2c4d6e8f01",
    start: "2026-10-01T00:00:00Z", end: "2026-10-03T00:00:00Z", granularity: "day");

// Every key of a customer, one row per key: the read behind a monthly invoice run.
var rows = await dn.Usage.AllAsync(new UsageQuery
{
    Start = "2026-10-01T00:00:00Z",
    End = "2026-11-01T00:00:00Z",
    Labels = new Dictionary<string, string> { ["customer"] = "acme" },
});
```

`KeyAsync(keyId, start, end, granularity, groupBy)` reads `GET /v1/keys/{key_id}/usage`; left out, the period is the UTC month so far. `ListAsync(query, cursor)` reads one page of `GET /v1/usage` (`period`, `as_of`, `items`, `next_cursor`), and `AllAsync(query)` follows `next_cursor` until it is `null` and returns every row, since a page can hold fewer keys than `Limit`. A `UsageQuery` takes `Start` and `End` (RFC 3339; a period is at most 400 days), `Granularity` (`none`, `hour`, `day` or `month`), `GroupBy` (`model` or `channel`), `Labels` (a key must match every one), `DeviceId`, `KeyIds` (up to 100), `Scope` (`full`, `decide` or `keys`), `Status` (`active`, `expired`, `revoked` or `all`), `IncludeEmpty` and `Limit` (1 to 1,000). Each call returns the API's object as sent, and a filter the API would refuse throws its `BadRequestError` before anything is sent.

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

Every error derives from `DecisionNodeError` (catch that name to catch them all), which carries `Status`, `ErrorType`, `RequestId`, `Boosters` (see Boosters), `Details` (the server's `detail.details` object when it sends one, else `null`) and `Reason`: the server's explanation in words from the `x-decisionnode-error` header, for example "question 'q': a score takes up to 10 levels, got 11". The exception's own `Message` is the server's message exactly (`detail.message`, or `detail` when it is a string; empty when the server sent neither), or the library's description when no response arrived.

| Status | Error | Extra |
|---|---|---|
| 400 | `BadRequestError`; `ContextLimitError` when the request is longer than the model takes | |
| 401, or no key configured | `AuthenticationError` | |
| 402 | `InsufficientCreditError` | |
| 403 | `RefusalError` for a refusal, else `PermissionDeniedError` (a booster turned off for the workspace, a suspended workspace) | `Category` (`harm` or `self_harm`), `Safety` |
| 404 | `NotFoundError` | |
| 413, or a body over 25 MiB found before sending | `RequestTooLargeError`, never retried | |
| 422 | `ValidationError` | `Entries` |
| 429 | `RateLimitError`; `SessionLimitError` for a session limit | `RetryAfter` |
| 529 | `OverloadedError`, `booster_unavailable` included | `RetryAfter` |
| other 5xx | `ServerError` | |
| 2xx with a body that is not JSON | `ServerError` | |
| 2xx where a helper's own answer is missing or malformed | `ServerError` | |
| any other status, 3xx and 410 included | `DecisionNodeError` itself, with `Status` | |
| a storage `PUT` that failed | `DecisionNodeError` itself, `ErrorType` `upload_failed`, the storage's error code in `Reason` | |
| no response | `ConnectionError`, `TimeoutError` | |

An error whose body is not JSON, such as an HTML page from a proxy or an empty body, is classified by its status alone: `ErrorType` is `null` (so a 400 is never a `ContextLimitError` and a 403 never a `RefusalError`), `RequestId` and `Reason` come from their headers, and the `Message` is the library's own description, `HTTP <status> <text>` with ` (request id <id>)` when there is one, for example `HTTP 413 Content Too Large` or `HTTP 408 Request Timeout (request id req_edge_0408)`. The page itself is never read into the error. Retries follow the status as for any other error: a 408 is retried, a 413 never.

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

The library checks every fixed rule the API documents before sending, so a request the server would refuse costs no round trip. A fault throws exactly what the server would answer, with no `Status` and no `RequestId` (`Reason` says what went wrong):

- `BadRequestError` with the API's message and no `ErrorType` for a choice without options or with more than 255, a score with more than 10 levels, a truth question with neither instructions nor criteria, and a number question without instructions.
- `ValidationError` with the server's exact `Entries` for a question without a type or with fields of the wrong type, a state that is not a string, an object or an array, a number grid the API cannot build (`min` above `max`, a `step` not above 0, more than 6 decimals, beyond 2^53, more than 4,096 values), and a session `window` outside 1 to 64 or `ttl_seconds` outside 1 to 3,600.
- `BadRequestError` with `ErrorType` `api_usage_error` for an unknown question type, a request nested more than 128 levels deep, an image fault (see Images), a frame fault (see Sessions), a batch request without a unique `custom_id` or an empty add, and `ListAsync` paging out of range; `image_url_refused` for an image URL with user information, a port other than 443, or a literal private address.

Limits only `/v1/models` states, such as whether a model answers number questions or serves sessions, its session window, `requests_per_batch` and `max_custom_id_chars`, are checked once you have called `ModelsAsync()` on the client, and left to the server otherwise. A model is found in that list by its name, its alias (the family id, `decisionnode-moe`) or its pinned version. Token limits stay the server's, because they differ by model.

To run the same checks without sending, for example to validate input as it is entered, call `CheckRequest(request)` (a `DecideRequest` or `SessionOptions`), `Batches.CheckCreate(requests)`, `Batches.CheckAdd(requests)` or `session.CheckFrame(...)`. Each throws exactly what the call would throw and returns when the call would be sent; nothing is sent and nothing uploaded.

```csharp
try
{
    dn.CheckRequest(new DecideRequest { State = form, Questions = questions, Images = images });
}
catch (ValidationError e)
{
    foreach (var entry in e.Entries!) Console.WriteLine(entry!["msg"]);
}
```

A bad argument throws .NET's own `ArgumentException` (or its subclasses `ArgumentNullException` and `ArgumentOutOfRangeException`), never a DecisionNode error: an empty question map, a value that cannot be serialised, an unreadable image file, a base URL that is not `https`, a `timeout` that is not above zero, a negative `maxRetries` or `inlineThreshold`, a `concurrency` below 1, a `pollInterval` below 10 seconds, a `WaitUntilReadyAsync` `pollInterval` below 0.1 seconds or `timeout` not above zero, an `HttpClient` that cannot be shown to refuse redirects (including one with a `DelegatingHandler` you have not declared safe), and an `HttpClient` that replaces the certificate check for an `https` base URL. A missing key is an `AuthenticationError`, also before anything is sent.

## Timeouts and retries

Each attempt has 10 seconds, from sending the request to the last byte of the response body. Failed attempts are retried twice by default on 408, 429, every 5xx (529 included), timeouts and connection errors. The wait is the server's `retry-after-ms` when sent, else `Retry-After` in seconds or as an HTTP date, capped at 60 seconds; without either, exponential backoff from 0.5 seconds with jitter, capped at 8 seconds. 400, 401, 402, 403, 404 and 422 are never retried. A decision changes nothing on the server, so a retry is always safe.

One exception: a request naming the `ocr` booster (by its name or as `Booster.Of(Booster.Ocr, ...)`) takes longer, so it has 30 seconds an attempt when you set no `timeout`; a `timeout` you set is used as given. Such a request is never retried after a timeout, because the server finishes the timed-out attempt and may bill it: the call throws `TimeoutError` instead. Its other retries (408, 429, 5xx, connection errors) stay. Give requests that use `ocr` a timeout of at least 30 seconds. This covers `DecideAsync`, the helpers and each request of `DecideManyAsync`, not sessions, batches or `DecideRawAsync`.

Only a 2xx status is a success. Redirects are never followed: a 3xx is a `DecisionNodeError` with its status, so your key is never sent to another URL or over plain HTTP. The same holds for an `HttpClient` you pass in (see Configuration).

Your `CancellationToken` cancels the whole call, retries included, and surfaces as `OperationCanceledException`, not `TimeoutError`.

## Configuration

| Setting | Argument | Environment variable | Default |
|---|---|---|---|
| API key | `apiKey` | `DECISIONNODE_API_KEY` | none |
| Base URL | `baseUrl` | `DECISIONNODE_BASE_URL` | `https://api.decisionnode.com` |
| Model | `model` | `DECISIONNODE_MODEL` | `decisionnode-moe` |
| Timeout per attempt | `timeout` (above zero) | | 10 seconds (30 for a decision naming `ocr`, 20 for a document call with `wait`) |
| Retries | `maxRetries` (0 or more) | | 2 |
| Inline threshold | `inlineThreshold` (0 or more) | | 524,288 bytes |
| Timeout per upload attempt | `uploadTimeout` (above zero) | | 60 seconds for an image, 120 for a document |
| Extra headers | `headers` | | none |
| HTTP client | `httpClient` | | one the client creates and owns |

An argument beats the environment, which beats the default. The base URL must be `https`; plain `http` is accepted only for `localhost`, `127.0.0.1` and `::1` (a local or self-hosted server), and anything else throws `ArgumentException` when the client is made, so a key never travels unencrypted. An empty string counts as not given, for the client's arguments and for a call's `model`. The model ids are the families `decisionnode-moe` (the default) and `decisionnode-flash` (faster and lower cost), each always the current release. A pinned id names one release, `decisionnode-<family>-<version>`: `ModelsAsync()` lists the pinned ids your key may use, and every decision's `Model` is the pinned id that answered. `DecisionNodeClientOptions` holds the same settings, plus `DebugLog`, which receives one line per attempt (method, path, status, request id and timing; never the state, the questions or the key).

`headers` are added to every API request and to a session's WebSocket handshake, never to a storage upload; `x-decisionnode-safety-body: true` is the one the API reads (see Safety).

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

The library sends no telemetry and no request other than to the base URL, the stream URL of a session you open, and the storage URLs the upload answer names. Beyond your key and the standard `Accept` and `Content-Type`, the only header it adds is `User-Agent: decisionnode-dotnet/<version>`.

## Versioning

This library implements version 1.4 of the DecisionNode SDK specification (`DecisionNodeClient.SpecVersion`), the same surface every official DecisionNode SDK implements, checked by a shared conformance suite. See [CHANGELOG.md](CHANGELOG.md).

## Development

```sh
dotnet test tests/Bynn.DecisionNode.Tests
```

```sh
DECISIONNODE_CONFORMANCE_DIR=/path/to/decision-node-conformance dotnet test tests/Bynn.DecisionNode.Conformance
```

The conformance tests start the DecisionNode conformance suite's mock server and run every case of its five kinds (requests, responses, errors, behaviour, constants) against it. Error cases that name `catch_as` are caught through a `catch (DecisionNodeError)` clause. They need Python 3.10 or later and `DECISIONNODE_CONFORMANCE_DIR` set to a checkout of the suite (version 1.25); without it every conformance test fails, none is skipped. CI checks the suite out at the commit pinned in `DECISIONNODE_CONFORMANCE_REF` in `.github/workflows/ci.yml`, with `persist-credentials: false`, and runs it on every push. Every action in the workflow is pinned to a commit SHA.

## License

MIT. See [LICENSE](LICENSE).
