namespace AnythingCanBeFarming.Data;

// Reference data only. WFO strings (including identifiers and HTML) are untrusted source text.
public sealed class WfoTaxon
{
    public long Id { get; set; }
    public required string TaxonId { get; set; }
    public string? ScientificNameId { get; set; }
    public string? LocalId { get; set; }
    public required string ScientificName { get; set; }
    public string? TaxonRank { get; set; }
    public string? ParentNameUsageId { get; set; }
    public string? ScientificNameAuthorship { get; set; }
    public string? Family { get; set; }
    public string? Subfamily { get; set; }
    public string? Tribe { get; set; }
    public string? Subtribe { get; set; }
    public string? Genus { get; set; }
    public string? Subgenus { get; set; }
    public string? SpecificEpithet { get; set; }
    public string? InfraspecificEpithet { get; set; }
    public string? VerbatimTaxonRank { get; set; }
    public string? NomenclaturalStatus { get; set; }
    public string? NamePublishedIn { get; set; }
    public string? TaxonomicStatus { get; set; }
    public string? AcceptedNameUsageId { get; set; }
    public string? OriginalNameUsageId { get; set; }
    public string? NameAccordingToId { get; set; }
    public string? TaxonRemarks { get; set; }
    public DateOnly? WfoCreatedAt { get; set; }
    public DateOnly? WfoModifiedAt { get; set; }
    public string? References { get; set; }
    public string? Source { get; set; }
    public string? MajorGroup { get; set; }
    public string? TplId { get; set; }
    public long ImportId { get; set; }
    public bool IsCurrent { get; set; }
    public long? ParentId { get; set; }
    public long? AcceptedTaxonId { get; set; }
    public long? OriginalTaxonId { get; set; }
    public WfoTaxon? Parent { get; set; }
    public WfoTaxon? AcceptedTaxon { get; set; }
    public WfoTaxon? OriginalTaxon { get; set; }
}
