namespace AnythingCanBeFarming.Data;

public sealed class WfoIpniMapping
{
    public long Id { get; set; }
    public required string IpniId { get; set; }
    public required string NormalizedIpniId { get; set; }
    public required string WfoId { get; set; }
    public long? WfoTaxonId { get; set; }
    public long ImportId { get; set; }
}

public sealed class WfoDeprecatedName
{
    public long Id { get; set; }
    public required string WfoId { get; set; }
    public string? CanonicalName { get; set; }
    public string? AuthorsString { get; set; }
    public string? Rank { get; set; }
    public string? NomenclaturalStatus { get; set; }
    public long? WfoTaxonId { get; set; }
    public long ImportId { get; set; }
}

public sealed class WfoDeduplicatedId
{
    public long Id { get; set; }
    public required string DeprecatedWfoId { get; set; }
    public required string ReplacementWfoId { get; set; }
    public string? CanonicalName { get; set; }
    public string? AuthorsString { get; set; }
    public string? Rank { get; set; }
    public string? NomenclaturalStatus { get; set; }
    public long? DeprecatedTaxonId { get; set; }
    public long? ReplacementTaxonId { get; set; }
    public long ImportId { get; set; }
}

public static class IpniIdentifier
{
    public static string Normalize(string value)
    {
        const string prefix = "urn:lsid:ipni.org:names:";
        value = value.Trim();
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }
}
