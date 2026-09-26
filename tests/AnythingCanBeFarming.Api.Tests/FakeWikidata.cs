using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnythingCanBeFarming.DataImport;
using Microsoft.AspNetCore.WebUtilities;

namespace AnythingCanBeFarming.Api.Tests;

// In-memory stand-in for the query service and Action API. No test contacts Wikidata.
internal sealed partial class FakeWikidata
{
    public const string Preferred = "http://wikiba.se/ontology#PreferredRank";
    public const string Normal = "http://wikiba.se/ontology#NormalRank";
    public const string Deprecated = "http://wikiba.se/ontology#DeprecatedRank";

    public List<(string Qid, string WfoId, string Rank)> Statements { get; } = [];
    public Dictionary<string, string> Datatypes { get; } = new(WikidataApiClient.ExpectedDatatypes);
    public HashSet<string> HangingPartitions { get; } = [];
    public HashSet<string> FailingPartitions { get; } = [];
    public List<string> SparqlPartitions { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];
    public Queue<Func<HttpResponseMessage>> ApiResponses { get; } = new();
    public List<TimeSpan> Delays { get; } = [];

    public void Add(string qid, string wfoId, string rank = Normal) => Statements.Add((qid, wfoId, rank));

    public WikidataClients Clients(TimeSpan? sparqlTimeout = null) => new(new WikidataOptions
    {
        SparqlInterval = TimeSpan.Zero, ApiInterval = TimeSpan.Zero,
        SparqlRequestTimeout = sparqlTimeout ?? TimeSpan.FromSeconds(30)
    }, new StubHandler(SendAsync), (delay, _) => { lock (Delays) Delays.Add(delay); return Task.CompletedTask; });

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        return request.RequestUri!.AbsolutePath switch
        {
            "/sparql" => await SparqlAsync(request, cancellationToken),
            "/w/api.php" => ApiResponses.TryDequeue(out var response) ? response() : Api(QueryHelpers.ParseQuery(request.RequestUri.Query)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private async Task<HttpResponseMessage> SparqlAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        var query = form["query"].ToString();
        var suffix = SuffixPattern().Match(query) is { Success: true } match ? match.Groups[1].Value : null;
        if (suffix == null && !query.Contains("FILTER(!REGEX(?wfo, \"[0-9]$\"))")) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var label = suffix ?? "non-digit";
        lock (SparqlPartitions) SparqlPartitions.Add(label);
        if (HangingPartitions.Contains(label)) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (FailingPartitions.Contains(label)) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var rows = Statements.Where(x => x.Rank != Deprecated &&
            (suffix != null ? x.WfoId.EndsWith(suffix, StringComparison.Ordinal) : x.WfoId.Length == 0 || !char.IsAsciiDigit(x.WfoId[^1])));
        return SparqlResponse(rows);
    }

    public static HttpResponseMessage SparqlResponse(IEnumerable<(string Qid, string WfoId, string Rank)> rows)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("head");
            writer.WriteStartArray("vars");
            foreach (var name in new[] { "item", "wfo", "rank" }) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("results");
            writer.WriteStartArray("bindings");
            foreach (var (qid, wfoId, rank) in rows)
            {
                writer.WriteStartObject();
                Binding(writer, "item", "uri", "http://www.wikidata.org/entity/" + qid);
                Binding(writer, "wfo", "literal", wfoId);
                Binding(writer, "rank", "uri", rank);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(buffer.ToArray()) { Headers = { { "Content-Type", "application/sparql-results+json" } } }
        };
    }

    private static void Binding(Utf8JsonWriter writer, string name, string type, string value)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", type);
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    private HttpResponseMessage Api(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
    {
        if (query["action"] != "wbgetentities" || query["props"] != "datatype") return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var entities = query["ids"].ToString().Split('|').ToDictionary(id => id, id => (object)(Datatypes.TryGetValue(id, out var datatype)
            ? new { type = "property", id, datatype } : new { id, missing = "" }));
        return Json(new { entities, success = 1 });
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    [GeneratedRegex("""STRENDS\(\?wfo, "([0-9]+)"\)""")]
    private static partial Regex SuffixPattern();

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
