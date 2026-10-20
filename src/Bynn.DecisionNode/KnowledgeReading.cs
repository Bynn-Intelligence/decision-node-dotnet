using System.Text.Json;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// The <c>knowledge</c> booster's reading: the passages of a knowledge base that apply to the
/// decision, pinned ones first, each with its citation (<see cref="KnowledgePassage.Item"/>,
/// <see cref="KnowledgePassage.Chunk"/> and the byte offsets into the item's stored text). It is
/// the one result of <c>Evidence["knowledge"]</c>, and what
/// <see cref="CollectionsClient.SearchAsync"/> returns. Every field is read as sent; numbers are
/// passed through as received.
/// </summary>
/// <remarks>
/// A reading with <c>collection</c> or <c>passages</c> missing or of the wrong type, or a
/// passage missing <c>n</c>, <c>pinned</c>, <c>item</c>, <c>chunk</c>, <c>byte_start</c> or
/// <c>byte_end</c> or holding one of the wrong type, is an <see cref="UnknownReading"/> with its
/// raw fields instead, and the decision does not fail.
/// </remarks>
public sealed class KnowledgeReading : Reading
{
    private KnowledgeReading(JsonObject raw, string collection, IReadOnlyList<KnowledgePassage> passages)
        : base(DecisionNode.Booster.KnowledgeName, raw)
    {
        Collection = collection;
        Passages = passages;
        CollectionName = Text(raw["collection_name"]);
        Corpus = raw["corpus"] is JsonObject corpus ? new KnowledgeCorpus(JsonValues.AsLong(corpus["version"]), corpus) : null;
        Query = raw["query"] is JsonObject query
            ? new KnowledgeQuery(Text(query["source"]), JsonValues.AsLong(query["tokens"]), Text(query["text"]), query)
            : null;
        Budget = raw["budget"] is JsonObject budget
            ? new KnowledgeBudget(Text(budget["mode"]), JsonValues.AsLong(budget["tokens"]), Text(budget["entry"]), Text(budget["cause"]), budget)
            : null;
        Scope = raw["scope"] is JsonObject scope ? new KnowledgeScope(Text(scope["key"]), Text(scope["value"])) : null;
        Warnings = raw["warnings"] is JsonArray warnings
            ? warnings.Select(Text).OfType<string>().ToList()
            : Array.Empty<string>();
        Omitted = raw["omitted"] as JsonObject;
        PendingItems = JsonValues.AsLong(raw["pending_items"]);
        Search = raw["search"] is JsonObject search
            ? new KnowledgeSearchPlan(Text(search["plan"]), JsonValues.AsLong(search["candidates"]), Boolean(search["fallback"]), search)
            : null;
        Truncated = Boolean(raw["truncated"]);
    }

    /// <summary>The knowledge base read: its <c>col_</c> id.</summary>
    public string Collection { get; }

    /// <summary>The knowledge base's name; <c>null</c> for a publishable key's reading.</summary>
    public string? CollectionName { get; }

    /// <summary>The corpus version read (<c>corpus</c>); <c>null</c> when absent.</summary>
    public KnowledgeCorpus? Corpus { get; }

    /// <summary>The query the passages were found by (<c>query</c>); <c>null</c> when absent.</summary>
    public KnowledgeQuery? Query { get; }

    /// <summary>The retrieved passages' token budget (<c>budget</c>); <c>null</c> when absent.</summary>
    public KnowledgeBudget? Budget { get; }

    /// <summary>The scope key and the value named (<c>scope</c>); <c>null</c> for a collection without a scope key.</summary>
    public KnowledgeScope? Scope { get; }

    /// <summary>
    /// The reading's warnings, as sent: <c>scope_not_named</c>, <c>over_budget</c>,
    /// <c>budget_floor</c>, <c>pinned_filtered</c> or a newer one; empty when there are none.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The passages, in the block's order: pinned first, then the retrieved ones.</summary>
    public IReadOnlyList<KnowledgePassage> Passages { get; }

    /// <summary>
    /// The passages left out, by cause (<c>below_min_score</c>, <c>per_item_cap</c>,
    /// <c>over_budget</c>, <c>pinned_filtered</c>, <c>short_entry</c>, <c>context_limit</c> or a
    /// newer one), kept as sent; <c>null</c> when absent.
    /// </summary>
    public JsonObject? Omitted { get; }

    /// <summary>How many of the knowledge base's documents were not ready yet.</summary>
    public long? PendingItems { get; }

    /// <summary>How the search ran (<c>search</c>); <c>null</c> when absent.</summary>
    public KnowledgeSearchPlan? Search { get; }

    /// <summary>
    /// On a decision's reading, <c>true</c> when a passage was dropped to fit the model's
    /// context; <c>null</c> when absent, as on a search's answer.
    /// </summary>
    public bool? Truncated { get; }

    internal static KnowledgeReading Read(JsonObject raw)
    {
        var collection = Text(raw, "collection");
        var passages = List(raw, "passages", Passage);
        return new KnowledgeReading(raw, collection, passages);
    }

    private static KnowledgePassage Passage(JsonObject p) => new(
        Whole(p, "n"),
        Flag(p, "pinned"),
        Text(p, "item"),
        Whole(p, "chunk"),
        Whole(p, "byte_start"),
        Whole(p, "byte_end"),
        Text(p["reference"]),
        Text(p["title"]),
        Text(p["heading"]),
        JsonValues.AsLong(p["tokens"]),
        JsonValues.TryDouble(p["score"], out var score) ? score : null,
        Boolean(p["lexical"]),
        Boolean(p["rendered"]),
        Text(p["text"]),
        p);

    /// <summary>A field that is read as sent: its string, else <c>null</c>.</summary>
    private static string? Text(JsonNode? node) => JsonValues.AsString(node);

    /// <summary>A field that is read as sent: its boolean, else <c>null</c>.</summary>
    private static bool? Boolean(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
}

/// <summary>
/// One passage of a <see cref="KnowledgeReading"/>: a chunk of a document and its citation.
/// <see cref="ByteStart"/> and <see cref="ByteEnd"/> are UTF-8 byte offsets, end exclusive, into
/// the item's stored text (<see cref="CollectionsClient.GetItemAsync"/> with <c>includeText</c>):
/// encode that text to UTF-8 before slicing it by them.
/// </summary>
/// <param name="N">The passage's number in the block the model read, from 1.</param>
/// <param name="Pinned">Whether it comes from a pinned document, always included.</param>
/// <param name="Item">The document's <c>itm_</c> id.</param>
/// <param name="Chunk">The chunk's number within the document.</param>
/// <param name="ByteStart">Where the chunk starts in the stored text, in UTF-8 bytes.</param>
/// <param name="ByteEnd">Where it ends, in UTF-8 bytes, exclusive.</param>
/// <param name="Reference">The document's reference; <c>null</c> for a publishable key's reading.</param>
/// <param name="Title">The document's title; <c>null</c> for a publishable key's reading.</param>
/// <param name="Heading">The chunk's heading path; <c>null</c> when it has none or for a publishable key's reading.</param>
/// <param name="Tokens">The passage's tokens.</param>
/// <param name="Score">The similarity, as sent (4 decimals); <c>null</c> for a pinned passage.</param>
/// <param name="Lexical">Whether it matched on words.</param>
/// <param name="Rendered">Whether the model read it; <c>false</c> when it was left out to fit. <c>null</c> on a search's passages.</param>
/// <param name="Text">The passage's text, with <c>include_text</c> and always on a search; else <c>null</c>.</param>
/// <param name="Raw">The passage exactly as received.</param>
public sealed record KnowledgePassage(
    long N,
    bool Pinned,
    string Item,
    long Chunk,
    long ByteStart,
    long ByteEnd,
    string? Reference,
    string? Title,
    string? Heading,
    long? Tokens,
    double? Score,
    bool? Lexical,
    bool? Rendered,
    string? Text,
    JsonObject Raw)
{
    public bool Equals(KnowledgePassage? other) =>
        other is not null && N == other.N && Pinned == other.Pinned && Item == other.Item && Chunk == other.Chunk
        && ByteStart == other.ByteStart && ByteEnd == other.ByteEnd && JsonNode.DeepEquals(Raw, other.Raw);

    public override int GetHashCode() => HashCode.Combine(N, Pinned, Item, Chunk, ByteStart, ByteEnd);
}

/// <summary>The corpus a <see cref="KnowledgeReading"/> read.</summary>
/// <param name="Version">The corpus version, raised by every change a search can see.</param>
/// <param name="Raw">The object exactly as received.</param>
public sealed record KnowledgeCorpus(long? Version, JsonObject Raw)
{
    public bool Equals(KnowledgeCorpus? other) => other is not null && JsonNode.DeepEquals(Raw, other.Raw);

    public override int GetHashCode() => HashCode.Combine(Version);
}

/// <summary>The query a <see cref="KnowledgeReading"/>'s passages were found by.</summary>
/// <param name="Source"><c>auto</c> (built from the request), <c>request</c> (the <c>query</c> option), <c>none</c> (<c>top_k</c> 0) or a newer value.</param>
/// <param name="Tokens">The query's tokens.</param>
/// <param name="Text">The query's text, with <c>include_text</c> and on a search; else <c>null</c>.</param>
/// <param name="Raw">The object exactly as received.</param>
public sealed record KnowledgeQuery(string? Source, long? Tokens, string? Text, JsonObject Raw)
{
    public bool Equals(KnowledgeQuery? other) => other is not null && JsonNode.DeepEquals(Raw, other.Raw);

    public override int GetHashCode() => HashCode.Combine(Source, Tokens, Text);
}

/// <summary>The retrieved passages' token budget.</summary>
/// <param name="Mode"><c>auto</c> or <c>fixed</c>.</param>
/// <param name="Tokens">The retrieved passages' tokens.</param>
/// <param name="Entry"><c>short</c>, <c>long</c> or <c>null</c>.</param>
/// <param name="Cause"><c>null</c>, or why a long entry was used: <c>state</c>, <c>images</c>, <c>other_boosters</c>, <c>pinned</c>, <c>fixed</c>, <c>require_floor</c> or a newer value.</param>
/// <param name="Raw">The object exactly as received.</param>
public sealed record KnowledgeBudget(string? Mode, long? Tokens, string? Entry, string? Cause, JsonObject Raw)
{
    public bool Equals(KnowledgeBudget? other) => other is not null && JsonNode.DeepEquals(Raw, other.Raw);

    public override int GetHashCode() => HashCode.Combine(Mode, Tokens, Entry, Cause);
}

/// <summary>The knowledge base's scope key and the value the request named (<c>null</c> when it named none).</summary>
public sealed record KnowledgeScope(string? Key, string? Value);

/// <summary>How a knowledge search ran.</summary>
/// <param name="Plan">The search plan, as sent.</param>
/// <param name="Candidates">The candidates considered.</param>
/// <param name="Fallback">Whether an exact rerun made up a short candidate list.</param>
/// <param name="Raw">The object exactly as received.</param>
public sealed record KnowledgeSearchPlan(string? Plan, long? Candidates, bool? Fallback, JsonObject Raw)
{
    public bool Equals(KnowledgeSearchPlan? other) => other is not null && JsonNode.DeepEquals(Raw, other.Raw);

    public override int GetHashCode() => HashCode.Combine(Plan, Candidates, Fallback);
}
