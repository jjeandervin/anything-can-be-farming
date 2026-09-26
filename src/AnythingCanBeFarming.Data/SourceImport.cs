namespace AnythingCanBeFarming.Data;

// Import history for every non-WFO source (Wikidata, and later USDA PLANTS, WCVP, ...).
public sealed class SourceImport
{
    public long Id { get; set; }
    public required string Source { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string Status { get; set; }
    public string? ParametersJson { get; set; }
    public long RowsRead { get; set; }
    public long RowsInserted { get; set; }
    public long RowsUpdated { get; set; }
    public long RowsRetired { get; set; }
    public long WarningCount { get; set; }
    public string? ValidationJson { get; set; }
    public string? ErrorMessage { get; set; }
}
