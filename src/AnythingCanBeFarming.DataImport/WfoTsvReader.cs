using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace AnythingCanBeFarming.DataImport;

public sealed record WfoRow(long SourceRow, object?[] Values);

public static class WfoTsvReader
{
    public static readonly string[] Headers = ["taxonID", "scientificNameID", "localID", "scientificName", "taxonRank", "parentNameUsageID", "scientificNameAuthorship", "family", "subfamily", "tribe", "subtribe", "genus", "subgenus", "specificEpithet", "infraspecificEpithet", "verbatimTaxonRank", "nomenclaturalStatus", "namePublishedIn", "taxonomicStatus", "acceptedNameUsageID", "originalNameUsageID", "nameAccordingToID", "taxonRemarks", "created", "modified", "references", "source", "majorGroup", "tplID"];
    public static readonly string[] Columns = ["TaxonId", "ScientificNameId", "LocalId", "ScientificName", "TaxonRank", "ParentNameUsageId", "ScientificNameAuthorship", "Family", "Subfamily", "Tribe", "Subtribe", "Genus", "Subgenus", "SpecificEpithet", "InfraspecificEpithet", "VerbatimTaxonRank", "NomenclaturalStatus", "NamePublishedIn", "TaxonomicStatus", "AcceptedNameUsageId", "OriginalNameUsageId", "NameAccordingToId", "TaxonRemarks", "WfoCreatedAt", "WfoModifiedAt", "References", "Source", "MajorGroup", "TplId"];
    private static readonly int[] IdentifierColumns = [0, 1, 2, 5, 19, 20, 21, 28];

    public static IEnumerable<WfoRow> Read(TextReader reader, ImportReport report, ImportDiagnostics diagnostics)
    {
        var malformed = false;
        using var parser = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = "\t", Quote = '"', Escape = '"', Mode = CsvMode.RFC4180,
            IgnoreBlankLines = false, TrimOptions = TrimOptions.None,
            // Bound damage from unterminated quotes while allowing very long source remarks.
            MaxFieldSize = 16 * 1024 * 1024,
            ExceptionMessagesContainRawData = false,
            BadDataFound = _ => malformed = true
        });
        if (!parser.Read()) throw new InvalidDataException("WFO file is empty.");
        var header = parser.Record!;
        if (malformed || !header.SequenceEqual(Headers))
            throw new InvalidDataException("Expected the 29 WFO column names in source order; the header does not match.");

        while (true)
        {
            var sourceRow = (long)parser.RawRow + 1;
            malformed = false;
            string[] fields;
            try
            {
                if (!parser.Read()) break;
                fields = parser.Record!;
            }
            catch (CsvHelperException)
            {
                report.RowsRead++;
                report.RowsRejected++;
                diagnostics.Issue(sourceRow, "error", "record", "TSV parsing failed; record boundaries cannot be recovered safely.");
                throw new InvalidDataException($"Malformed TSV at source row {sourceRow}.");
            }
            report.RowsRead++;
            if (malformed || fields.Length != Headers.Length)
            {
                report.RowsRejected++;
                diagnostics.Issue(sourceRow, "error", "record",
                    malformed ? "Malformed TSV quoting." : $"Expected 29 columns, found {fields.Length}.");
                continue;
            }

            var rejected = false;
            if (string.IsNullOrWhiteSpace(fields[0]))
            {
                report.MissingTaxonIds++;
                diagnostics.Issue(sourceRow, "error", "taxonID", "Required identifier is missing.");
                rejected = true;
            }
            if (string.IsNullOrWhiteSpace(fields[3]))
            {
                report.MissingScientificNames++;
                diagnostics.Issue(sourceRow, "error", "scientificName", "Required scientific name is missing.");
                rejected = true;
            }
            var values = new object?[Headers.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var value = fields[i];
                if (value.Contains('\0'))
                {
                    diagnostics.Issue(sourceRow, "error", Headers[i], "NUL cannot be stored in PostgreSQL text.");
                    rejected = true;
                }
                if (value.Contains('\uFFFD'))
                {
                    var identifier = IdentifierColumns.Contains(i);
                    diagnostics.Issue(sourceRow, identifier ? "error" : "warning", Headers[i],
                        "Invalid UTF-8 decoded as U+FFFD, or U+FFFD already present in source. Consult the unchanged source file.");
                    rejected |= identifier;
                }
                if (i is 23 or 24)
                {
                    if (value.Length == 0) continue;
                    if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        values[i] = date;
                    else
                        diagnostics.Issue(sourceRow, "warning", Headers[i],
                            $"Invalid date '{Abbreviate(value)}'; stored as null.", fields[0]);
                }
                else values[i] = value.Length == 0 ? null : value;
            }
            if (rejected) { report.RowsRejected++; continue; }
            report.RowsStaged++;
            yield return new WfoRow(sourceRow, values);
        }
    }

    private static string Abbreviate(string value) => value.Length <= 80 ? value : value[..80] + "…";
}
