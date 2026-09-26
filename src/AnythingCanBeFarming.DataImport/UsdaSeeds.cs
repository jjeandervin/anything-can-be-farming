using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record TraitTypeSeed(string TypeUri, string Key, string Label, string ValueKind, string? Notes);
public sealed record CodeLabelSeed(string Code, string? TypeUri, string? Label, int? Ordinal, string Confidence, string? Evidence);
public sealed record PlaceLabelSeed(string Scheme, string PlaceId, string Name, string Kind, string? CountryCode, string? AdminCode);
public sealed record SeedTableSync(long Rows, long Inserted, long Updated, long Deleted);

// The committed, human-reviewed mapping files under data/reference/. The importer mirrors them into the database
// on every run (even when the data import itself is skipped), so edits show up in Git diffs.
public sealed partial class UsdaSeeds
{
    public const string TraitTypesFile = "usda-trait-types.csv";
    public const string CodeLabelsFile = "usda-code-labels.csv";
    public const string PlaceLabelsFile = "place-labels.csv";
    public static readonly string[] ValueKinds = ["coded", "numeric", "literal", "multi"];
    public static readonly string[] Confidences = ["verified", "inferred", "unresolved"];
    public static readonly string[] PlaceSchemes = ["geonames", "wikidata"];
    public static readonly string[] PlaceKinds = ["state", "province", "territory", "country", "region"];

    public required IReadOnlyList<TraitTypeSeed> TraitTypes { get; init; }
    public required IReadOnlyList<CodeLabelSeed> CodeLabels { get; init; }
    public required IReadOnlyList<PlaceLabelSeed> PlaceLabels { get; init; }

    public static string DefaultDirectory(string repositoryRoot) => Path.Combine(repositoryRoot, "data", "reference");

    public static UsdaSeeds Load(string directory)
    {
        var errors = new List<string>();
        var traitTypes = Read(directory, TraitTypesFile, ["type_uri", "key", "label", "value_kind", "notes"], errors,
            (row, at) => new TraitTypeSeed(Required(row[0], "type_uri", at, errors), Required(row[1], "key", at, errors),
                Required(row[2], "label", at, errors), Required(row[3], "value_kind", at, errors), Optional(row[4])));
        var codeLabels = Read(directory, CodeLabelsFile, ["code", "type_uri", "label", "ordinal", "confidence", "evidence"], errors,
            (row, at) => new CodeLabelSeed(Required(row[0], "code", at, errors), Optional(row[1]), Optional(row[2]),
                Ordinal(row[3], at, errors), Required(row[4], "confidence", at, errors), Optional(row[5])));
        var placeLabels = Read(directory, PlaceLabelsFile, ["scheme", "place_id", "name", "kind", "country_code", "admin_code"], errors,
            (row, at) => new PlaceLabelSeed(Required(row[0], "scheme", at, errors), Required(row[1], "place_id", at, errors),
                Required(row[2], "name", at, errors), Required(row[3], "kind", at, errors), Optional(row[4]), Optional(row[5])));

        var types = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (seed, line) in traitTypes)
        {
            if (!types.Add(seed.TypeUri)) errors.Add($"{TraitTypesFile}:{line}: duplicate type_uri {seed.TypeUri}.");
            if (!keys.Add(seed.Key)) errors.Add($"{TraitTypesFile}:{line}: duplicate key {seed.Key}.");
            if (!KeyPattern().IsMatch(seed.Key)) errors.Add($"{TraitTypesFile}:{line}: key {seed.Key} must be snake_case.");
            if (!ValueKinds.Contains(seed.ValueKind)) errors.Add($"{TraitTypesFile}:{line}: value_kind must be one of {string.Join(", ", ValueKinds)}.");
            if (UsdaValues.DistributionKind(seed.TypeUri) != null) errors.Add($"{TraitTypesFile}:{line}: distribution types are not traits.");
        }
        var codes = new HashSet<(string, string?)>();
        foreach (var (seed, line) in codeLabels)
        {
            if (!codes.Add((seed.Code, seed.TypeUri)))
                errors.Add($"{CodeLabelsFile}:{line}: duplicate code {seed.Code} for {seed.TypeUri ?? "all traits"}.");
            if (!Confidences.Contains(seed.Confidence)) errors.Add($"{CodeLabelsFile}:{line}: confidence must be one of {string.Join(", ", Confidences)}.");
            else if ((seed.Label == null) != (seed.Confidence == "unresolved"))
                errors.Add($"{CodeLabelsFile}:{line}: a label is required unless confidence is unresolved, and must be empty when it is.");
            if (seed.TypeUri != null && !types.Contains(seed.TypeUri)) errors.Add($"{CodeLabelsFile}:{line}: type_uri {seed.TypeUri} is not a trait type.");
        }
        var places = new HashSet<(string, string)>();
        foreach (var (seed, line) in placeLabels)
        {
            if (!places.Add((seed.Scheme, seed.PlaceId))) errors.Add($"{PlaceLabelsFile}:{line}: duplicate place {seed.Scheme}:{seed.PlaceId}.");
            if (!PlaceSchemes.Contains(seed.Scheme)) errors.Add($"{PlaceLabelsFile}:{line}: scheme must be one of {string.Join(", ", PlaceSchemes)}.");
            if (!PlaceKinds.Contains(seed.Kind)) errors.Add($"{PlaceLabelsFile}:{line}: kind must be one of {string.Join(", ", PlaceKinds)}.");
        }
        if (errors.Count > 0)
            throw new InvalidDataException($"Seed files under {directory} are invalid; nothing was imported:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Take(20)) + (errors.Count > 20 ? $"{Environment.NewLine}…and {errors.Count - 20} more." : ""));
        return new UsdaSeeds
        {
            TraitTypes = traitTypes.Select(x => x.Seed).ToList(),
            CodeLabels = codeLabels.Select(x => x.Seed).ToList(),
            PlaceLabels = placeLabels.Select(x => x.Seed).ToList()
        };
    }

    private static List<(T Seed, long Line)> Read<T>(string directory, string file, string[] header, List<string> errors,
        Func<string[], string, T> create)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"Seed file {file} is missing from {directory}.");
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        using var parser = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Mode = CsvMode.RFC4180, TrimOptions = TrimOptions.None, IgnoreBlankLines = true, ExceptionMessagesContainRawData = false
        });
        if (!parser.Read() || !parser.Record!.SequenceEqual(header))
            throw new InvalidDataException($"{file} must start with the header {string.Join(",", header)}.");
        var rows = new List<(T, long)>();
        while (parser.Read())
        {
            var line = (long)parser.RawRow;
            var record = parser.Record!;
            if (record.Length != header.Length)
            {
                errors.Add($"{file}:{line}: expected {header.Length} fields, found {record.Length}.");
                continue;
            }
            rows.Add((create(record, $"{file}:{line}"), line));
        }
        return rows;
    }

    private static string Required(string value, string field, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{at}: {field} is required.");
        return value;
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    private static int? Ordinal(string value, string at, List<string> errors)
    {
        if (value.Length == 0) return null;
        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ordinal)) return ordinal;
        errors.Add($"{at}: ordinal must be a whole number.");
        return null;
    }

    // Makes each mapping table exactly match its seed file. Rows are rewritten only when they changed.
    public async Task<Dictionary<string, SeedTableSync>> SyncAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = new Dictionary<string, SeedTableSync>
        {
            ["usda_trait_type"] = await SyncTableAsync(connection, "usda_trait_type", ["TypeUri"], ["Key", "Label", "ValueKind", "Notes"],
                TraitTypes.Select(x => new[] { x.TypeUri, x.Key, x.Label, x.ValueKind, x.Notes }), cancellationToken),
            ["usda_code_label"] = await SyncTableAsync(connection, "usda_code_label", ["Code", "TypeUri"], ["Label", "Ordinal", "Confidence", "Evidence"],
                CodeLabels.Select(x => new[] { x.Code, x.TypeUri, x.Label, x.Ordinal?.ToString(CultureInfo.InvariantCulture), x.Confidence, x.Evidence }),
                cancellationToken, integerColumn: "Ordinal"),
            ["place_label"] = await SyncTableAsync(connection, "place_label", ["Scheme", "PlaceId"], ["Name", "Kind", "CountryCode", "AdminCode"],
                PlaceLabels.Select(x => new[] { x.Scheme, x.PlaceId, x.Name, x.Kind, x.CountryCode, x.AdminCode }), cancellationToken)
        };
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<SeedTableSync> SyncTableAsync(NpgsqlConnection connection, string table, string[] keys, string[] values,
        IEnumerable<string?[]> rows, CancellationToken cancellationToken, string? integerColumn = null)
    {
        var columns = keys.Concat(values).ToArray();
        var stage = $"{table}_seed";
        string Type(string column) => column == integerColumn ? "integer" : "text";
        await ExecuteAsync(connection, $"""
            DROP TABLE IF EXISTS {stage};
            CREATE TEMP TABLE {stage} ({string.Join(", ", columns.Select(x => $"\"{x}\" {Type(x)}"))}) ON COMMIT DROP;
            """, cancellationToken);
        long count = 0;
        await using (var copy = await connection.BeginBinaryImportAsync(
            $"COPY {stage} ({string.Join(", ", columns.Select(x => $"\"{x}\""))}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var row in rows)
            {
                count++;
                await copy.StartRowAsync(cancellationToken);
                for (var i = 0; i < columns.Length; i++)
                {
                    if (row[i] == null) await copy.WriteNullAsync(cancellationToken);
                    else if (columns[i] == integerColumn) await copy.WriteAsync(int.Parse(row[i]!, CultureInfo.InvariantCulture), NpgsqlDbType.Integer, cancellationToken);
                    else await copy.WriteAsync(row[i]!, NpgsqlDbType.Text, cancellationToken);
                }
            }
            await copy.CompleteAsync(cancellationToken);
        }
        // IS NOT DISTINCT FROM matches null type_uri keys, like the table's NULLS NOT DISTINCT unique index.
        var match = string.Join(" AND ", keys.Select(x => $"t.\"{x}\" IS NOT DISTINCT FROM s.\"{x}\""));
        var changed = $"({string.Join(", ", values.Select(x => $"t.\"{x}\""))}) IS DISTINCT FROM ({string.Join(", ", values.Select(x => $"s.\"{x}\""))})";
        var deleted = await CountAsync(connection, $"""
            WITH deleted AS (DELETE FROM reference.{table} t WHERE NOT EXISTS (SELECT 1 FROM {stage} s WHERE {match}) RETURNING 1)
            SELECT count(*) FROM deleted
            """, cancellationToken);
        var updated = await CountAsync(connection, $"""
            WITH updated AS (UPDATE reference.{table} t SET {string.Join(", ", values.Select(x => $"\"{x}\" = s.\"{x}\""))}
                FROM {stage} s WHERE {match} AND {changed} RETURNING 1)
            SELECT count(*) FROM updated
            """, cancellationToken);
        var inserted = await CountAsync(connection, $"""
            WITH inserted AS (INSERT INTO reference.{table} ({string.Join(", ", columns.Select(x => $"\"{x}\""))})
                SELECT {string.Join(", ", columns.Select(x => $"s.\"{x}\""))} FROM {stage} s
                WHERE NOT EXISTS (SELECT 1 FROM reference.{table} t WHERE {match}) RETURNING 1)
            SELECT count(*) FROM inserted
            """, cancellationToken);
        return new(count, inserted, updated, deleted);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
