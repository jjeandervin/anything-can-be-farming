using System.Net;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.DataImport;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace AnythingCanBeFarming.Api.Tests;

// In-memory stand-in for the English Wikipedia Action API, shaped like the saved responses in Fixtures/wikipedia.
// No test contacts Wikipedia.
internal sealed class FakeWikipedia
{
    public sealed class Page
    {
        public long PageId { get; set; }
        public long Revision { get; set; }
        public string? Item { get; set; }
        public string? ShortDescription { get; set; }
        public bool Disambiguation { get; set; }
        public string? Lead { get; set; }
    }

    public Dictionary<string, Page> Pages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);
    public List<(string Kind, string[] Titles)> Requests { get; } = [];
    public List<HttpRequestMessage> HttpRequests { get; } = [];
    public Queue<Func<HttpResponseMessage>> Responses { get; } = new();
    public List<TimeSpan> Delays { get; } = [];
    // Like TextExtracts under its response-size limit: return at most this many extracts, then `continue`.
    public int? ExtractsPerResponse { get; set; }
    // Returns a replacement response for the nth lead request (for example, to simulate a crash).
    public Func<int, HttpResponseMessage?>? LeadRequestOverride { get; set; }

    public Page Add(string title, long revision, string? item, string? lead, string? shortDescription = null, bool disambiguation = false)
    {
        var page = new Page
        {
            PageId = Pages.Count + 1000, Revision = revision, Item = item, Lead = lead,
            ShortDescription = shortDescription, Disambiguation = disambiguation
        };
        Pages[title] = page;
        return page;
    }

    public int LeadRequests => Requests.Count(x => x.Kind == "leads");
    public string[] LeadTitles => Requests.Where(x => x.Kind == "leads").SelectMany(x => x.Titles).ToArray();

    public WikipediaClients Clients() => new(new WikipediaOptions { ApiInterval = TimeSpan.Zero }, new StubHandler(Send),
        (delay, _) => { lock (Delays) Delays.Add(delay); return Task.CompletedTask; });

    private HttpResponseMessage Send(HttpRequestMessage request)
    {
        HttpRequests.Add(request);
        if (Responses.TryDequeue(out var canned)) return canned();
        if (request.RequestUri!.AbsolutePath != "/w/api.php") return new HttpResponseMessage(HttpStatusCode.NotFound);
        var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
        foreach (var (name, expected) in new[] { ("action", "query"), ("redirects", "1"), ("format", "json"), ("formatversion", "2"), ("maxlag", "5") })
            if (query[name] != expected) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var titles = query["titles"].ToString().Split('|');
        if (query["prop"] == "info|pageprops" && query["ppprop"] == "wikibase_item|wikibase-shortdesc|disambiguation")
        {
            Requests.Add(("check", titles));
            return Json(Result(titles, (title, page) => Check(title, page), null));
        }
        if (query["prop"] == "extracts|info" && query["exintro"] == "1" && query["explaintext"] == "1"
            && query["exsectionformat"] == "plain" && query["exlimit"] == "20")
        {
            var continued = query.ContainsKey("excontinue");
            if (!continued)
            {
                Requests.Add(("leads", titles));
                if (LeadRequestOverride?.Invoke(LeadRequests) is { } replacement) return replacement;
            }
            return Json(Leads(titles, query, continued));
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private static Dictionary<string, object?> Check(string title, Page page)
    {
        var props = new Dictionary<string, object?>();
        if (page.Disambiguation) props["disambiguation"] = "";
        if (page.ShortDescription != null) props["wikibase-shortdesc"] = page.ShortDescription;
        if (page.Item != null) props["wikibase_item"] = page.Item;
        var result = new Dictionary<string, object?> { ["pageid"] = page.PageId, ["ns"] = 0, ["title"] = title, ["lastrevid"] = page.Revision };
        if (props.Count > 0) result["pageprops"] = props;
        return result;
    }

    // Pages come back in page ID order, as they do from Wikipedia. Continued responses repeat every page without lastrevid.
    private Dictionary<string, object?> Leads(string[] titles, Dictionary<string, StringValues> query, bool continued)
    {
        var offset = continued ? int.Parse(query["excontinue"]!) : 0;
        var existing = Resolve(titles).Where(x => x.Page != null).Select(x => x.Title).Distinct().OrderBy(x => Pages[x].PageId).ToList();
        var end = Math.Min(existing.Count, offset + (ExtractsPerResponse ?? int.MaxValue));
        var withExtract = existing.Skip(offset).Take(end - offset).ToHashSet();
        var result = Result(titles, (title, page) =>
        {
            var row = new Dictionary<string, object?> { ["pageid"] = page.PageId, ["ns"] = 0, ["title"] = title };
            if (!continued) row["lastrevid"] = page.Revision;
            if (withExtract.Contains(title) && page.Lead != null) row["extract"] = page.Lead;
            return row;
        }, end < existing.Count ? new Dictionary<string, object> { ["excontinue"] = end, ["continue"] = "||info" } : null);
        return result;
    }

    private Dictionary<string, object?> Result(string[] titles, Func<string, Page, Dictionary<string, object?>> row, object? next)
    {
        var resolved = Resolve(titles);
        var body = new Dictionary<string, object?>();
        if (next != null) body["continue"] = next;
        else body["batchcomplete"] = true;
        var queryResult = new Dictionary<string, object?>();
        var normalized = resolved.Where(x => x.Normalized != x.Requested)
            .Select(x => new { fromencoded = false, from = x.Requested, to = x.Normalized }).ToList();
        if (normalized.Count > 0) queryResult["normalized"] = normalized;
        var redirects = resolved.Where(x => x.Title != x.Normalized).DistinctBy(x => x.Normalized)
            .Select(x => new { from = x.Normalized, to = x.Title }).ToList();
        if (redirects.Count > 0) queryResult["redirects"] = redirects;
        queryResult["pages"] = resolved.DistinctBy(x => x.Title).OrderBy(x => x.Page?.PageId ?? 0)
            .Select(x => x.Page == null ? new Dictionary<string, object?> { ["ns"] = 0, ["title"] = x.Title, ["missing"] = true } : row(x.Title, x.Page))
            .ToList();
        body["query"] = queryResult;
        return body;
    }

    private List<(string Requested, string Normalized, string Title, Page? Page)> Resolve(string[] titles) =>
        titles.Select(requested =>
        {
            var normalized = Normalize(requested);
            var title = Redirects.GetValueOrDefault(normalized, normalized);
            return (requested, normalized, title, Pages.GetValueOrDefault(title));
        }).ToList();

    private static string Normalize(string title)
    {
        var spaced = title.Replace('_', ' ');
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Fixture(string name) =>
        new(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(FixturePath(name)), Encoding.UTF8, "application/json") };

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "wikipedia", name);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
