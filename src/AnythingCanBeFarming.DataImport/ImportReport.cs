using System.Text.Json;

namespace AnythingCanBeFarming.DataImport;

public sealed class ImportReport
{
    public long RowsRead { get; set; }
    public long RowsStaged { get; set; }
    public long RowsImported { get; set; }
    public long RowsRejected { get; set; }
    public long WarningCount { get; set; }
    public long MissingTaxonIds { get; set; }
    public long MissingScientificNames { get; set; }
    public long UniqueTaxonIds { get; set; }
    public long DuplicateTaxonIds { get; set; }
    public long DuplicateRows { get; set; }
    public long UniqueFamilies { get; set; }
    public long UniqueGenera { get; set; }
    public Dictionary<string, List<ValueCount>> Groups { get; } = [];
    public Dictionary<string, RelationshipCount> Relationships { get; } = [];
}

public sealed record ValueCount(string? Value, long Count);
public sealed record RelationshipCount(long Resolved, long Unresolved);
public sealed record ImportResult(long ImportId, bool AlreadyImported, ImportReport Report);

// Stream all diagnostics to disk; do not accumulate remarks or millions of warnings in memory.
public sealed class ImportDiagnostics(TextWriter writer, ImportReport report)
{
    public void Issue(long? row, string severity, string field, string message, string? taxonId = null)
    {
        if (severity == "warning") report.WarningCount++;
        writer.WriteLine(JsonSerializer.Serialize(new { row, severity, field, taxonId, message }));
    }
}
