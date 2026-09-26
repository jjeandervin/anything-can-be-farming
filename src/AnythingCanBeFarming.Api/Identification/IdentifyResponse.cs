using AnythingCanBeFarming.Api.PlantNet;

namespace AnythingCanBeFarming.Api.Identification;

public sealed record IdentifyError(string Error, string Message);

public sealed record IdentifyResponse(
    string? BestMatch,
    int? RemainingRequests,
    IReadOnlyList<IdentifyPredictedOrgan> PredictedOrgans,
    IReadOnlyList<IdentifyResult> Results)
{
    public static readonly IdentifyResponse NoMatch = new(null, null, [], []);

    /// <param name="fileNames">The uploaded file names, in image order, used to map predicted organs back to photos.</param>
    public static IdentifyResponse From(PlantNetIdentificationResult result, IReadOnlyList<string> fileNames)
    {
        var indexes = fileNames.Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
        var predicted = (result.PredictedOrgans ?? [])
            .Select(p => p is { Filename: { } file, Score: { } score } && indexes.TryGetValue(file, out var index) &&
                    PlantOrgans.TryParse(p.Organ, out var organ)
                ? new IdentifyPredictedOrgan(index, PlantOrgans.ToWire(organ), score)
                : null)
            .OfType<IdentifyPredictedOrgan>()
            .ToList();
        var results = (result.Results ?? []).Select(IdentifyResult.From).OfType<IdentifyResult>().ToList();
        var remaining = result.RemainingIdentificationRequests is { } r
            ? (int)Math.Clamp(Math.Round(r), int.MinValue, int.MaxValue)
            : (int?)null;
        return new IdentifyResponse(result.BestMatch, remaining, predicted, results);
    }
}

public sealed record IdentifyPredictedOrgan(int ImageIndex, string Organ, double Score);

public sealed record IdentifyResult(
    double Score,
    string ScientificName,
    string ScientificNameWithoutAuthor,
    string? Authorship,
    string? Genus,
    string? Family,
    IReadOnlyList<string> CommonNames,
    long? GbifId,
    string? PowoId,
    IReadOnlyList<IdentifyReferenceImage> ReferenceImages)
{
    /// <summary>Returns null for matches without a score or a scientific name.</summary>
    public static IdentifyResult? From(PlantNetMatch match)
    {
        if (match is not { Score: { } score, Species: { } species } || string.IsNullOrEmpty(species.ScientificName))
            return null;
        return new IdentifyResult(
            score,
            species.ScientificName,
            string.IsNullOrEmpty(species.ScientificNameWithoutAuthor) ? species.ScientificName : species.ScientificNameWithoutAuthor,
            species.ScientificNameAuthorship,
            TaxonName(species.Genus),
            TaxonName(species.Family),
            (species.CommonNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            match.Gbif is { Id: > 0 } gbif ? (long)gbif.Id : null,
            match.Powo?.Id,
            (match.Images ?? []).Select(IdentifyReferenceImage.From).OfType<IdentifyReferenceImage>().ToList());
    }

    private static string? TaxonName(PlantNetTaxonName? taxon) =>
        string.IsNullOrEmpty(taxon?.ScientificNameWithoutAuthor) ? taxon?.ScientificName : taxon.ScientificNameWithoutAuthor;
}

public sealed record IdentifyReferenceImage(
    string? Organ,
    string ThumbnailUrl,
    string ImageUrl,
    string FullUrl,
    string? Author,
    string? License,
    string? Citation)
{
    /// <summary>Returns null when the image has no HTTPS URL. Non-HTTPS URLs are treated as absent.</summary>
    public static IdentifyReferenceImage? From(PlantNetImage? image)
    {
        var s = Https(image?.Url?.S);
        var m = Https(image?.Url?.M);
        var o = Https(image?.Url?.O);
        if (image is null || (s ?? m ?? o) is null) return null;
        // The spec's fallbacks (S ?? M, M ?? O, O ?? M), extended so every field is set when any size exists.
        return new IdentifyReferenceImage(image.Organ, (s ?? m ?? o)!, (m ?? o ?? s)!, (o ?? m ?? s)!,
            image.Author, image.License, image.Citation);
    }

    private static string? Https(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
}
