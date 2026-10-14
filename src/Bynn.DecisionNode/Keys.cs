using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// The workspace's Decisions-only keys, for devices: make, list, change and revoke them from a
/// provisioning server, with a Manage device keys key made in the console. Every call returns
/// the API's object as sent. <see cref="CreateAsync"/> always sends an <c>Idempotency-Key</c>,
/// the same on every attempt, so a retried call makes one key; the other calls retry as any call.
/// A key's secret is returned once, by <see cref="CreateAsync"/>: keep it out of logs.
/// </summary>
public sealed class KeysClient
{
    private readonly DecisionNodeClient _client;

    internal KeysClient(DecisionNodeClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Makes a Decisions-only key (<c>POST /v1/keys</c>), sending only the fields given, and
    /// returns <c>{"key", "secret"}</c> as sent. The secret is shown this once.
    /// </summary>
    /// <param name="key">The new key's name and settings.</param>
    /// <param name="idempotencyKey">
    /// Your own <c>Idempotency-Key</c>, 1 to 255 printable ASCII characters, so a call repeated
    /// after a lost answer returns the same key; left out, a new UUID is sent, the same on every
    /// attempt of this call.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="BadRequestError">An <paramref name="idempotencyKey"/> the API would refuse; nothing is sent.</exception>
    public async Task<JsonObject> CreateAsync(KeyCreate key, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        // The fixed rules, in the body's order; the first refused is the API's 422.
        var refusal = NameRefusal(key.Name)
            ?? (key.DeviceId is { } device && !DeviceOk(device) ? Refusal("device_id", DeviceRule, device) : null)
            ?? (key.Labels is { } labels ? LabelsRefusal(labels) : null)
            ?? (key.DailySpendCapUsd is { } usd ? CapRefusal(usd) : null);
        if (refusal is { } r)
        {
            throw LocalChecks.KeyRefusal((JsonArray)r.Loc, r.Message, r.Input);
        }
        if (idempotencyKey is not null && (idempotencyKey.Length is < 1 or > 255 || idempotencyKey.Any(c => c is < (char)0x20 or > (char)0x7E)))
        {
            throw LocalChecks.BadRequest("Idempotency-Key must be sent once, as 1 to 255 printable ASCII characters.", "api_usage_error", null);
        }
        var body = new JsonObject { ["name"] = key.Name };
        if (key.DeviceId is not null)
        {
            body["device_id"] = key.DeviceId;
        }
        if (key.Labels is not null)
        {
            body["labels"] = Labels(key.Labels);
        }
        if (key.Limits is not null)
        {
            body["limits"] = key.Limits.ToJson();
        }
        if (key.DailySpendCapUsd is { } cap)
        {
            body["daily_spend_cap"] = new JsonObject { ["usd"] = cap };
        }
        if (key.ExpiresAt is not null)
        {
            body["expires_at"] = key.ExpiresAt;
        }
        if (key.MaySearchCollections is { } search)
        {
            body["may_search_collections"] = search;
        }
        var plan = new DecisionNodeClient.AttemptPlan(null, true, idempotencyKey ?? Guid.NewGuid().ToString("D"));
        return await SendAsync(HttpMethod.Post, "/v1/keys", body, DecisionNodeClient.RetryPolicy.Idempotent, cancellationToken, plan).ConfigureAwait(false);
    }

    /// <summary>
    /// One page of the Decisions-only keys, newest first (<c>GET /v1/keys</c>):
    /// <c>{"items", "next_cursor"}</c> as sent. Pass a page's <c>next_cursor</c> as
    /// <paramref name="cursor"/> to read the next.
    /// </summary>
    /// <exception cref="BadRequestError">A filter the API would refuse; nothing is sent.</exception>
    public Task<JsonObject> ListAsync(KeyListQuery? query = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var parameters = ListQuery(query ?? new KeyListQuery());
        if (cursor is not null)
        {
            parameters.Add(("cursor", cursor));
        }
        return SendAsync(HttpMethod.Get, "/v1/keys" + Query(parameters), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);
    }

    /// <summary>Every key that matches: <see cref="ListAsync"/>'s pages, <c>next_cursor</c> followed until it is <c>null</c>.</summary>
    /// <exception cref="BadRequestError">A filter the API would refuse; nothing is sent.</exception>
    public async Task<IReadOnlyList<JsonObject>> AllAsync(KeyListQuery? query = null, CancellationToken cancellationToken = default)
    {
        var parameters = ListQuery(query ?? new KeyListQuery());
        var items = new List<JsonObject>();
        string? cursor = null;
        while (true)
        {
            var path = "/v1/keys" + Query(cursor is null ? parameters : parameters.Append(("cursor", cursor)).ToList());
            var page = await SendAsync(HttpMethod.Get, path, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
            if (page["items"] is JsonArray list)
            {
                items.AddRange(list.OfType<JsonObject>());
            }
            cursor = JsonValues.AsString(page["next_cursor"]);
            if (string.IsNullOrEmpty(cursor))
            {
                return items;
            }
        }
    }

    /// <summary>
    /// Changes a key (<c>PATCH /v1/keys/{keyId}</c>), sending only the fields
    /// <paramref name="update"/> gives, and returns the key as sent. A key's <c>device_id</c>
    /// never changes.
    /// </summary>
    /// <exception cref="ArgumentException">A field both given and cleared.</exception>
    public Task<JsonObject> UpdateAsync(string keyId, KeyUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var refusal = (update.Name is { } name ? NameRefusal(name) : null)
            ?? (update.Labels is { } labels ? LabelsRefusal(labels) : null)
            ?? (update.DailySpendCapUsd is { } usd ? CapRefusal(usd) : null);
        if (refusal is { } r)
        {
            throw LocalChecks.KeyRefusal((JsonArray)r.Loc, r.Message, r.Input);
        }
        var body = new JsonObject();
        if (update.Name is not null)
        {
            body["name"] = update.Name;
        }
        Set(body, "labels", update.Labels is null ? null : Labels(update.Labels), update.ClearLabels, nameof(KeyUpdate.ClearLabels));
        Set(body, "limits", update.Limits?.ToJson(), update.ClearLimits, nameof(KeyUpdate.ClearLimits));
        Set(body, "daily_spend_cap", update.DailySpendCapUsd is { } cap ? new JsonObject { ["usd"] = cap } : null, update.ClearDailySpendCap, nameof(KeyUpdate.ClearDailySpendCap));
        Set(body, "expires_at", update.ExpiresAt is null ? null : JsonValue.Create(update.ExpiresAt), update.ClearExpiresAt, nameof(KeyUpdate.ClearExpiresAt));
        if (update.MaySearchCollections is { } search)
        {
            body["may_search_collections"] = search;
        }
        if (body.Count == 0)
        {
            throw LocalChecks.BadRequest("Nothing to change.", "api_usage_error", null);
        }
        return SendAsync(HttpMethod.Patch, Path(keyId), body, DecisionNodeClient.RetryPolicy.Default, cancellationToken);
    }

    /// <summary>Revokes one key at once (<c>DELETE /v1/keys/{keyId}</c>): <c>{"id", "revoked": true}</c> as sent. A revocation cannot be undone.</summary>
    public Task<JsonObject> RevokeAsync(string keyId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Path(keyId), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    /// <summary>
    /// Revokes every active key whose labels match all of <paramref name="labels"/>
    /// (<c>POST /v1/keys/revoke</c>), at most 10,000 a call: <c>{"revoked", "more"}</c> as sent;
    /// call again while <c>more</c> is <c>true</c>. With <paramref name="dryRun"/> it revokes
    /// nothing and answers <c>{"would_revoke", "more"}</c>.
    /// </summary>
    /// <param name="labels">The labels every key must have.</param>
    /// <param name="deviceIds">Only keys of these devices, at most 1,000; sent only when given.</param>
    /// <param name="dryRun">Count the keys instead of revoking them; sent only when given.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<JsonObject> RevokeByLabelsAsync(IReadOnlyDictionary<string, string> labels, IReadOnlyList<string>? deviceIds = null, bool? dryRun = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var refusal = LabelsRefusal(labels)
            ?? (labels.Count == 0 ? Refusal("labels", "A bulk revoke names at least one label: every key it revokes has them all.", null) : null)
            ?? (deviceIds is { Count: > MaxRevokeDevices } ? Refusal("device_ids", "device_ids is a list of at most 1,000 device ids.", null) : null)
            ?? (deviceIds?.FirstOrDefault(d => !DeviceOk(d)) is { } bad ? Refusal("device_id", DeviceRule, bad) : null);
        if (refusal is { } r)
        {
            throw LocalChecks.KeyRefusal((JsonArray)r.Loc, r.Message, r.Input);
        }
        var body = new JsonObject { ["labels"] = Labels(labels) };
        if (deviceIds is not null)
        {
            body["device_ids"] = new JsonArray(deviceIds.Select(d => (JsonNode?)d).ToArray());
        }
        if (dryRun is { } dry)
        {
            body["dry_run"] = dry;
        }
        return SendAsync(HttpMethod.Post, "/v1/keys/revoke", body, DecisionNodeClient.RetryPolicy.Default, cancellationToken);
    }

    private static readonly Regex LabelName = new(@"\A[a-z0-9_.-]{1,32}\z", RegexOptions.CultureInvariant);
    private const int MaxLabels = 16, MaxRevokeDevices = 1000, MaxNameChars = 48;
    private const string DeviceRule = "device_id is 1 to 128 printable ASCII characters, not starting with a space.";

    private static (JsonNode Loc, string Message, JsonNode? Input)? Refusal(string field, string message, JsonNode? input) =>
        (new JsonArray(field), message, input);

    /// <summary>A key's name, trimmed, 1 to 48 characters; its character set is the server's.</summary>
    private static (JsonNode Loc, string Message, JsonNode? Input)? NameRefusal(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return (new JsonArray("name"), "Give the key a name.", name);
        }
        return trimmed.EnumerateRunes().Count() > MaxNameChars ? (new JsonArray("name"), "Key names are at most 48 characters.", name) : null;
    }

    private static bool DeviceOk(string? device) =>
        device is not null && device.Length is >= 1 and <= 128 && device.All(c => c is >= (char)0x20 and <= (char)0x7E) && !device.StartsWith(' ');

    /// <summary>A key's labels: at most 16, each name 1 to 32 of a-z, 0-9, _ . and -, each value 1 to 64 characters with no control character.</summary>
    private static (JsonNode Loc, string Message, JsonNode? Input)? LabelsRefusal(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count > MaxLabels)
        {
            return (new JsonArray("labels"), "A key has at most 16 labels.", null);
        }
        foreach (var (name, value) in labels)
        {
            if (!LabelName.IsMatch(name))
            {
                return (new JsonArray("labels"), "A label's name is 1 to 32 of a-z, 0-9, _ . and -.", null);
            }
            var length = value?.EnumerateRunes().Count() ?? 0;
            if (value is null || length is < 1 or > 64 || value.EnumerateRunes().Any(r => System.Text.Rune.GetUnicodeCategory(r) == UnicodeCategory.Control))
            {
                return (new JsonArray("labels", name), "A label's value is 1 to 64 characters of text.", null);
            }
        }
        return null;
    }

    /// <summary>A daily spend cap: 0.01 to 1,000 USD, in whole cents.</summary>
    private static (JsonNode Loc, string Message, JsonNode? Input)? CapRefusal(decimal usd)
    {
        var cents = usd * 100;
        return cents != decimal.Round(cents) || cents < 1 || usd > 1000
            ? (new JsonArray("daily_spend_cap"), "daily_spend_cap.usd is from 0.01 to 1000, in cents, or null.", null)
            : null;
    }
    private static readonly string[] Statuses = { "active", "expired", "revoked", "all" };
    private const int MaxLimit = 200;

    private static List<(string Name, string Value)> ListQuery(KeyListQuery query)
    {
        var q = new List<(string, string)>();
        // Labels by name, so a map without order is sent the same every time.
        foreach (var (name, value) in (query.Labels ?? new Dictionary<string, string>()).OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            var length = value?.EnumerateRunes().Count() ?? 0;
            if (!LabelName.IsMatch(name) || value is null || length is < 1 or > 64 || value.EnumerateRunes().Any(r => System.Text.Rune.GetUnicodeCategory(r) == UnicodeCategory.Control))
            {
                throw Refused("A label filter is name:value, as labels are written.");
            }
            q.Add(("label", name + ":" + value));
        }
        if (query.DeviceId is { } device)
        {
            if (device.Length is < 1 or > 128 || device.Any(c => c is < (char)0x20 or > (char)0x7E) || device.StartsWith(' '))
            {
                throw Refused("device_id is 1 to 128 printable ASCII characters, not starting with a space.");
            }
            q.Add(("device_id", device));
        }
        if (query.Status is { } status)
        {
            if (!Statuses.Contains(status))
            {
                throw Refused("status is active, expired, revoked or all.");
            }
            q.Add(("status", status));
        }
        if (query.Limit is { } limit)
        {
            if (limit is < 1 or > MaxLimit)
            {
                throw Refused("limit is a whole number from 1 to 200.");
            }
            q.Add(("limit", limit.ToString(CultureInfo.InvariantCulture)));
        }
        return q;
    }

    private static void Set(JsonObject body, string field, JsonNode? value, bool clear, string clearName)
    {
        if (value is not null && clear)
        {
            throw new ArgumentException($"give {field} or {clearName}, not both");
        }
        if (value is not null)
        {
            body[field] = value;
        }
        else if (clear)
        {
            body[field] = null;
        }
    }

    private static JsonObject Labels(IReadOnlyDictionary<string, string> labels) =>
        new(labels.Select(l => new KeyValuePair<string, JsonNode?>(l.Key, l.Value)));

    private static BadRequestError Refused(string message) => LocalChecks.BadRequest(message, "api_usage_error", null);

    private static string Query(IReadOnlyList<(string Name, string Value)> parameters) =>
        parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value)));

    private static string Path(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        return "/v1/keys/" + Uri.EscapeDataString(keyId);
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, DecisionNodeClient.RetryPolicy policy, CancellationToken ct, DecisionNodeClient.AttemptPlan? plan = null)
    {
        var (parsed, result) = await _client.SendJsonAsync(
            method,
            path,
            body is null ? null : JsonValues.ToUtf8(body),
            body is null ? null : DecisionNodeClient.JsonContentType,
            policy,
            ct,
            plan).ConfigureAwait(false);
        return parsed as JsonObject ?? throw new ServerError(new ErrorDetails
        {
            Status = result.Status,
            RequestId = result.Header("x-request-id"),
            Message = $"the response of {method.Method} {path.Split('?')[0]} is not a JSON object",
        });
    }
}

/// <summary>A device key's own rate limits, a second; a value left <c>null</c> is not sent.</summary>
/// <param name="RequestsPerSecond">Requests a second, from 1 to the workspace's own.</param>
/// <param name="TokensPerSecond">Input tokens a second, from 1 to the workspace's own.</param>
public sealed record KeyLimits(int? RequestsPerSecond = null, int? TokensPerSecond = null)
{
    internal JsonObject ToJson()
    {
        var o = new JsonObject();
        if (RequestsPerSecond is { } r)
        {
            o["requests_per_second"] = r;
        }
        if (TokensPerSecond is { } t)
        {
            o["tokens_per_second"] = t;
        }
        return o;
    }
}

/// <summary>A new Decisions-only key for <see cref="KeysClient.CreateAsync"/>; a field left <c>null</c> is not sent.</summary>
public sealed class KeyCreate
{
    /// <summary>1 to 48 letters, digits, spaces, dots, dashes and underscores, unique among the workspace's active keys.</summary>
    public required string Name { get; init; }

    /// <summary>Your id for the device, 1 to 128 printable ASCII characters, not starting with a space; it never changes.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Up to 16 names (1 to 32 of a-z, 0-9 and _ . -) and values (1 to 64 characters).</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>The key's own rate limits; left out, the workspace's apply.</summary>
    public KeyLimits? Limits { get; init; }

    /// <summary>The most the key may spend in a day, in USD, to two decimals and at most 1,000; resets at 00:00 UTC.</summary>
    public decimal? DailySpendCapUsd { get; init; }

    /// <summary>When the key stops working, RFC 3339, at least a minute and at most 10 years ahead; left out, never.</summary>
    public string? ExpiresAt { get; init; }

    /// <summary>Whether the key may run <c>face_recognition</c>; the API's default is <c>false</c>.</summary>
    public bool? MaySearchCollections { get; init; }
}

/// <summary>The filters of <see cref="KeysClient.ListAsync"/> and <see cref="KeysClient.AllAsync"/>; a filter left <c>null</c> is not sent.</summary>
public sealed class KeyListQuery
{
    /// <summary>The keys whose labels hold every one of these; sent as <c>label=name:value</c>, sorted by name.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>The key of one device.</summary>
    public string? DeviceId { get; init; }

    /// <summary><c>active</c> (the API's default), <c>expired</c>, <c>revoked</c> or <c>all</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Keys a page, 1 to 200; the API's default is 50.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// What <see cref="KeysClient.UpdateAsync"/> changes. A field left <c>null</c> stays as it is;
/// a <c>Clear</c> flag sends the field as <c>null</c>, which removes it. <see cref="Labels"/>
/// replaces the whole map.
/// </summary>
public sealed class KeyUpdate
{
    /// <summary>A new name.</summary>
    public string? Name { get; init; }

    /// <summary>The key's whole new labels.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Removes the key's labels.</summary>
    public bool ClearLabels { get; init; }

    /// <summary>New rate limits.</summary>
    public KeyLimits? Limits { get; init; }

    /// <summary>Removes the key's own limits: the workspace's apply.</summary>
    public bool ClearLimits { get; init; }

    /// <summary>A new daily spend cap, in USD.</summary>
    public decimal? DailySpendCapUsd { get; init; }

    /// <summary>Removes the daily spend cap.</summary>
    public bool ClearDailySpendCap { get; init; }

    /// <summary>A new expiry, RFC 3339.</summary>
    public string? ExpiresAt { get; init; }

    /// <summary>Removes the expiry: the key never expires.</summary>
    public bool ClearExpiresAt { get; init; }

    /// <summary>Whether the key may run <c>face_recognition</c>.</summary>
    public bool? MaySearchCollections { get; init; }
}
