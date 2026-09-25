using System.Globalization;
using System.Text;
using AnythingCanBeFarming.Data;
using CsvHelper;
using CsvHelper.Configuration;

namespace AnythingCanBeFarming.DataImport;

public enum WfoSupplementalKind { Ipni, Deprecated, Deduplicated }

public sealed class SupplementalReport
{
    public long RowsRead { get; set; }
    public long RowsImported { get; set; }
    public long RowsRejected { get; set; }
    public List<string> Warnings { get; } = [];
    public Dictionary<string, long> Statistics { get; } = [];
}

public static class WfoSupplementalSource
{
    private static readonly string[] NameHeaders = ["wfo_id", "name_canonical", "authors_string", "rank", "nomenclatural_status"];

    public static string Discover(string directory, WfoSupplementalKind kind)
    {
        if (!Directory.Exists(directory))
            throw new FileNotFoundException($"Supplemental source directory does not exist: {directory}. Use --directory to select a staged package.");
        var suffix = kind switch
        {
            WfoSupplementalKind.Ipni => "ipni_to_wfo.csv",
            WfoSupplementalKind.Deprecated => "deprecated_names_lookup.csv",
            _ => "deduplicated_ids_lookup.csv"
        };
        var matches = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(x => Path.GetFileName(x).Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(x).EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"No supplemental file matching *_{suffix} under {directory}."),
            _ => throw new InvalidDataException($"Ambiguous supplemental source *_{suffix}: {string.Join(", ", matches)}. Select a single package with --directory.")
        };
    }

    public static string? Version(string path, string? versionOverride = null)
    {
        var adjacent = WfoSource.Version(path, versionOverride);
        if (adjacent != null) return adjacent;
        // This is the inspected package layout, not an arbitrary ancestor's metadata.
        return WfoSource.Version(Path.Combine(Path.GetDirectoryName(path)!, "backbone", "classification.csv"));
    }

    public static IEnumerable<string?[]> Read(TextReader reader, WfoSupplementalKind kind, SupplementalReport report)
    {
        var malformed = false;
        using var parser = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",", Mode = CsvMode.RFC4180, IgnoreBlankLines = false,
            TrimOptions = TrimOptions.None, MaxFieldSize = 16 * 1024 * 1024,
            ExceptionMessagesContainRawData = false, BadDataFound = _ => malformed = true
        });
        string[] header;
        try
        {
            if (!parser.Read()) throw new InvalidDataException("Supplemental CSV is empty.");
            header = parser.Record!;
        }
        catch (Exception exception) when (exception is CsvHelperException or DecoderFallbackException)
        {
            throw new InvalidDataException($"Malformed {kind} CSV header or invalid UTF-8.", exception);
        }
        header[0] = header[0].TrimStart('\uFEFF');
        var expected = kind == WfoSupplementalKind.Ipni ? new[] { "ipni_id", "wfo_id" } : NameHeaders;
        var legacyDedupe = kind == WfoSupplementalKind.Deduplicated && header.SequenceEqual(NameHeaders);
        if (kind == WfoSupplementalKind.Deduplicated && !legacyDedupe)
            expected = ["wfo_id", "replacement_wfo_id", "name_canonical", "authors_string", "rank", "nomenclatural_status"];
        if (malformed || !header.SequenceEqual(expected))
            throw new InvalidDataException($"Incompatible {kind} CSV header. Expected: {string.Join(',', expected)}.");
        if (legacyDedupe)
            report.Warnings.Add("Known WFO source quirk: five-column deduplication header omits replacement_wfo_id at position 2; each data row must contain six fields. Source is unchanged.");

        var width = kind == WfoSupplementalKind.Ipni ? 2 : kind == WfoSupplementalKind.Deprecated ? 5 : 6;
        while (true)
        {
            var line = parser.RawRow + 1;
            malformed = false;
            string[] fields;
            try
            {
                if (!parser.Read()) break;
                fields = parser.Record!;
            }
            catch (Exception exception) when (exception is CsvHelperException or DecoderFallbackException)
            {
                report.RowsRead++;
                report.RowsRejected++;
                throw new InvalidDataException($"Malformed {kind} CSV or invalid UTF-8 at source line {line}.", exception);
            }
            report.RowsRead++;
            var invalid = malformed || fields.Length != width || fields.Any(x => x.Contains('\0'));
            if (!invalid)
            {
                var wfoIndices = kind switch { WfoSupplementalKind.Ipni => new[] { 1 }, WfoSupplementalKind.Deprecated => [0], _ => [0, 1] };
                invalid = wfoIndices.Any(i => !IsWfoId(fields[i]));
                if (kind == WfoSupplementalKind.Ipni)
                    invalid |= string.IsNullOrWhiteSpace(IpniIdentifier.Normalize(fields[0])) || fields[0].Contains('\uFFFD');
            }
            if (invalid)
            {
                report.RowsRejected++;
                throw new InvalidDataException($"Incompatible {kind} CSV at source line {line}: expected {width} fields, valid required IDs, and valid CSV text; found {fields.Length} fields.");
            }
            var values = fields.Select(x => x.Length == 0 ? null : x).ToList();
            if (kind == WfoSupplementalKind.Ipni) values.Add(IpniIdentifier.Normalize(fields[0]));
            yield return values.ToArray();
        }
    }

    private static bool IsWfoId(string value) => value.StartsWith("wfo-", StringComparison.Ordinal) &&
        value.Length > 4 && !value.Any(char.IsWhiteSpace) && !value.Contains('\uFFFD');
}
