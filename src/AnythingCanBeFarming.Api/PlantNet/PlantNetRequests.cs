namespace AnythingCanBeFarming.Api.PlantNet;

/// <summary>An image uploaded as a multipart file. Content must be JPEG or PNG.</summary>
public sealed record PlantNetImageUpload(byte[] Content, string FileName, string ContentType = "image/jpeg");

public class PlantNetIdentificationOptions
{
    public string? Language { get; init; }
    public bool? IncludeRelatedImages { get; init; }
    public bool? NoReject { get; init; }
    public int? NumberOfResults { get; init; }
    /// <summary>When supplied, one organ per image; otherwise the API uses auto.</summary>
    public IReadOnlyList<string>? Organs { get; init; }
}

public sealed class PlantNetPlantIdentificationOptions : PlantNetIdentificationOptions
{
    public string? Type { get; init; }
    public bool? Detailed { get; init; }
}

public sealed class PlantNetSpeciesOptions
{
    public string? Language { get; init; }
    /// <summary>Set Page and PageSize together. Empty strings disable pagination.</summary>
    public string? Page { get; init; }
    public string? PageSize { get; init; }
    public string? Prefix { get; init; }
    public bool? Images { get; init; }
}
