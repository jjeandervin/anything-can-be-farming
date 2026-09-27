using System.Text.Json;

namespace AnythingCanBeFarming.DataImport;

public sealed class WikidataApiClient(WikidataHttp http)
{
    public const string Path = "w/api.php";

    public static IReadOnlyDictionary<string, string> ExpectedDatatypes { get; } = new Dictionary<string, string>
    {
        ["P225"] = "string",
        ["P105"] = "wikibase-item",
        ["P1843"] = "monolingualtext",
        ["P18"] = "commonsMedia",
        ["P846"] = "external-id",
        ["P961"] = "external-id",
        ["P1772"] = "external-id",
        ["P5037"] = "external-id",
        ["P7715"] = "external-id"
    };

    public async Task VerifyPropertiesAsync(CancellationToken cancellationToken)
    {
        var ids = string.Join('|', ExpectedDatatypes.Keys);
        using var document = await GetAsync($"action=wbgetentities&ids={Uri.EscapeDataString(ids)}&props=datatype&format=json&maxlag=5",
            cancellationToken);
        var entities = document.RootElement.TryGetProperty("entities", out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw new InvalidDataException("Wikidata property check returned no entities.");
        var problems = new List<string>();
        foreach (var (id, expected) in ExpectedDatatypes)
        {
            var actual = entities.TryGetProperty(id, out var entity) && entity.TryGetProperty("datatype", out var datatype)
                && datatype.ValueKind == JsonValueKind.String ? datatype.GetString() : null;
            if (actual != expected) problems.Add($"{id} is {Token(actual) ?? "missing"}, expected {expected}");
        }
        if (problems.Count > 0)
            throw new InvalidDataException($"Wikidata property datatypes changed: {string.Join("; ", problems)}. Nothing was imported.");
    }

    public const int MaximumIdsPerRequest = 50;

    // infoOnly requests just lastrevid (and missing/redirect state) for the incremental check.
    public async Task<List<WikidataEntity>> GetEntitiesAsync(IReadOnlyCollection<string> qids, bool infoOnly,
        ICollection<string> warnings, CancellationToken cancellationToken)
    {
        if (qids.Count is 0 or > MaximumIdsPerRequest) throw new ArgumentOutOfRangeException(nameof(qids));
        if (!qids.All(Data.WikidataIdentifier.IsQid)) throw new ArgumentException("Only QIDs can be requested.", nameof(qids));
        var props = infoOnly ? "info" : "info|labels|claims|sitelinks";
        using var document = await GetAsync($"action=wbgetentities&ids={Uri.EscapeDataString(string.Join('|', qids))}" +
            $"&props={Uri.EscapeDataString(props)}{(infoOnly ? "" : "&languages=en&sitefilter=enwiki")}&format=json&maxlag=5",
            cancellationToken);
        return WikidataEntityParser.Parse(document.RootElement, warnings);
    }

    private Task<JsonDocument> GetAsync(string query, CancellationToken cancellationToken) =>
        MediaWikiActionApi.GetAsync(http, "Wikidata", Path, query, cancellationToken);

    private static string? Token(string? value) => MediaWikiActionApi.Token(value);
}
