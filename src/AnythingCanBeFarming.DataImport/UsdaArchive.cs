using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AnythingCanBeFarming.DataImport;

public sealed record UsdaTable(string Role, string RowType, string[] Terms)
{
    // The data files' header lines use each term's local name, e.g. taxonID.
    public string[] Headers { get; } = Terms.Select(x => x[(x.LastIndexOfAny(['/', '#']) + 1)..]).ToArray();
}

public sealed record UsdaArchiveFile(UsdaTable Table, string Path);

// The EOL "USDA PLANTS structured data" Darwin Core Archive. Only taxa, occurrences, and measurements are read;
// meta.xml must declare them exactly as expected, and the importer never modifies the files.
public sealed class UsdaArchive
{
    public const string MetaFile = "meta.xml";
    private static readonly XNamespace Text = "http://rs.tdwg.org/dwc/text/";

    public static UsdaTable Taxon { get; } = new("taxon", "http://rs.tdwg.org/dwc/terms/Taxon",
    [
        "http://rs.tdwg.org/dwc/terms/taxonID", "http://purl.org/dc/terms/source", "http://rs.tdwg.org/dwc/terms/scientificName",
        "http://rs.tdwg.org/dwc/terms/family", "http://rs.tdwg.org/dwc/terms/taxonRank", "http://rs.tdwg.org/dwc/terms/taxonomicStatus"
    ]);

    public static UsdaTable Occurrence { get; } = new("occurrence", "http://rs.tdwg.org/dwc/terms/Occurrence",
    [
        "http://rs.tdwg.org/dwc/terms/occurrenceID", "http://rs.tdwg.org/dwc/terms/taxonID",
        "http://eol.org/schema/terms/bodyPart", "http://rs.tdwg.org/dwc/terms/lifeStage"
    ]);

    public static UsdaTable Fact { get; } = new("measurementOrFact", "http://rs.tdwg.org/dwc/terms/MeasurementOrFact",
    [
        "http://rs.tdwg.org/dwc/terms/measurementID", "http://rs.tdwg.org/dwc/terms/occurrenceID",
        "http://eol.org/schema/measurementOfTaxon", "http://eol.org/schema/associationID", "http://eol.org/schema/parentMeasurementID",
        "http://rs.tdwg.org/dwc/terms/measurementType", "http://rs.tdwg.org/dwc/terms/measurementValue",
        "http://rs.tdwg.org/dwc/terms/measurementUnit", "http://rs.tdwg.org/dwc/terms/measurementAccuracy",
        "http://eol.org/schema/terms/statisticalMethod", "http://rs.tdwg.org/dwc/terms/measurementDeterminedDate",
        "http://rs.tdwg.org/dwc/terms/measurementDeterminedBy", "http://rs.tdwg.org/dwc/terms/measurementMethod",
        "http://rs.tdwg.org/dwc/terms/measurementRemarks", "http://purl.org/dc/terms/source",
        "http://purl.org/dc/terms/bibliographicCitation", "http://purl.org/dc/terms/contributor",
        "http://eol.org/schema/reference/referenceID"
    ]);

    // Leftover EOL template rows (a polar bear, a death cap mushroom), not USDA data.
    public static IReadOnlyList<string> IgnoredRowTypes { get; } =
        ["http://eol.org/schema/media/Document", "http://eol.org/schema/reference/Reference", "http://eol.org/schema/agent/Agent"];

    private UsdaArchive(string directory, UsdaArchiveFile taxa, UsdaArchiveFile occurrences, UsdaArchiveFile facts, List<string> ignored)
    {
        Directory = directory;
        Taxa = taxa;
        Occurrences = occurrences;
        Facts = facts;
        Ignored = ignored;
    }

    public string Directory { get; }
    public UsdaArchiveFile Taxa { get; }
    public UsdaArchiveFile Occurrences { get; }
    public UsdaArchiveFile Facts { get; }
    public IReadOnlyList<UsdaArchiveFile> Files => [Taxa, Occurrences, Facts];
    public IReadOnlyList<string> Ignored { get; }

    // The archive is the directory holding meta.xml: the given directory itself, or its only descendant that has one.
    public static string Discover(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
            throw new FileNotFoundException($"Extract the USDA archive under {directory} or use --directory.");
        if (File.Exists(Path.Combine(directory, MetaFile))) return directory;
        var candidates = System.IO.Directory.EnumerateFiles(directory, MetaFile, SearchOption.AllDirectories).Take(2).ToArray();
        return candidates.Length switch
        {
            1 => Path.GetDirectoryName(candidates[0])!,
            0 => throw new FileNotFoundException($"No {MetaFile} found under {directory}. Extract usda_plant_traits.tar.gz there or use --directory."),
            _ => throw new InvalidOperationException($"More than one {MetaFile} found under {directory}. Select one archive with --directory.")
        };
    }

    public static UsdaArchive Open(string directory)
    {
        var metaPath = Path.Combine(directory, MetaFile);
        if (!File.Exists(metaPath)) throw new FileNotFoundException($"{MetaFile} is missing from {directory}.");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(metaPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"{MetaFile} is not well-formed XML (line {exception.LineNumber}).");
        }
        if (document.Root?.Name != Text + "archive")
            throw new InvalidDataException($"{MetaFile} is not a Darwin Core Archive descriptor.");
        var tables = document.Root.Elements().Where(x => x.Name.Namespace == Text && x.Name.LocalName is "core" or "extension" or "table").ToList();
        var ignored = new List<string>();
        foreach (var table in tables)
        {
            var rowType = (string?)table.Attribute("rowType");
            if (rowType == null || rowType == Taxon.RowType || rowType == Occurrence.RowType || rowType == Fact.RowType) continue;
            var location = Location(table) ?? "(no file)";
            var known = IgnoredRowTypes.Contains(rowType) ? "EOL template rows" : "unused row type";
            ignored.Add($"{location} ({rowType}): {known}, {(File.Exists(Path.Combine(directory, location)) ? "present" : "not present")}");
        }
        return new UsdaArchive(directory, Declared(Taxon), Declared(Occurrence), Declared(Fact), ignored);

        UsdaArchiveFile Declared(UsdaTable expected)
        {
            var matches = tables.Where(x => (string?)x.Attribute("rowType") == expected.RowType).ToList();
            if (matches.Count != 1)
                throw new InvalidDataException($"{MetaFile} must declare exactly one {expected.RowType} table; found {matches.Count}.");
            var table = matches[0];
            foreach (var (attribute, value) in new[]
            {
                ("encoding", "UTF-8"), ("fieldsTerminatedBy", "\\t"), ("linesTerminatedBy", "\\n"), ("ignoreHeaderLines", "1")
            })
                if (!string.Equals((string?)table.Attribute(attribute), value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{MetaFile}: the {expected.Role} table must have {attribute}=\"{value}\".");
            if (!string.IsNullOrEmpty((string?)table.Attribute("fieldsEnclosedBy")))
                throw new InvalidDataException($"{MetaFile}: the {expected.Role} table declares quoted fields, which this importer does not expect.");
            var location = Location(table) ?? throw new InvalidDataException($"{MetaFile}: the {expected.Role} table has no file location.");
            if (location != Path.GetFileName(location) || location.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"{MetaFile}: the {expected.Role} file location must be a plain file name.");
            var fields = table.Elements(Text + "field").ToList();
            var terms = new string?[fields.Count];
            foreach (var field in fields)
            {
                if (!int.TryParse((string?)field.Attribute("index"), out var index) || index < 0 || index >= fields.Count || terms[index] != null)
                    throw new InvalidDataException($"{MetaFile}: the {expected.Role} table's field indexes must be 0 to {fields.Count - 1}, each used once.");
                terms[index] = (string?)field.Attribute("term");
            }
            if (!terms.SequenceEqual(expected.Terms))
                throw new InvalidDataException($"{MetaFile}: the {expected.Role} columns differ from the expected {expected.Terms.Length} terms in order " +
                    $"({string.Join(", ", expected.Headers)}).");
            var path = Path.Combine(directory, location);
            if (!File.Exists(path)) throw new FileNotFoundException($"The {expected.Role} file {location} declared in {MetaFile} is missing.");
            return new(expected, path);
        }
    }

    private static string? Location(XElement table) => table.Element(Text + "files")?.Element(Text + "location")?.Value.Trim();

    // Combined over the per-file hashes in a fixed order: taxon, occurrence, measurementOrFact.
    public static string CombinedHash(IReadOnlyList<(string Role, string Hash)> files) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(files.Select(x => $"{x.Role}:{x.Hash}\n")))));
}
