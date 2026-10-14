using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// Per-key usage: what each of the workspace's keys asked and cost over a period. Every call
/// returns the API's object as sent and is retried as any read. The query rules the API checks
/// as it reads the query are checked before anything is sent; the period's rules and the cursor
/// are the API's.
/// </summary>
public sealed class UsageClient
{
    private readonly DecisionNodeClient _client;

    internal UsageClient(DecisionNodeClient client)
    {
        _client = client;
    }

    /// <summary>
    /// One key's usage (<c>GET /v1/keys/{keyId}/usage</c>): its totals and, with a
    /// <paramref name="granularity"/>, its buckets. Left out, the period is the UTC month so far.
    /// </summary>
    /// <param name="keyId">The key's id, <c>key_</c> and 32 hex digits.</param>
    /// <param name="start">The period's start, an ISO 8601 time; sent only when given.</param>
    /// <param name="end">The period's end, an ISO 8601 time; sent only when given.</param>
    /// <param name="granularity"><c>none</c>, <c>hour</c>, <c>day</c> or <c>month</c>; sent only when given.</param>
    /// <param name="groupBy"><c>model</c> or <c>channel</c>; sent only when given.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="BadRequestError">A granularity or group the API does not take; nothing is sent.</exception>
    public Task<JsonObject> KeyAsync(string keyId, string? start = null, string? end = null, string? granularity = null, string? groupBy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        var query = Period(start, end, granularity, groupBy);
        return GetAsync("/v1/keys/" + Uri.EscapeDataString(keyId) + "/usage", query, cancellationToken);
    }

    /// <summary>
    /// One page of every key's usage (<c>GET /v1/usage</c>): <c>period</c>, <c>as_of</c>,
    /// <c>items</c> (one per key) and <c>next_cursor</c>, <c>null</c> on the last page. Pass a
    /// page's <c>next_cursor</c> as <paramref name="cursor"/> to read the next.
    /// </summary>
    /// <exception cref="BadRequestError">A filter the API does not take; nothing is sent.</exception>
    public Task<JsonObject> ListAsync(UsageQuery query, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var parameters = ListQuery(query);
        if (cursor is not null)
        {
            parameters.Add(("cursor", cursor));
        }
        return GetAsync("/v1/usage", parameters, cancellationToken);
    }

    /// <summary>
    /// Every key's usage over the period: <see cref="ListAsync"/>'s pages, <c>next_cursor</c>
    /// followed until it is <c>null</c>, every page's <c>items</c> in order. A page can hold fewer
    /// keys than <see cref="UsageQuery.Limit"/>, so a short page is not the last.
    /// </summary>
    /// <exception cref="BadRequestError">A filter the API does not take; nothing is sent.</exception>
    public async Task<IReadOnlyList<JsonObject>> AllAsync(UsageQuery query, CancellationToken cancellationToken = default)
    {
        var parameters = ListQuery(query);
        var items = new List<JsonObject>();
        string? cursor = null;
        while (true)
        {
            var page = await GetAsync("/v1/usage", cursor is null ? parameters : parameters.Append(("cursor", cursor)).ToList(), cancellationToken).ConfigureAwait(false);
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

    private static readonly string[] Granularities = { "none", "hour", "day", "month" };
    private static readonly string[] Groups = { "model", "channel" };
    private static readonly string[] Scopes = { "full", "decide", "keys" };
    private static readonly string[] Statuses = { "active", "expired", "revoked", "all" };
    private const int MaxLimit = 1000, MaxKeyIds = 100;
    private static readonly Regex KeyId = new(@"\Akey_[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex LabelName = new(@"\A[a-z0-9_.-]{1,32}\z", RegexOptions.CultureInvariant);

    private static List<(string Name, string Value)> Period(string? start, string? end, string? granularity, string? groupBy)
    {
        if (granularity is not null && !Granularities.Contains(granularity))
        {
            throw Refused("granularity is none, hour, day or month.");
        }
        if (groupBy is not null && !Groups.Contains(groupBy))
        {
            throw Refused("group_by is model or channel.");
        }
        var q = new List<(string, string)>();
        foreach (var (name, value) in new[] { ("start", start), ("end", end), ("granularity", granularity), ("group_by", groupBy) })
        {
            if (value is not null)
            {
                q.Add((name, value));
            }
        }
        return q;
    }

    private static List<(string Name, string Value)> ListQuery(UsageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = Period(query.Start, query.End, query.Granularity, query.GroupBy);
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
        var index = 0;
        foreach (var id in query.KeyIds ?? Array.Empty<string>())
        {
            if (id is null || !KeyId.IsMatch(id))
            {
                throw Refused("key_id is a key's id, key_ and 32 hex digits.");
            }
            if (index == MaxKeyIds)
            {
                throw Refused("At most 100 key_id filters.");
            }
            q.Add(("key_id", id));
            index++;
        }
        if (query.Scope is { } scope)
        {
            if (!Scopes.Contains(scope))
            {
                throw Refused("scope is full, decide or keys.");
            }
            q.Add(("scope", scope));
        }
        if (query.Status is { } status)
        {
            if (!Statuses.Contains(status))
            {
                throw Refused("status is active, expired, revoked or all.");
            }
            q.Add(("status", status));
        }
        if (query.IncludeEmpty is { } includeEmpty)
        {
            q.Add(("include_empty", includeEmpty ? "true" : "false"));
        }
        if (query.Limit is { } limit)
        {
            if (limit is < 1 or > MaxLimit)
            {
                throw Refused("limit is a whole number from 1 to 1000.");
            }
            q.Add(("limit", limit.ToString(CultureInfo.InvariantCulture)));
        }
        return q;
    }

    private static BadRequestError Refused(string message) => LocalChecks.BadRequest(message, "api_usage_error", null);

    private async Task<JsonObject> GetAsync(string path, IReadOnlyList<(string Name, string Value)> query, CancellationToken ct)
    {
        var text = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value)));
        var full = path + (text.Length > 0 ? "?" + text : string.Empty);
        var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Get, full, null, null, DecisionNodeClient.RetryPolicy.Default, ct).ConfigureAwait(false);
        return parsed as JsonObject ?? throw new ServerError(new ErrorDetails
        {
            Status = result.Status,
            RequestId = result.Header("x-request-id"),
            Message = $"the response of GET {path} is not a JSON object",
            Body = parsed,
        });
    }
}

/// <summary>
/// The period and filters of <see cref="UsageClient.ListAsync"/> and <see cref="UsageClient.AllAsync"/>;
/// a filter left <c>null</c> is not sent.
/// </summary>
public sealed class UsageQuery
{
    /// <summary>The period's start, an ISO 8601 time.</summary>
    public required string Start { get; init; }

    /// <summary>The period's end, an ISO 8601 time.</summary>
    public required string End { get; init; }

    /// <summary><c>none</c>, <c>hour</c>, <c>day</c> or <c>month</c>: the buckets of each key's usage.</summary>
    public string? Granularity { get; init; }

    /// <summary><c>model</c> or <c>channel</c>.</summary>
    public string? GroupBy { get; init; }

    /// <summary>The keys whose labels hold every one of these, name to value; sent as <c>label=name:value</c>, sorted by name.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>The keys of one device.</summary>
    public string? DeviceId { get; init; }

    /// <summary>These keys only, at most 100.</summary>
    public IReadOnlyList<string>? KeyIds { get; init; }

    /// <summary><c>full</c>, <c>decide</c> or <c>keys</c>.</summary>
    public string? Scope { get; init; }

    /// <summary><c>active</c>, <c>expired</c>, <c>revoked</c> or <c>all</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Whether keys without usage in the period are listed.</summary>
    public bool? IncludeEmpty { get; init; }

    /// <summary>The most keys a page holds, 1 to 1000.</summary>
    public int? Limit { get; init; }
}
