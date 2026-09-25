namespace AnythingCanBeFarming.Data;

public sealed class WfoImport
{
    public long Id { get; set; }
    public string DatasetKind { get; set; } = "Backbone";
    public string? DatasetVersion { get; set; }
    public required string SourceFileName { get; set; }
    public required string SourceFileHash { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string Status { get; set; }
    public long RowsRead { get; set; }
    public long RowsImported { get; set; }
    public long RowsRejected { get; set; }
    public long WarningCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ValidationJson { get; set; }
}
