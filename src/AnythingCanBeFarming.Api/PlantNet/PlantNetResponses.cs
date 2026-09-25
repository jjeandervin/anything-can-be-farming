using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnythingCanBeFarming.Api.PlantNet;

// Response contracts from the supplied My Pl@ntNet API 2.2.2 Swagger definitions.
// Optional fields remain nullable; the quota endpoint leaves its object schema open.

public sealed class PlantNetStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class PlantNetDisease
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; init; }
}

public sealed class PlantNetProject
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("speciesCount")]
    public double? SpeciesCount { get; init; }
}

public sealed class PlantNetQuotaResponse
{
    [JsonPropertyName("quota")]
    public Dictionary<string, JsonElement>? Quota { get; init; }
}

public sealed class PlantNetImageDate
{
    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }

    [JsonPropertyName("string")]
    public string? String { get; init; }
}

public sealed class PlantNetImageUrls
{
    [JsonPropertyName("o")]
    public string? O { get; init; }

    [JsonPropertyName("m")]
    public string? M { get; init; }

    [JsonPropertyName("s")]
    public string? S { get; init; }
}

public sealed class PlantNetImage
{
    [JsonPropertyName("organ")]
    public string? Organ { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("date")]
    public PlantNetImageDate? Date { get; init; }

    [JsonPropertyName("citation")]
    public string? Citation { get; init; }

    [JsonPropertyName("url")]
    public PlantNetImageUrls? Url { get; init; }
}

public sealed class PlantNetSpecies
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("scientificNameWithoutAuthor")]
    public string? ScientificNameWithoutAuthor { get; init; }

    [JsonPropertyName("scientificNameAuthorship")]
    public string? ScientificNameAuthorship { get; init; }

    [JsonPropertyName("gbifId")]
    public double? GbifId { get; init; }

    [JsonPropertyName("powoId")]
    public string? PowoId { get; init; }

    [JsonPropertyName("iucnCategory")]
    public string? IucnCategory { get; init; }

    [JsonPropertyName("commonNames")]
    public List<string>? CommonNames { get; init; }

    [JsonPropertyName("genus")]
    public string? Genus { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }
}

public sealed class PlantNetAccountName
{
    [JsonPropertyName("first")]
    public string? First { get; init; }

    [JsonPropertyName("last")]
    public string? Last { get; init; }
}

public sealed class PlantNetAccount
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("name")]
    public PlantNetAccountName? Name { get; init; }

    [JsonPropertyName("created")]
    public string? Created { get; init; }
}

public sealed class PlantNetContract
{
    [JsonPropertyName("plan")]
    public string? Plan { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("firstSignatureDate")]
    public string? FirstSignatureDate { get; init; }

    [JsonPropertyName("latestSignatureDate")]
    public string? LatestSignatureDate { get; init; }

    [JsonPropertyName("nextSignatureDate")]
    public string? NextSignatureDate { get; init; }

    [JsonPropertyName("indicativeYearlyQuota")]
    public double? IndicativeYearlyQuota { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

public sealed class PlantNetRequestCount
{
    [JsonPropertyName("identify")]
    public double? Identify { get; init; }
}

public sealed class PlantNetSubscriptionPeriod
{
    [JsonPropertyName("period")]
    public string? Period { get; init; }

    [JsonPropertyName("startDate")]
    public string? StartDate { get; init; }

    [JsonPropertyName("endDate")]
    public string? EndDate { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("count")]
    public PlantNetRequestCount? Count { get; init; }

    [JsonPropertyName("aboveQuota")]
    public PlantNetRequestCount? AboveQuota { get; init; }

    [JsonPropertyName("discount")]
    public bool? Discount { get; init; }
}

public sealed class PlantNetBilling
{
    [JsonPropertyName("nextDueDate")]
    public string? NextDueDate { get; init; }

    [JsonPropertyName("estimatedAmount")]
    public double? EstimatedAmount { get; init; }
}

public sealed class PlantNetSecurity
{
    [JsonPropertyName("exposeKey")]
    public bool? ExposeKey { get; init; }

    [JsonPropertyName("ips")]
    public List<string>? Ips { get; init; }

    [JsonPropertyName("domains")]
    public List<string>? Domains { get; init; }
}

public sealed class PlantNetSubscription
{
    [JsonPropertyName("account")]
    public PlantNetAccount? Account { get; init; }

    [JsonPropertyName("contract")]
    public PlantNetContract? Contract { get; init; }

    [JsonPropertyName("history")]
    public List<PlantNetSubscriptionPeriod>? History { get; init; }

    [JsonPropertyName("billing")]
    public PlantNetBilling? Billing { get; init; }

    [JsonPropertyName("security")]
    public PlantNetSecurity? Security { get; init; }
}

public sealed class PlantNetGbif
{
    [JsonPropertyName("id")]
    public double Id { get; init; }
}

public sealed class PlantNetVarietySpecies
{
    [JsonPropertyName("scientificName")]
    public string? ScientificName { get; init; }

    [JsonPropertyName("scientificNameWithoutAuthor")]
    public string? ScientificNameWithoutAuthor { get; init; }

    [JsonPropertyName("scientificNameAuthorship")]
    public string? ScientificNameAuthorship { get; init; }

    [JsonPropertyName("genus")]
    public string? Genus { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("commonNames")]
    public List<string>? CommonNames { get; init; }

    [JsonPropertyName("gbif")]
    public PlantNetGbif? Gbif { get; init; }
}

public sealed class PlantNetVariety
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("species")]
    public PlantNetVarietySpecies? Species { get; init; }
}

public sealed class PlantNetIdentificationQuery
{
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("images")]
    public List<string>? Images { get; init; }

    [JsonPropertyName("organs")]
    public List<string>? Organs { get; init; }

    [JsonPropertyName("includeRelatedImages")]
    public bool? IncludeRelatedImages { get; init; }

    [JsonPropertyName("noReject")]
    public bool? NoReject { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class PlantNetTaxonName
{
    [JsonPropertyName("scientificNameWithoutAuthor")]
    public string? ScientificNameWithoutAuthor { get; init; }

    [JsonPropertyName("scientificNameAuthorship")]
    public string? ScientificNameAuthorship { get; init; }

    [JsonPropertyName("scientificName")]
    public string? ScientificName { get; init; }
}

public sealed class PlantNetIdentifiedSpecies
{
    [JsonPropertyName("scientificNameWithoutAuthor")]
    public string? ScientificNameWithoutAuthor { get; init; }

    [JsonPropertyName("scientificNameAuthorship")]
    public string? ScientificNameAuthorship { get; init; }

    [JsonPropertyName("scientificName")]
    public string? ScientificName { get; init; }

    [JsonPropertyName("genus")]
    public PlantNetTaxonName? Genus { get; init; }

    [JsonPropertyName("family")]
    public PlantNetTaxonName? Family { get; init; }

    [JsonPropertyName("commonNames")]
    public List<string>? CommonNames { get; init; }
}

public sealed class PlantNetPowo
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

public sealed class PlantNetIucn
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }
}

public sealed class PlantNetMatch
{
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("species")]
    public PlantNetIdentifiedSpecies? Species { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }

    [JsonPropertyName("gbif")]
    public PlantNetGbif? Gbif { get; init; }

    [JsonPropertyName("powo")]
    public PlantNetPowo? Powo { get; init; }

    [JsonPropertyName("iucn")]
    public PlantNetIucn? Iucn { get; init; }
}

public sealed class PlantNetPredictedOrgan
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("organ")]
    public string? Organ { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }
}

public sealed class PlantNetIdentifiedGenus
{
    [JsonPropertyName("scientificName")]
    public string? ScientificName { get; init; }

    [JsonPropertyName("family")]
    public PlantNetTaxonName? Family { get; init; }

    [JsonPropertyName("commonNames")]
    public List<string>? CommonNames { get; init; }
}

public sealed class PlantNetGenusMatch
{
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("genus")]
    public PlantNetIdentifiedGenus? Genus { get; init; }

    [JsonPropertyName("gbif")]
    public PlantNetGbif? Gbif { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }
}

public sealed class PlantNetIdentifiedFamily
{
    [JsonPropertyName("scientificName")]
    public string? ScientificName { get; init; }

    [JsonPropertyName("commonNames")]
    public List<string>? CommonNames { get; init; }
}

public sealed class PlantNetFamilyMatch
{
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("family")]
    public PlantNetIdentifiedFamily? Family { get; init; }

    [JsonPropertyName("gbif")]
    public PlantNetGbif? Gbif { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }
}

public sealed class PlantNetOtherResults
{
    [JsonPropertyName("genus")]
    public List<PlantNetGenusMatch>? Genus { get; init; }

    [JsonPropertyName("family")]
    public List<PlantNetFamilyMatch>? Family { get; init; }
}

public sealed class PlantNetIdentificationResult
{
    [JsonPropertyName("query")]
    public PlantNetIdentificationQuery? Query { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("preferedReferential")]
    public string? PreferredReferential { get; init; }

    [JsonPropertyName("switchToProject")]
    public string? SwitchToProject { get; init; }

    [JsonPropertyName("bestMatch")]
    public string? BestMatch { get; init; }

    [JsonPropertyName("results")]
    public List<PlantNetMatch>? Results { get; init; }

    [JsonPropertyName("remainingIdentificationRequests")]
    public double? RemainingIdentificationRequests { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("predictedOrgans")]
    public List<PlantNetPredictedOrgan>? PredictedOrgans { get; init; }

    [JsonPropertyName("otherResults")]
    public PlantNetOtherResults? OtherResults { get; init; }
}

public sealed class PlantNetQuotaUsage
{
    [JsonPropertyName("count")]
    public double? Count { get; init; }

    [JsonPropertyName("total")]
    public double? Total { get; init; }

    [JsonPropertyName("remaining")]
    public double? Remaining { get; init; }
}

public sealed class PlantNetDailyQuotaCounts
{
    [JsonPropertyName("identify")]
    public PlantNetQuotaUsage? Identify { get; init; }
}

public sealed class PlantNetDailyQuota
{
    [JsonPropertyName("day")]
    public string? Day { get; init; }

    [JsonPropertyName("quota")]
    public PlantNetDailyQuotaCounts? Quota { get; init; }
}

public sealed class PlantNetEmbeddingsQuery
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }
}

public sealed class PlantNetEmbeddingsResult
{
    [JsonPropertyName("query")]
    public PlantNetEmbeddingsQuery? Query { get; init; }

    [JsonPropertyName("embeddings")]
    public List<double>? Embeddings { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed class PlantNetImageQuery
{
    [JsonPropertyName("images")]
    public List<string>? Images { get; init; }

    [JsonPropertyName("organs")]
    public List<string>? Organs { get; init; }

    [JsonPropertyName("includeRelatedImages")]
    public bool? IncludeRelatedImages { get; init; }

    [JsonPropertyName("noReject")]
    public bool? NoReject { get; init; }
}

public sealed class PlantNetDiseaseMatch
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }
}

public sealed class PlantNetDiseaseIdentificationResult
{
    [JsonPropertyName("query")]
    public PlantNetImageQuery? Query { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("results")]
    public List<PlantNetDiseaseMatch>? Results { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("remainingIdentificationRequests")]
    public double? RemainingIdentificationRequests { get; init; }
}

public sealed class PlantNetVarietyMatch
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }
}

public sealed class PlantNetSpeciesVarietyMatch
{
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("species")]
    public PlantNetIdentifiedSpecies? Species { get; init; }

    [JsonPropertyName("images")]
    public List<PlantNetImage>? Images { get; init; }

    [JsonPropertyName("gbif")]
    public PlantNetGbif? Gbif { get; init; }

    [JsonPropertyName("powo")]
    public PlantNetPowo? Powo { get; init; }

    [JsonPropertyName("iucn")]
    public PlantNetIucn? Iucn { get; init; }

    [JsonPropertyName("varieties")]
    public List<PlantNetVarietyMatch>? Varieties { get; init; }
}

public sealed class PlantNetVarietyIdentificationResult
{
    [JsonPropertyName("query")]
    public PlantNetImageQuery? Query { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("results")]
    public List<PlantNetSpeciesVarietyMatch>? Results { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("remainingIdentificationRequests")]
    public double? RemainingIdentificationRequests { get; init; }
}
