// Generates data/reference/place-labels.csv from GeoNames' admin1CodesASCII.txt and countryInfo.txt (CC BY 4.0,
// https://download.geonames.org/export/dump/) plus the place IDs used in the USDA archive.
//
//   dotnet run tools/generate-place-labels.cs -- <admin1CodesASCII.txt> <countryInfo.txt> <usda archive directory> <output csv>
//
// Includes every US state and DC, every Canadian province and territory, and the US territories, countries, and
// regions whose IDs appear in the archive's distribution facts. A few IDs the dumps do not cover are listed by hand below.
using System.Globalization;
using System.Text;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: dotnet run tools/generate-place-labels.cs -- <admin1CodesASCII.txt> <countryInfo.txt> <archive dir> <output csv>");
    return 1;
}
var (admin1Path, countryPath, archive, outputPath) = (args[0], args[1], args[2], args[3]);

// Place IDs used by Present, NativeRange, and IntroducedRange facts, with the last URI segment as the ID.
string[] distributionTypes = ["http://eol.org/schema/terms/Present", "http://eol.org/schema/terms/NativeRange", "http://eol.org/schema/terms/IntroducedRange"];
var used = new HashSet<string>(StringComparer.Ordinal);
foreach (var line in File.ReadLines(Path.Combine(archive, "measurement_or_fact_specific.tab")).Skip(1))
{
    var fields = line.Split('\t');
    if (fields.Length > 6 && distributionTypes.Contains(fields[5]) && fields[6].StartsWith("http"))
        used.Add(fields[6][(fields[6].LastIndexOfAny(['/', '#']) + 1)..]);
}

// US territories are countries in GeoNames; label them as territories.
string[] usTerritories = ["AS", "GU", "MP", "PR", "UM", "VI"];
string[] canadianTerritories = ["NT", "NU", "YT"];
var rows = new List<string[]>();
foreach (var line in File.ReadLines(admin1Path))
{
    // US.OH <tab> Ohio <tab> Ohio <tab> 5165418
    var fields = line.Split('\t');
    var code = fields[0].Split('.');
    if (fields.Length < 4 || code.Length != 2 || code[0] is not ("US" or "CA")) continue;
    var kind = code[0] == "US" ? "state" : canadianTerritories.Contains(code[1]) ? "territory" : "province";
    rows.Add(["geonames", fields[3], fields[1], kind, code[0], code[1]]);
}
foreach (var line in File.ReadLines(countryPath).Where(x => !x.StartsWith('#')))
{
    // ISO, ISO3, ISO-Numeric, fips, Country, ..., geonameid at index 16
    var fields = line.Split('\t');
    if (fields.Length < 17 || !used.Contains(fields[16])) continue;
    rows.Add(["geonames", fields[16], fields[4], usTerritories.Contains(fields[0]) ? "territory" : "country", fields[0], ""]);
}
// Not in either dump; names from the GeoNames feature pages (checked 2026-09-26) and Wikidata.
rows.Add(["geonames", "3577322", "Virgin Islands", "region", "", ""]);
rows.Add(["geonames", "6255149", "North America", "region", "", ""]);
rows.Add(["wikidata", "Q578170", "Contiguous United States", "region", "US", ""]);
rows.Add(["wikidata", "Q797", "Alaska", "state", "US", "AK"]);

var missing = used.Where(id => !rows.Any(x => x[1] == id)).Order().ToList();
if (missing.Count > 0) Console.Error.WriteLine($"No label for place IDs used in the archive: {string.Join(", ", missing)}");

var output = new StringBuilder("scheme,place_id,name,kind,country_code,admin_code\n");
foreach (var row in rows.DistinctBy(x => (x[0], x[1])).OrderBy(x => x[0], StringComparer.Ordinal)
    .ThenBy(x => x[4], StringComparer.Ordinal).ThenBy(x => x[5], StringComparer.Ordinal)
    .ThenBy(x => long.TryParse(x[1].TrimStart('Q'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue))
    output.Append(string.Join(',', row.Select(Csv))).Append('\n');
File.WriteAllText(outputPath, output.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Wrote {rows.Count} place labels to {outputPath}.");
return missing.Count > 0 ? 2 : 0;

static string Csv(string value) =>
    value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
