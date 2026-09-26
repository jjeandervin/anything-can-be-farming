using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnythingCanBeFarming.DataImport;

public sealed partial class WikidataApiClient(WikidataHttp http)
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

    // Every Action API call goes through here so maxlag and API errors are handled consistently.
    private Task<JsonDocument> GetAsync(string query, CancellationToken cancellationToken) =>
        http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Path}?{query}"), async (response, token) =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (!document.RootElement.TryGetProperty("error", out var error)) return document;
            using (document)
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (code == "maxlag") throw new WikidataRetryException(null, "Wikidata API reported maxlag");
                throw new WikidataHttpException(null, $"Wikidata API error {Token(code) ?? "(no code)"}.");
            }
        }, cancellationToken);

    // Error codes and datatypes are untrusted; only echo short, plain tokens.
    private static string? Token(string? value) => value != null && TokenPattern().IsMatch(value) ? value : value == null ? null : "(unrecognized)";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
