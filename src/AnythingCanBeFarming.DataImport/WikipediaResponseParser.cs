using System.Text.Json;
using AnythingCanBeFarming.Data;

namespace AnythingCanBeFarming.DataImport;

// Title is the page reached after normalization and redirects; for a missing page it is the title that was looked up.
public sealed record WikipediaPageInfo(string RequestedTitle, string? Title, bool Redirected, bool Missing, long? PageId, long? LastRevId,
    string? WikibaseItem, string? ShortDescription, bool IsDisambiguation);

public sealed record WikipediaLead(string RequestedTitle, string? Title, bool Redirected, bool Missing, long? LastRevId, string? Extract);

// The API renames and reorders titles, so every result is mapped back to its requested title through the
// normalized (and converted) and redirects arrays; response position is never used.
public static class WikipediaResponseParser
{
    private const int MaximumRedirectHops = 10;

    public static List<WikipediaPageInfo> ParseCheck(JsonElement root, IReadOnlyList<string> requested)
    {
        var map = new TitleMap();
        map.Add(root);
        return requested.Select(title =>
        {
            var (target, redirected) = map.Resolve(title);
            return map.Pages.TryGetValue(target, out var page) && !page.Missing
                ? new WikipediaPageInfo(title, target, redirected, false, page.PageId, page.LastRevId, page.WikibaseItem,
                    page.ShortDescription, page.IsDisambiguation)
                : new WikipediaPageInfo(title, target, redirected, true, null, null, null, null, false);
        }).ToList();
    }

    // A continued lead request returns the same pages several times, each extract once, and lastrevid only on the first response.
    public static List<WikipediaLead> ParseLeads(IReadOnlyList<JsonElement> responses, IReadOnlyList<string> requested)
    {
        var map = new TitleMap();
        foreach (var root in responses) map.Add(root);
        return requested.Select(title =>
        {
            var (target, redirected) = map.Resolve(title);
            return map.Pages.TryGetValue(target, out var page) && !page.Missing
                ? new WikipediaLead(title, target, redirected, false, page.LastRevId, page.Extract)
                : new WikipediaLead(title, target, redirected, true, null, null);
        }).ToList();
    }

    private sealed class Page
    {
        public bool Missing { get; set; }
        public long? PageId { get; set; }
        public long? LastRevId { get; set; }
        public string? WikibaseItem { get; set; }
        public string? ShortDescription { get; set; }
        public bool IsDisambiguation { get; set; }
        public string? Extract { get; set; }
    }

    private sealed class TitleMap
    {
        private readonly Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> converted = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> redirects = new(StringComparer.Ordinal);
        public Dictionary<string, Page> Pages { get; } = new(StringComparer.Ordinal);

        public void Add(JsonElement root)
        {
            var query = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("query", out var value) && value.ValueKind == JsonValueKind.Object
                ? value : throw new InvalidDataException("Wikipedia returned no query result.");
            foreach (var (from, to) in Pairs(query, "normalized")) normalized[from] = to;
            foreach (var (from, to) in Pairs(query, "converted")) converted[from] = to;
            foreach (var (from, to) in Pairs(query, "redirects")) redirects[from] = to;
            if (!query.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Wikipedia returned no pages.");
            foreach (var element in pages.EnumerateArray())
            {
                if (String(element, "title") is not { } title) continue;
                if (!Pages.TryGetValue(title, out var page)) Pages[title] = page = new Page();
                page.Missing |= True(element, "missing") || True(element, "invalid");
                page.PageId ??= Number(element, "pageid");
                page.LastRevId ??= Number(element, "lastrevid");
                page.Extract ??= String(element, "extract");
                if (element.TryGetProperty("pageprops", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    page.WikibaseItem ??= String(props, "wikibase_item");
                    page.ShortDescription ??= String(props, "wikibase-shortdesc");
                    page.IsDisambiguation |= props.TryGetProperty("disambiguation", out _);
                }
            }
        }

        public (string Title, bool Redirected) Resolve(string requested)
        {
            // MediaWiki normalizes a title, then converts language variants, then follows redirects.
            var title = normalized.GetValueOrDefault(requested, requested);
            title = converted.GetValueOrDefault(title, title);
            var redirected = false;
            for (var hop = 0; hop < MaximumRedirectHops && redirects.TryGetValue(title, out var next) && next != title; hop++)
            {
                title = next;
                redirected = true;
            }
            return (title, redirected);
        }

        // With fromencoded, `from` is percent-encoded because the input was not valid UTF-8.
        private static IEnumerable<(string From, string To)> Pairs(JsonElement query, string name)
        {
            if (!query.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
            foreach (var entry in array.EnumerateArray())
                if (String(entry, "from") is { } from && String(entry, "to") is { } to)
                    yield return (True(entry, "fromencoded") ? Uri.UnescapeDataString(from) : from, to);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool True(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

public static class WikipediaLeadStatus
{
    // Spec §3, in order. A redirect never sets the status by itself: one landing on the requesting item's article is Ok,
    // and one landing on another item's article (often a species redirected to its genus) is an ItemMismatch.
    public static string Evaluate(bool missing, bool disambiguation, string? wikibaseItem, IReadOnlyCollection<string> requestingQids,
        string? leadText)
    {
        if (missing) return WikipediaArticleStatus.Missing;
        if (disambiguation) return WikipediaArticleStatus.Disambiguation;
        if (wikibaseItem == null || !requestingQids.Contains(wikibaseItem)) return WikipediaArticleStatus.ItemMismatch;
        if (string.IsNullOrWhiteSpace(leadText)) return WikipediaArticleStatus.EmptyLead;
        return WikipediaArticleStatus.Ok;
    }
}

public static class WikipediaUrl
{
    public const string EnglishArticleBase = "https://en.wikipedia.org/wiki/";

    // MediaWiki's wfUrlencode: percent-encode, then leave ; @ $ ! * ( ) , / : unescaped, so the URL is the one Wikipedia itself uses.
    public static string For(string title)
    {
        var escaped = Uri.EscapeDataString(title.Replace(' ', '_'));
        foreach (var (encoded, character) in Unescaped) escaped = escaped.Replace(encoded, character, StringComparison.Ordinal);
        return EnglishArticleBase + escaped;
    }

    private static readonly (string Encoded, string Character)[] Unescaped =
        [("%3B", ";"), ("%40", "@"), ("%24", "$"), ("%21", "!"), ("%2A", "*"), ("%28", "("), ("%29", ")"), ("%2C", ","), ("%2F", "/"), ("%3A", ":")];
}
