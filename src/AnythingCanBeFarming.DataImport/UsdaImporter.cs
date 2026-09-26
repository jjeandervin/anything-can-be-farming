using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record UsdaImportOptions(bool Force = false, string? Version = null, string? ZenodoRecord = null);
public sealed record UsdaImportResult(long? ImportId, bool AlreadyImported, UsdaImportReport Report);
public sealed record CodeCount(string Code, long Count);
public sealed record PlaceCount(string Scheme, string? PlaceId, long Count);

public sealed class UsdaImportReport
{
    public const int MaximumExamples = 20;
    public string? SourceHash { get; set; }
    public Dictionary<string, string> FileHashes { get; } = [];
    public Dictionary<string, long> RowsRead { get; } = [];
    public List<string> IgnoredFiles { get; } = [];
    public Dictionary<string, SeedTableSync> Seeds { get; set; } = [];
    public long RowsRejected { get; set; }
    public long DuplicateSymbols { get; set; }
    public long DuplicateOccurrenceIds { get; set; }
    public long SymbolsMissingFromTaxonFile { get; set; }
    public long OccurrencesSkipped { get; set; }
    public long FactsSkipped { get; set; }
    public List<ValueCount> MissingSymbolExamples { get; } = [];
    public long FactsWithUnknownOccurrence { get; set; }
    public long MissingRequiredFields { get; set; }
    public long WarningCount { get; set; }
    public Dictionary<string, long> WarningsByKind { get; } = [];
    public Dictionary<string, long> TaxaByRank { get; } = [];
    public Dictionary<string, long> TaxaByStatus { get; } = [];
    public long TaxaInserted { get; set; }
    public long TaxaUpdated { get; set; }
    public long TaxaRetired { get; set; }
    public long CurrentTaxa { get; set; }
    public long RetiredTaxa { get; set; }
    public long TaxaWithoutCanonicalName { get; set; }
    public long Facts { get; set; }
    public long Remarks { get; set; }
    public Dictionary<string, long> FactsByTraitKey { get; } = [];
    public long TaxaWithCharacteristics { get; set; }
    public Dictionary<string, List<CodeCount>> UnmappedCodedValues { get; } = [];
    public Dictionary<string, List<CodeCount>> UnresolvedCodedValues { get; } = [];
    public List<ValueCount> UnknownTypeUris { get; } = [];
    public long UnparseableNumerics { get; set; }
    public long FactsNotMeasurementOfTaxon { get; set; }
    public Dictionary<string, long> UnstoredColumnValues { get; } = [];
    public long DistributionRows { get; set; }
    public long DuplicateDistributionRowsCollapsed { get; set; }
    public Dictionary<string, long> DistributionByKind { get; } = [];
    public Dictionary<string, long> DistributionByScheme { get; } = [];
    public long TaxaPresentInOhio { get; set; }
    public long TaxaPresentInOhioAndNativeToContiguousUs { get; set; }
    public List<PlaceCount> UnlabeledPlaces { get; } = [];
    public long TaxaWithMoreThanTwoHeights { get; set; }
    public UsdaLinkReport? Links { get; set; }
}

// Streams row-level problems to a JSONL file; source text such as remarks is never copied into it.
public sealed class UsdaDiagnostics(TextWriter writer, UsdaImportReport report)
{
    public void Issue(string file, long? row, string severity, string kind, string field, string message, string? key = null)
    {
        if (severity == "warning")
        {
            report.WarningCount++;
            report.WarningsByKind[kind] = report.WarningsByKind.GetValueOrDefault(kind) + 1;
        }
        else report.RowsRejected++;
        writer.WriteLine(JsonSerializer.Serialize(new { file, row, severity, kind, field, key, message }));
    }
}

public sealed class UsdaImporter(string connectionString, string seedDirectory, TextWriter output)
{
    public const string Source = "USDA";
    public const string Kind = "EolTraits";
    public const string ContiguousUnitedStates = "Q578170";
    // Measurement columns this schema does not keep. measurementID is positional and never a durable key.
    private static readonly int[] UnstoredFactColumns = [3, 4, 8, 10, 11, 15, 16, 17];

    public async Task<UsdaImportResult> ImportAsync(string archiveDirectory, UsdaImportOptions options, TextWriter diagnosticWriter,
        CancellationToken cancellationToken = default)
    {
        var archive = UsdaArchive.Open(archiveDirectory);
        var seeds = UsdaSeeds.Load(seedDirectory);
        var report = new UsdaImportReport();
        var diagnostics = new UsdaDiagnostics(diagnosticWriter, report);
        report.IgnoredFiles.AddRange(archive.Ignored);
        foreach (var ignored in archive.Ignored) output.WriteLine($"Ignored {ignored}.");

        // Keep the handles open through publication; FileShare.Read prevents changes on Windows.
        var streams = new List<FileStream>();
        try
        {
            var hashes = new List<(string Role, string Hash)>();
            foreach (var file in archive.Files)
            {
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                streams.Add(stream);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                stream.Position = 0;
                hashes.Add((file.Table.Role, hash));
                report.FileHashes[Path.GetFileName(file.Path)] = hash;
            }
            report.SourceHash = UsdaArchive.CombinedHash(hashes);

            await using var session = await SourceImportSession.OpenAsync(connectionString, Source, cancellationToken);
            report.Seeds = await seeds.SyncAsync(session.Connection, cancellationToken);
            output.WriteLine("Seed files synchronized: " + string.Join("; ", report.Seeds.Select(x =>
                $"{x.Key} {x.Value.Rows:N0} rows (+{x.Value.Inserted} ~{x.Value.Updated} -{x.Value.Deleted})")));

            var prior = await PriorImportAsync(session, report.SourceHash, cancellationToken);
            if (prior != null && !options.Force)
            {
                output.WriteLine($"Already imported: import {prior}, source SHA-256 {report.SourceHash}. Use --force to reimport.");
                await LabelCoverageAsync(session, report, null, cancellationToken);
                output.WriteLine(JsonSerializer.Serialize(new { report.Seeds, report.UnmappedCodedValues, report.UnresolvedCodedValues, report.UnknownTypeUris }, Indented));
                return new(prior, true, report);
            }

            var import = await session.StartAsync(Kind, new
            {
                command = "import", directory = archive.Directory, force = options.Force,
                version = options.Version, zenodoRecord = options.ZenodoRecord
            }, cancellationToken);
            output.WriteLine($"Import {import.Id}: USDA PLANTS traits (Zenodo record {options.ZenodoRecord ?? "unknown"}, version {options.Version ?? "unknown"}), source SHA-256 {report.SourceHash}");
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? publication = null;
            try
            {
                publication = await session.Db.Database.BeginTransactionAsync(cancellationToken);
                await StageAsync(session, archive, streams, seeds, report, diagnostics, cancellationToken);
                for (var i = 0; i < streams.Count; i++)
                {
                    streams[i].Position = 0;
                    if (Convert.ToHexStringLower(await SHA256.HashDataAsync(streams[i], cancellationToken)) != hashes[i].Hash)
                        throw new InvalidDataException("Source files changed during import; nothing was published.");
                }
                await ValidateAsync(session, report, diagnostics, cancellationToken);
                if (report.RowsRejected > 0)
                    throw new InvalidDataException($"{report.RowsRejected:N0} rejected rows; nothing was published. See the diagnostics file.");
                output.WriteLine("Validation passed; publishing…");
                await PublishAsync(session, import.Id, report, cancellationToken);
                output.WriteLine("Linking USDA symbols to WFO taxa…");
                report.Links = await UsdaLinker.LinkAsync(session.Connection, output, cancellationToken);
                await SummarizeAsync(session, report, diagnostics, cancellationToken);
                Apply(import, report);
                await session.CompleteAsync(import, "Succeeded", report, null);
                await publication.CommitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                if (publication != null) await publication.RollbackAsync(CancellationToken.None);
                var (status, message) = SourceImportSession.Describe(exception, cancellationToken);
                report.TaxaInserted = report.TaxaUpdated = report.TaxaRetired = 0;
                Apply(import, report);
                await session.CompleteAsync(import, status, report, message);
                output.WriteLine(JsonSerializer.Serialize(report, Indented));
                throw new InvalidDataException($"Import {import.Id}: {message}", exception);
            }
            finally
            {
                if (publication != null) await publication.DisposeAsync();
            }
            output.WriteLine(JsonSerializer.Serialize(report, Indented));
            return new(import.Id, false, report);
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }

    private static async Task<long?> PriorImportAsync(SourceImportSession session, string hash, CancellationToken cancellationToken)
    {
        await using var command = session.Command("""
            SELECT "Id" FROM reference.source_import
            WHERE "Source" = @source AND "Kind" = @kind AND "Status" = 'Succeeded' AND "ValidationJson"->>'SourceHash' = @hash
            ORDER BY "Id" DESC LIMIT 1
            """);
        command.Parameters.AddWithValue("source", Source);
        command.Parameters.AddWithValue("kind", Kind);
        command.Parameters.AddWithValue("hash", hash);
        return (long?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task StageAsync(SourceImportSession session, UsdaArchive archive, List<FileStream> streams, UsdaSeeds seeds,
        UsdaImportReport report, UsdaDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync("""
            CREATE TEMP TABLE usda_taxon_stage (symbol text, source_url text, scientific_name text, family text, taxon_rank text,
                taxonomic_status text, canonical_name text, source_row bigint NOT NULL) ON COMMIT DROP;
            CREATE TEMP TABLE usda_occurrence_stage (occurrence_id text, symbol text, body_part_uri text, life_stage_uri text,
                source_row bigint NOT NULL) ON COMMIT DROP;
            CREATE TEMP TABLE usda_fact_stage (occurrence_id text, type_uri text, value_raw text, value_numeric numeric, unit_uri text,
                statistical_method text, source_term text, remark_sha text, method_sha text, source_url text,
                distribution_kind text, place_scheme text, place_id text, source_row bigint NOT NULL) ON COMMIT DROP;
            CREATE TEMP TABLE usda_remark_stage (sha256 text PRIMARY KEY, text text NOT NULL) ON COMMIT DROP;
            """, cancellationToken);
        var numericTypes = seeds.TraitTypes.Where(x => x.ValueKind == "numeric").Select(x => x.TypeUri).ToHashSet(StringComparer.Ordinal);
        var remarks = new Dictionary<string, string>(StringComparer.Ordinal);

        var taxonFile = Path.GetFileName(archive.Taxa.Path);
        await CopyAsync(session.Connection, streams[0], archive.Taxa, "usda_taxon_stage", report, diagnostics, (row, fields) =>
        {
            if (!Required(taxonFile, row, fields, [0, 2], archive.Taxa.Table, report, diagnostics)) return null;
            var canonical = UsdaValues.CanonicalName(fields[2], Optional(fields[4]));
            if (canonical == null)
            {
                report.TaxaWithoutCanonicalName++;
                diagnostics.Issue(taxonFile, row, "warning", "canonicalName", "scientificName",
                    "No canonical name could be derived for this rank; the symbol can only link through Wikidata.", fields[0]);
            }
            return [fields[0], Optional(fields[1]), fields[2], Optional(fields[3]), Optional(fields[4]), Optional(fields[5]), canonical, row];
        }, cancellationToken);

        var occurrenceFile = Path.GetFileName(archive.Occurrences.Path);
        await CopyAsync(session.Connection, streams[1], archive.Occurrences, "usda_occurrence_stage", report, diagnostics, (row, fields) =>
            Required(occurrenceFile, row, fields, [0, 1], archive.Occurrences.Table, report, diagnostics)
                ? [fields[0], fields[1], Optional(fields[2]), Optional(fields[3]), row] : null, cancellationToken);

        var factFile = Path.GetFileName(archive.Facts.Path);
        var headers = archive.Facts.Table.Headers;
        await CopyAsync(session.Connection, streams[2], archive.Facts, "usda_fact_stage", report, diagnostics, (row, fields) =>
        {
            if (!Required(factFile, row, fields, [1, 5], archive.Facts.Table, report, diagnostics)) return null;
            var (type, value) = (fields[5], fields[6]);
            if (fields[2] != "true")
            {
                report.FactsNotMeasurementOfTaxon++;
                diagnostics.Issue(factFile, row, "warning", "measurementOfTaxon", "measurementOfTaxon",
                    "Fact is not marked as a measurement of the taxon; it is stored like any other fact.", type);
            }
            foreach (var column in UnstoredFactColumns)
                if (fields[column].Length > 0) report.UnstoredColumnValues[headers[column]] = report.UnstoredColumnValues.GetValueOrDefault(headers[column]) + 1;
            var number = UsdaValues.Number(value);
            if (number == null && numericTypes.Contains(type))
            {
                report.UnparseableNumerics++;
                diagnostics.Issue(factFile, row, "warning", "numeric", "measurementValue",
                    $"Numeric trait value '{Abbreviate(value)}' is not a number; stored raw with no numeric value.", type);
            }
            var remark = Optional(fields[13]);
            var kind = UsdaValues.DistributionKind(type);
            var place = kind == null ? null : UsdaValues.ClassifyPlace(value);
            return [fields[1], type, value, number, Optional(fields[7]), Optional(fields[9]), UsdaValues.SourceTerm(remark),
                Remember(remarks, remark), Remember(remarks, Optional(fields[12])), Optional(fields[14]), kind, place?.Scheme, place?.PlaceId, row];
        }, cancellationToken);

        await using (var copy = await session.Connection.BeginBinaryImportAsync("COPY usda_remark_stage (sha256, text) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var (sha, text) in remarks)
            {
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(sha, NpgsqlDbType.Text, cancellationToken);
                await copy.WriteAsync(text, NpgsqlDbType.Text, cancellationToken);
            }
            await copy.CompleteAsync(cancellationToken);
        }
        await session.ExecuteAsync("""
            CREATE INDEX ON usda_taxon_stage (symbol);
            CREATE INDEX ON usda_occurrence_stage (occurrence_id);
            CREATE INDEX ON usda_occurrence_stage (symbol);
            CREATE INDEX ON usda_fact_stage (occurrence_id);
            ANALYZE usda_taxon_stage; ANALYZE usda_occurrence_stage; ANALYZE usda_fact_stage; ANALYZE usda_remark_stage;
            """, cancellationToken);
    }

    private delegate object?[]? RowMapper(long row, string[] fields);

    private async Task CopyAsync(NpgsqlConnection connection, FileStream stream, UsdaArchiveFile file, string stage,
        UsdaImportReport report, UsdaDiagnostics diagnostics, RowMapper map, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file.Path);
        output.WriteLine($"Reading {name}…");
        long read = 0;
        stream.Position = 0;
        // Invalid UTF-8 becomes U+FFFD and is reported; the source file keeps the original bytes.
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, 65536, leaveOpen: true);
        if (reader.Peek() == '﻿') reader.Read();
        using var copy = connection.BeginBinaryImport($"COPY {stage} FROM STDIN (FORMAT BINARY)");
        copy.Timeout = TimeSpan.Zero;
        foreach (var row in UsdaTsvReader.Read(reader, file.Table,
            (sourceRow, message) => { read++; diagnostics.Issue(name, sourceRow, "error", "record", "record", message); }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++read % 100000 == 0) output.WriteLine($"  {read:N0} rows…");
            var values = map(row.SourceRow, row.Fields);
            if (values == null) continue;
            copy.StartRow();
            foreach (var value in values)
            {
                switch (value)
                {
                    case null: copy.WriteNull(); break;
                    case string text: copy.Write(text, NpgsqlDbType.Text); break;
                    case decimal number: copy.Write(number, NpgsqlDbType.Numeric); break;
                    case long number: copy.Write(number, NpgsqlDbType.Bigint); break;
                    default: throw new InvalidOperationException($"Unsupported staging value {value.GetType().Name}.");
                }
            }
        }
        copy.Complete();
        report.RowsRead[name] = read;
        output.WriteLine($"  {read:N0} rows read from {name}.");
    }

    // Required identifiers must be present; NULs cannot be stored; U+FFFD marks invalid UTF-8 (rejected in identifiers).
    private static bool Required(string file, long row, string[] fields, int[] required, UsdaTable table, UsdaImportReport report,
        UsdaDiagnostics diagnostics)
    {
        var ok = true;
        foreach (var index in required)
            if (string.IsNullOrWhiteSpace(fields[index]))
            {
                report.MissingRequiredFields++;
                diagnostics.Issue(file, row, "error", "required", table.Headers[index], "Required field is missing.");
                ok = false;
            }
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Contains('\0'))
            {
                diagnostics.Issue(file, row, "error", "nul", table.Headers[i], "NUL cannot be stored in PostgreSQL text.");
                ok = false;
            }
            if (fields[i].Contains('�'))
            {
                var identifier = required.Contains(i);
                diagnostics.Issue(file, row, identifier ? "error" : "warning", "invalidUtf8", table.Headers[i],
                    "Invalid UTF-8 decoded as U+FFFD, or U+FFFD already present in source. Consult the unchanged source file.");
                ok &= !identifier;
            }
        }
        return ok;
    }

    private static string? Remember(Dictionary<string, string> remarks, string? text)
    {
        if (text == null) return null;
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        remarks.TryAdd(sha, text);
        return sha;
    }

    private static async Task ValidateAsync(SourceImportSession session, UsdaImportReport report, UsdaDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var (file, sql, message, count) in new (string, string, string, Action<long>)[]
        {
            ("taxon", """
                SELECT s.source_row, s.symbol FROM usda_taxon_stage s
                JOIN (SELECT symbol FROM usda_taxon_stage GROUP BY symbol HAVING count(*) > 1) d ON d.symbol = s.symbol
                ORDER BY s.symbol, s.source_row
                """, "Duplicate symbol; all conflicting rows prevent publication.", n => report.DuplicateSymbols = n),
            ("occurrence", """
                SELECT s.source_row, s.occurrence_id FROM usda_occurrence_stage s
                JOIN (SELECT occurrence_id FROM usda_occurrence_stage GROUP BY occurrence_id HAVING count(*) > 1) d ON d.occurrence_id = s.occurrence_id
                ORDER BY s.occurrence_id, s.source_row
                """, "Duplicate occurrenceID; facts cannot be attributed to one taxon.", n => report.DuplicateOccurrenceIds = n),
            ("measurementOrFact", """
                SELECT f.source_row, f.occurrence_id FROM usda_fact_stage f
                WHERE NOT EXISTS (SELECT 1 FROM usda_occurrence_stage o WHERE o.occurrence_id = f.occurrence_id) ORDER BY f.source_row
                """, "Fact points at an occurrenceID that is not in the occurrence file.", n => report.FactsWithUnknownOccurrence = n)
        })
        {
            long rows = 0;
            await using var command = session.Command(sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
                diagnostics.Issue(file, reader.GetInt64(0), "error", "integrity", file == "taxon" ? "taxonID" : "occurrenceID",
                    message, reader.IsDBNull(1) ? null : reader.GetString(1));
            }
            count(rows);
        }

        // Release 8 has 18,284 occurrences whose taxonID is missing from taxon.tab: old synonym symbols (ACGR, now SEGR4),
        // taxa EOL left out (ABGU), and a few malformed IDs. Their facts cannot be attributed to a staged taxon, so they are
        // skipped and reported per symbol rather than blocking every import of the release.
        await using (var command = session.Command("""
            SELECT o.symbol, count(DISTINCT o.occurrence_id), count(f.source_row), min(o.source_row)
            FROM usda_occurrence_stage o LEFT JOIN usda_fact_stage f ON f.occurrence_id = o.occurrence_id
            WHERE NOT EXISTS (SELECT 1 FROM usda_taxon_stage t WHERE t.symbol = o.symbol)
            GROUP BY o.symbol ORDER BY o.symbol
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var (symbol, occurrences, facts) = (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
                report.SymbolsMissingFromTaxonFile++;
                report.OccurrencesSkipped += occurrences;
                report.FactsSkipped += facts;
                if (report.MissingSymbolExamples.Count < UsdaImportReport.MaximumExamples)
                    report.MissingSymbolExamples.Add(new(Abbreviate(symbol), occurrences));
                diagnostics.Issue("occurrence", reader.GetInt64(3), "warning", "missingTaxon", "taxonID",
                    $"taxonID is not in the taxon file; its {occurrences:N0} occurrences and {facts:N0} facts were skipped.", Abbreviate(symbol));
            }
    }

    private static async Task PublishAsync(SourceImportSession session, long importId, UsdaImportReport report, CancellationToken cancellationToken)
    {
        var import = ("import", (object)importId);
        var taxa = await session.ScalarsAsync("""
            SELECT (SELECT count(*) FROM usda_taxon_stage s WHERE NOT EXISTS (SELECT 1 FROM reference.usda_taxon t WHERE t."Symbol" = s.symbol)),
                (SELECT count(*) FROM usda_taxon_stage s JOIN reference.usda_taxon t ON t."Symbol" = s.symbol
                 WHERE NOT t."IsCurrent" OR (t."ScientificName", t."CanonicalName", t."FamilyUsda", t."TaxonRank", t."TaxonomicStatus", t."SourceUrl")
                    IS DISTINCT FROM (s.scientific_name, s.canonical_name, s.family, s.taxon_rank, s.taxonomic_status, s.source_url)),
                (SELECT count(*) FROM reference.usda_taxon t WHERE t."IsCurrent" AND NOT EXISTS (SELECT 1 FROM usda_taxon_stage s WHERE s.symbol = t."Symbol"))
            """, cancellationToken);
        (report.TaxaInserted, report.TaxaUpdated, report.TaxaRetired) = (taxa[0], taxa[1], taxa[2]);
        // Absent symbols keep their ImportId, which then names the last import that contained them.
        await session.ExecuteAsync("""
            INSERT INTO reference.usda_taxon AS t ("Symbol", "ScientificName", "CanonicalName", "FamilyUsda", "TaxonRank", "TaxonomicStatus",
                "SourceUrl", "IsCurrent", "ImportId")
            SELECT symbol, scientific_name, canonical_name, family, taxon_rank, taxonomic_status, source_url, true, @import FROM usda_taxon_stage
            ON CONFLICT ("Symbol") DO UPDATE SET "ScientificName" = EXCLUDED."ScientificName", "CanonicalName" = EXCLUDED."CanonicalName",
                "FamilyUsda" = EXCLUDED."FamilyUsda", "TaxonRank" = EXCLUDED."TaxonRank", "TaxonomicStatus" = EXCLUDED."TaxonomicStatus",
                "SourceUrl" = EXCLUDED."SourceUrl", "IsCurrent" = true, "ImportId" = EXCLUDED."ImportId";
            UPDATE reference.usda_taxon t SET "IsCurrent" = false
            WHERE t."IsCurrent" AND NOT EXISTS (SELECT 1 FROM usda_taxon_stage s WHERE s.symbol = t."Symbol");
            """, cancellationToken, import);

        // Facts and distribution are replaced wholesale. DELETE rather than TRUNCATE keeps concurrent readers on the old rows.
        await session.ExecuteAsync("""
            DELETE FROM reference.usda_fact;
            DELETE FROM reference.usda_distribution;
            INSERT INTO reference.usda_remark ("Sha256", "Text") SELECT sha256, text FROM usda_remark_stage
            ON CONFLICT ("Sha256") DO NOTHING;
            INSERT INTO reference.usda_fact ("UsdaTaxonId", "Symbol", "OccurrenceId", "TypeUri", "ValueRaw", "ValueNumeric", "UnitUri",
                "BodyPartUri", "LifeStageUri", "StatisticalMethod", "SourceTerm", "RemarkId", "MethodRemarkId", "SourceUrl", "ImportId")
            SELECT t."Id", t."Symbol", f.occurrence_id, f.type_uri, f.value_raw, f.value_numeric, f.unit_uri, o.body_part_uri, o.life_stage_uri,
                f.statistical_method, f.source_term, r."Id", m."Id", f.source_url, @import
            FROM usda_fact_stage f
            JOIN usda_occurrence_stage o ON o.occurrence_id = f.occurrence_id
            JOIN reference.usda_taxon t ON t."Symbol" = o.symbol AND t."IsCurrent"
            LEFT JOIN reference.usda_remark r ON r."Sha256" = f.remark_sha
            LEFT JOIN reference.usda_remark m ON m."Sha256" = f.method_sha
            WHERE f.distribution_kind IS NULL
            ORDER BY f.source_row;
            INSERT INTO reference.usda_distribution ("UsdaTaxonId", "Symbol", "Kind", "PlaceRaw", "PlaceScheme", "PlaceId", "RemarkId", "ImportId")
            SELECT DISTINCT ON (t."Id", f.distribution_kind, f.value_raw)
                t."Id", t."Symbol", f.distribution_kind, f.value_raw, f.place_scheme, f.place_id, r."Id", @import
            FROM usda_fact_stage f
            JOIN usda_occurrence_stage o ON o.occurrence_id = f.occurrence_id
            JOIN reference.usda_taxon t ON t."Symbol" = o.symbol AND t."IsCurrent"
            LEFT JOIN reference.usda_remark r ON r."Sha256" = f.remark_sha
            WHERE f.distribution_kind IS NOT NULL
            ORDER BY t."Id", f.distribution_kind, f.value_raw, f.source_row;
            DELETE FROM reference.usda_remark r
            WHERE NOT EXISTS (SELECT 1 FROM reference.usda_fact f WHERE f."RemarkId" = r."Id")
              AND NOT EXISTS (SELECT 1 FROM reference.usda_fact f WHERE f."MethodRemarkId" = r."Id")
              AND NOT EXISTS (SELECT 1 FROM reference.usda_distribution d WHERE d."RemarkId" = r."Id");
            ANALYZE reference.usda_taxon; ANALYZE reference.usda_fact; ANALYZE reference.usda_distribution; ANALYZE reference.usda_remark;
            """, cancellationToken, import);
        var counts = await session.ScalarsAsync("""
            SELECT (SELECT count(*) FROM reference.usda_fact), (SELECT count(*) FROM reference.usda_distribution),
                (SELECT count(*) FROM usda_fact_stage WHERE distribution_kind IS NOT NULL), (SELECT count(*) FROM reference.usda_remark)
            """, cancellationToken);
        (report.Facts, report.DistributionRows, report.Remarks) = (counts[0], counts[1], counts[3]);
        report.DuplicateDistributionRowsCollapsed = counts[2] - counts[1];
    }

    // Unmapped and unresolved values by trait, and unknown type URIs, over the published facts and current seeds.
    // With diagnostics (an import), each distinct problem is also a warning.
    private static async Task LabelCoverageAsync(SourceImportSession session, UsdaImportReport report, UsdaDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        await using (var command = session.Command("""
            SELECT trait_key, value_code, label_confidence IS NULL AS unmapped, count(*) FROM reference.usda_fact_labeled
            WHERE trait_key IS NOT NULL AND value_kind <> 'numeric' AND value_label IS NULL
            GROUP BY trait_key, value_code, label_confidence IS NULL
            ORDER BY trait_key, count(*) DESC, value_code
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var (key, code, unmapped, count) = (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt64(3));
                var target = unmapped ? report.UnmappedCodedValues : report.UnresolvedCodedValues;
                if (!target.TryGetValue(key, out var list)) target[key] = list = [];
                list.Add(new(code, count));
                if (unmapped) diagnostics?.Issue("measurementOrFact", null, "warning", "unmappedCode", "measurementValue",
                    $"Value code '{Abbreviate(code)}' has no label ({count:N0} facts).", key);
            }
        await using (var command = session.Command("""
            SELECT f."TypeUri", count(*) FROM reference.usda_fact f
            WHERE NOT EXISTS (SELECT 1 FROM reference.usda_trait_type t WHERE t."TypeUri" = f."TypeUri")
            GROUP BY f."TypeUri" ORDER BY count(*) DESC, f."TypeUri"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                report.UnknownTypeUris.Add(new(reader.GetString(0), reader.GetInt64(1)));
                diagnostics?.Issue("measurementOrFact", null, "warning", "unknownType", "measurementType",
                    $"Measurement type has no entry in {UsdaSeeds.TraitTypesFile} ({reader.GetInt64(1):N0} facts).", Abbreviate(reader.GetString(0)));
            }
    }

    private static async Task SummarizeAsync(SourceImportSession session, UsdaImportReport report, UsdaDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        await LabelCoverageAsync(session, report, diagnostics, cancellationToken);
        await GroupAsync(session, """SELECT coalesce("TaxonRank", '(none)'), count(*) FROM reference.usda_taxon WHERE "IsCurrent" GROUP BY 1 ORDER BY 1""",
            report.TaxaByRank, cancellationToken);
        await GroupAsync(session, """SELECT coalesce("TaxonomicStatus", '(none)'), count(*) FROM reference.usda_taxon WHERE "IsCurrent" GROUP BY 1 ORDER BY 1""",
            report.TaxaByStatus, cancellationToken);
        await GroupAsync(session, """
            SELECT coalesce(t."Key", f."TypeUri"), count(*) FROM reference.usda_fact f
            LEFT JOIN reference.usda_trait_type t ON t."TypeUri" = f."TypeUri" GROUP BY 1 ORDER BY 1
            """, report.FactsByTraitKey, cancellationToken);
        await GroupAsync(session, """SELECT "Kind", count(*) FROM reference.usda_distribution GROUP BY 1 ORDER BY 1""",
            report.DistributionByKind, cancellationToken);
        await GroupAsync(session, """SELECT "PlaceScheme", count(*) FROM reference.usda_distribution GROUP BY 1 ORDER BY 1""",
            report.DistributionByScheme, cancellationToken);
        var totals = await session.ScalarsAsync($"""
            SELECT count(*) FILTER (WHERE "IsCurrent"), count(*) FILTER (WHERE NOT "IsCurrent"),
                (SELECT count(DISTINCT f."UsdaTaxonId") FROM reference.usda_fact f JOIN reference.usda_trait_type t ON t."TypeUri" = f."TypeUri"
                 WHERE t."Key" IN ('shade_tolerance', 'min_temperature_f')),
                (SELECT count(DISTINCT d."UsdaTaxonId") FROM reference.usda_distribution d
                 JOIN reference.place_label p ON p."Scheme" = d."PlaceScheme" AND p."PlaceId" = d."PlaceId"
                 WHERE d."Kind" = 'Present' AND p."CountryCode" = 'US' AND p."AdminCode" = 'OH'),
                (SELECT count(DISTINCT d."UsdaTaxonId") FROM reference.usda_distribution d
                 JOIN reference.place_label p ON p."Scheme" = d."PlaceScheme" AND p."PlaceId" = d."PlaceId"
                 WHERE d."Kind" = 'Present' AND p."CountryCode" = 'US' AND p."AdminCode" = 'OH'
                   AND EXISTS (SELECT 1 FROM reference.usda_distribution n WHERE n."UsdaTaxonId" = d."UsdaTaxonId"
                               AND n."Kind" = 'Native' AND n."PlaceScheme" = 'wikidata' AND n."PlaceId" = '{ContiguousUnitedStates}')),
                (SELECT count(*) FROM (SELECT 1 FROM reference.usda_fact f JOIN reference.usda_trait_type t ON t."TypeUri" = f."TypeUri"
                 WHERE t."Key" = 'height_ft' GROUP BY f."UsdaTaxonId" HAVING count(*) > 2) many)
            FROM reference.usda_taxon
            """, cancellationToken);
        (report.CurrentTaxa, report.RetiredTaxa, report.TaxaWithCharacteristics, report.TaxaPresentInOhio,
            report.TaxaPresentInOhioAndNativeToContiguousUs, report.TaxaWithMoreThanTwoHeights) =
            (totals[0], totals[1], totals[2], totals[3], totals[4], totals[5]);
        await using var command = session.Command("""
            SELECT d."PlaceScheme", d."PlaceId", count(*) FROM reference.usda_distribution d
            WHERE d."PlaceScheme" <> 'literal'
              AND NOT EXISTS (SELECT 1 FROM reference.place_label p WHERE p."Scheme" = d."PlaceScheme" AND p."PlaceId" = d."PlaceId")
            GROUP BY 1, 2 ORDER BY count(*) DESC, 1, 2
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var place = new PlaceCount(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2));
            report.UnlabeledPlaces.Add(place);
            diagnostics.Issue("measurementOrFact", null, "warning", "unlabeledPlace", "measurementValue",
                $"Place {place.Scheme}:{place.PlaceId} has no entry in {UsdaSeeds.PlaceLabelsFile} ({place.Count:N0} rows).");
        }
    }

    private static async Task GroupAsync(SourceImportSession session, string sql, Dictionary<string, long> target, CancellationToken cancellationToken)
    {
        await using var command = session.Command(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) target[reader.GetString(0)] = reader.GetInt64(1);
    }

    private static void Apply(SourceImport import, UsdaImportReport report)
    {
        import.RowsRead = report.RowsRead.Values.Sum();
        import.RowsInserted = report.TaxaInserted + report.Facts + report.DistributionRows;
        import.RowsUpdated = report.TaxaUpdated;
        import.RowsRetired = report.TaxaRetired;
        import.WarningCount = report.WarningCount;
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;
    private static string Abbreviate(string value) => value.Length <= 80 ? value : value[..80] + "…";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
