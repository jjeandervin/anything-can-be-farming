using System.Text.Json;
using AnythingCanBeFarming.Data;

namespace AnythingCanBeFarming.DataImport;

public sealed record WikidataCommonNameValue(string Language, string Name);
public sealed record WikidataExternalIdValue(string Property, string Value);

// Qid is where the data lives: for a redirect it is the target, and RequestedQid is the old item.
public sealed record WikidataEntity(string RequestedQid, string Qid, bool Missing, long? LastRevId)
{
    public bool IsRedirect => RequestedQid != Qid;
    public string? TaxonName { get; init; }
    public string? TaxonRankQid { get; init; }
    public string? LabelEn { get; init; }
    public string? EnwikiTitle { get; init; }
    public string? ImageFile { get; init; }
    public IReadOnlyList<WikidataCommonNameValue> CommonNames { get; init; } = [];
    public IReadOnlyList<WikidataExternalIdValue> ExternalIds { get; init; } = [];
    public IReadOnlyList<string> WfoIds { get; init; } = [];
}

public static class WikidataEntityParser
{
    public static IReadOnlyList<string> ExternalIdProperties { get; } = ["P846", "P961", "P1772", "P5037"];

    // Reads a wbgetentities response. Entities are keyed by the requested ID; a redirect carries
    // redirects.from/to and the target's data. Values that are not what the property promises are skipped.
    public static List<WikidataEntity> Parse(JsonElement root, ICollection<string> warnings)
    {
        var entities = new List<WikidataEntity>();
        if (!root.TryGetProperty("entities", out var map) || map.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Wikidata entity response contained no entities.");
        foreach (var property in map.EnumerateObject())
        {
            var entity = property.Value;
            var requested = String(entity, "redirects", "from") ?? property.Name;
            var qid = String(entity, "id") ?? requested;
            if (!WikidataIdentifier.IsQid(requested) || !WikidataIdentifier.IsQid(qid))
            {
                warnings.Add("Entity response contained an ID that is not a QID; it was skipped.");
                continue;
            }
            if (entity.TryGetProperty("missing", out _))
            {
                entities.Add(new(requested, requested, true, null));
                continue;
            }
            var lastRevId = entity.TryGetProperty("lastrevid", out var revision) && revision.TryGetInt64(out var value) ? value : (long?)null;
            var claims = entity.TryGetProperty("claims", out var claimMap) && claimMap.ValueKind == JsonValueKind.Object ? claimMap : default;
            entities.Add(new(requested, qid, false, lastRevId)
            {
                LabelEn = String(entity, "labels", "en", "value"),
                EnwikiTitle = String(entity, "sitelinks", "enwiki", "title"),
                TaxonName = Values(claims, "P225", StringValue).FirstOrDefault(),
                TaxonRankQid = Values(claims, "P105", ItemValue).FirstOrDefault(),
                ImageFile = Values(claims, "P18", StringValue).FirstOrDefault(),
                CommonNames = Values(claims, "P1843", MonolingualValue).OfType<WikidataCommonNameValue>().Distinct().ToList(),
                ExternalIds = ExternalIdProperties.SelectMany(p => Values(claims, p, StringValue).Select(v => new WikidataExternalIdValue(p, v!)))
                    .Distinct().ToList(),
                WfoIds = Values(claims, "P7715", StringValue).OfType<string>().Distinct(StringComparer.Ordinal).ToList()
            });
        }
        return entities;
    }

    // Non-deprecated values that have a real value (not somevalue/novalue), preferred rank first,
    // otherwise in statement order.
    private static IEnumerable<T?> Values<T>(JsonElement claims, string property, Func<JsonElement, T?> read) where T : class
    {
        if (claims.ValueKind != JsonValueKind.Object || !claims.TryGetProperty(property, out var statements)
            || statements.ValueKind != JsonValueKind.Array) return [];
        return statements.EnumerateArray()
            .Select((statement, index) => (Rank: String(statement, "rank"), Index: index,
                Value: String(statement, "mainsnak", "snaktype") == "value" && statement.TryGetProperty("mainsnak", out var snak)
                    && snak.TryGetProperty("datavalue", out var datavalue) && datavalue.TryGetProperty("value", out var raw) ? read(raw) : null))
            .Where(x => x.Rank is "preferred" or "normal" && x.Value != null)
            .OrderBy(x => x.Rank == "preferred" ? 0 : 1).ThenBy(x => x.Index)
            .Select(x => x.Value);
    }

    private static string? StringValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ItemValue(JsonElement value)
    {
        var id = String(value, "id") ?? (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("numeric-id", out var numeric)
            && numeric.TryGetInt64(out var number) ? "Q" + number : null);
        return WikidataIdentifier.IsQid(id) ? id : null;
    }

    private static WikidataCommonNameValue? MonolingualValue(JsonElement value) =>
        String(value, "text") is { } text && String(value, "language") is { } language ? new(language, text) : null;

    private static string? String(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
