using System.Globalization;
using System.Text.RegularExpressions;
using AnythingCanBeFarming.Data;

namespace AnythingCanBeFarming.DataImport;

public sealed record PlaceReference(string Scheme, string? PlaceId);

public static partial class UsdaValues
{
    public const string PresentType = "http://eol.org/schema/terms/Present";
    public const string NativeRangeType = "http://eol.org/schema/terms/NativeRange";
    public const string IntroducedRangeType = "http://eol.org/schema/terms/IntroducedRange";

    public static string? DistributionKind(string typeUri) => typeUri switch
    {
        PresentType => "Present",
        NativeRangeType => "Native",
        IntroducedRangeType => "Introduced",
        _ => null
    };

    // All digits: GeoNames. Q-number: Wikidata. A URI: its last path segment under the same two rules.
    public static PlaceReference ClassifyPlace(string raw)
    {
        var candidate = UsdaCode.FromValue(raw);
        if (candidate.Length > 0 && candidate.All(char.IsAsciiDigit)) return new("geonames", candidate);
        if (WikidataIdentifier.IsQid(candidate)) return new("wikidata", candidate);
        return new("literal", null);
    }

    // Remarks sometimes open with "Source term: <USDA field>." naming the PLANTS characteristic.
    public static string? SourceTerm(string? remarks) =>
        remarks != null && SourceTermPattern().Match(remarks) is { Success: true } match ? match.Groups[1].Value : null;

    public static decimal? Number(string value) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var number) ? number : null;

    // Section 4.1: the name without authorship, in WFO's spelling ("× " before a hybrid genus or species epithet,
    // "subsp." for ssp.). Returns null when the name does not have the shape its rank needs.
    public static string? CanonicalName(string scientificName, string? rank)
    {
        var tokens = scientificName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var position = 0;
        var (genus, hybridGenus) = NamePart(tokens, ref position, GenusPattern());
        if (genus == null) return null;
        var name = (hybridGenus ? "× " : "") + genus;
        if (rank == "genus") return name;
        if (rank is not ("species" or "variety" or "subspecies" or "form")) return null;
        var (epithet, hybridSpecies) = NamePart(tokens, ref position, EpithetPattern());
        if (epithet == null || NotEpithets.Contains(epithet)) return null;
        name += " " + (hybridSpecies ? "× " : "") + epithet.Replace(".", "");
        if (rank == "species") return name;
        var (markers, marker) = rank switch
        {
            "variety" => (new[] { "var." }, "var."),
            "subspecies" => (["ssp.", "subsp."], "subsp."),
            _ => (["f.", "fo.", "forma"], "f.")
        };
        // Search from the end: authorship before the marker can contain the same token (e.g. "Hook. f.").
        // WFO writes infraspecific names without a hybrid sign (Senna artemisioides subsp. coriacea for "ssp. ×coriacea").
        for (var i = tokens.Length - 2; i >= position; i--)
        {
            var infraspecific = tokens[i + 1].TrimStart('×');
            if (markers.Contains(tokens[i]) && EpithetPattern().IsMatch(infraspecific) && !NotEpithets.Contains(infraspecific))
                return $"{name} {marker} {infraspecific.Replace(".", "")}";
        }
        return null;
    }

    private static (string? Value, bool Hybrid) NamePart(string[] tokens, ref int position, Regex pattern)
    {
        var hybrid = false;
        if (position < tokens.Length && tokens[position] is "×" or "x" or "X")
        {
            hybrid = true;
            position++;
        }
        if (position >= tokens.Length) return (null, false);
        var token = tokens[position++];
        if (token.StartsWith('×'))
        {
            hybrid = true;
            token = token[1..];
        }
        return pattern.IsMatch(token) ? (token, hybrid) : (null, false);
    }

    // Lowercase connectives in authorship that look like epithets.
    private static readonly HashSet<string> NotEpithets = ["ex", "in", "et", "and"];

    [GeneratedRegex(@"^\p{Lu}\p{Ll}+(-\p{Ll}+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex GenusPattern();

    // Allows USDA's "st.-johnii"; the canonical name drops the dot, as WFO does (Cyanea st-johnii).
    [GeneratedRegex(@"^\p{Ll}+(\.?-\p{Ll}+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex EpithetPattern();

    [GeneratedRegex(@"^Source term: ([^.\\]+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex SourceTermPattern();
}
