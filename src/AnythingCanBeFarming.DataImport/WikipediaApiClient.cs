using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AnythingCanBeFarming.DataImport;

public sealed class WikipediaOptions
{
    public string UserAgent { get; set; } = WikidataOptions.DefaultUserAgent;
    public Uri ApiBaseAddress { get; set; } = new("https://en.wikipedia.org/");
    public TimeSpan ApiInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan ApiRequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public static WikipediaOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new WikipediaOptions();
        var userAgent = configuration["Wikipedia:UserAgent"] ?? configuration["Wikidata:UserAgent"];
        if (!string.IsNullOrWhiteSpace(userAgent)) options.UserAgent = userAgent;
        if (!string.IsNullOrWhiteSpace(configuration["Wikipedia:ApiBaseAddress"]))
            options.ApiBaseAddress = new Uri(configuration["Wikipedia:ApiBaseAddress"]!);
        return options;
    }
}

public sealed class WikipediaClients : IDisposable
{
    private readonly HttpClient http;

    public WikipediaClients(WikipediaOptions options, HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
    {
        http = new HttpClient(handler ?? WikidataOptions.CreateHandler(), disposeHandler: handler == null)
            { BaseAddress = options.ApiBaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        // Same politeness rules as Wikidata: one request at a time, a minimum gap, maxlag, and bounded Retry-After handling.
        Api = new WikipediaApiClient(new WikidataHttp(http, new WikidataOptions { UserAgent = options.UserAgent },
            options.ApiInterval, options.ApiRequestTimeout, delay, time, service: "Wikipedia"));
    }

    public WikipediaApiClient Api { get; }

    public void Dispose() => http.Dispose();
}

// Selects articles only by title (Wikidata sitelinks); it never searches Wikipedia.
public sealed class WikipediaApiClient(WikidataHttp http)
{
    public const string Path = "w/api.php";
    public const int MaximumCheckTitles = 50;
    // TextExtracts returns at most 20 intro extracts per request.
    public const int MaximumLeadTitles = 20;
    public const int MaximumContinuations = 20;

    // Requests made only to continue a lead batch (TextExtracts returned fewer extracts than requested).
    public long ContinuationRequests { get; private set; }

    // Page metadata for the incremental check: lastrevid, page ID, Wikidata item, short description, and disambiguation.
    public async Task<List<WikipediaPageInfo>> CheckAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken)
    {
        Validate(titles, MaximumCheckTitles);
        using var document = await GetAsync("action=query&prop=info%7Cpageprops&ppprop=wikibase_item%7Cwikibase-shortdesc%7Cdisambiguation" +
            $"&redirects=1&titles={Titles(titles)}&format=json&formatversion=2&maxlag=5", cancellationToken);
        return WikipediaResponseParser.ParseCheck(document.RootElement, titles);
    }

    // Plain-text lead sections, following `continue` until every requested page has been returned.
    public async Task<List<WikipediaLead>> GetLeadsAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken)
    {
        Validate(titles, MaximumLeadTitles);
        var query = "action=query&prop=extracts%7Cinfo&exintro=1&explaintext=1&exsectionformat=plain" +
            $"&exlimit={MaximumLeadTitles}&redirects=1&titles={Titles(titles)}&format=json&formatversion=2&maxlag=5";
        var responses = new List<JsonDocument>();
        try
        {
            var continuation = "";
            while (true)
            {
                var document = await GetAsync(query + continuation, cancellationToken);
                responses.Add(document);
                if (!document.RootElement.TryGetProperty("continue", out var next) || next.ValueKind != JsonValueKind.Object) break;
                if (responses.Count > MaximumContinuations)
                    throw new InvalidDataException($"Wikipedia kept continuing a lead batch after {MaximumContinuations} requests.");
                continuation = Continuation(next);
                ContinuationRequests++;
            }
            return WikipediaResponseParser.ParseLeads(responses.Select(x => x.RootElement).ToList(), titles);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    // Titles are joined with '|', so a title containing one (or a control character) can never be requested.
    public static bool IsRequestable(string title) =>
        !string.IsNullOrWhiteSpace(title) && !title.Contains('|') && !title.Any(char.IsControl);

    private static void Validate(IReadOnlyList<string> titles, int maximum)
    {
        if (titles.Count == 0 || titles.Count > maximum) throw new ArgumentOutOfRangeException(nameof(titles));
        if (!titles.All(IsRequestable)) throw new ArgumentException("A title cannot be requested.", nameof(titles));
    }

    private static string Titles(IReadOnlyList<string> titles) => Uri.EscapeDataString(string.Join('|', titles));

    // `continue` holds every parameter to send back, e.g. {"excontinue": 20, "continue": "||info"}.
    private static string Continuation(JsonElement next)
    {
        var builder = new StringBuilder();
        foreach (var property in next.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            builder.Append('&').Append(Uri.EscapeDataString(property.Name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return builder.ToString();
    }

    private Task<JsonDocument> GetAsync(string query, CancellationToken cancellationToken) =>
        MediaWikiActionApi.GetAsync(http, "Wikipedia", Path, query, cancellationToken);
}
