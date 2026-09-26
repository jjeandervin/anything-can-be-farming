using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record UsdaLinkCandidate(string WfoId, string ScientificName, string? Authorship, string? TaxonomicStatus);
public sealed record UsdaLinkExample(string Symbol, string? CanonicalName, string[] Candidates);

public sealed class UsdaLinkReport
{
    public long Taxa { get; set; }
    public long LinksChanged { get; set; }
    public long LinksRemoved { get; set; }
    public Dictionary<string, long> ByStatus { get; } = [];
    public Dictionary<string, long> ByStatusAndMethod { get; } = [];
    public long LinkedToSynonym { get; set; }
    public long LinkedWithAcceptedTaxon { get; set; }
    public long SymbolsWithWikidataIds { get; set; }
    public long Conflicts { get; set; }
    public List<UsdaLinkExample> ConflictExamples { get; } = [];
    public long Ambiguous { get; set; }
    public List<UsdaLinkExample> AmbiguousExamples { get; } = [];
}

// Section 4.2: a USDA symbol links to WFO through Wikidata's USDA PLANTS ID (P1772) first, then through an exact
// name match at the same rank. Ambiguous candidates are recorded, never guessed between. Links are rebuilt after
// every USDA import and every WFO backbone import; only rows whose outcome changed are rewritten.
public static class UsdaLinker
{
    private const int MaximumExamples = 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static long LockKey { get; } = SourceImportSession.LockKey("USDA:link");

    private sealed record Taxon(long Id, string Symbol, string? CanonicalName, string? Rank);
    private sealed record WfoCandidate(long Id, string WfoId, string ScientificName, string? Authorship, string? Status);
    private sealed record Outcome(long UsdaTaxonId, string Symbol, long? WfoTaxonId, string Method, string Status, string? DetailJson);

    public static async Task<UsdaLinkReport> RunAsync(string connectionString, TextWriter output, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>().UseNpgsql(connection).Options);
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("Apply EF Core migrations before linking USDA symbols; the importer does not migrate the database.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var report = await LinkAsync(connection, output, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    // Runs inside the caller's transaction.
    public static async Task<UsdaLinkReport> LinkAsync(NpgsqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, $"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        var taxa = new List<Taxon>();
        await using (var command = Command(connection, """
            SELECT "Id", "Symbol", "CanonicalName", "TaxonRank" FROM reference.usda_taxon WHERE "IsCurrent" ORDER BY "Id"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                taxa.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));

        // Step 1: every resolved current WFO taxon reached from a current item carrying the symbol as P1772.
        var viaWikidata = new Dictionary<long, (List<WfoCandidate> Taxa, SortedSet<string> Qids)>();
        await using (var command = Command(connection, """
            SELECT DISTINCT u."Id", i."Qid", w."Id", w."TaxonId", w."ScientificName", w."ScientificNameAuthorship", w."TaxonomicStatus"
            FROM reference.usda_taxon u
            JOIN reference.wikidata_external_id e ON e."Property" = 'P1772' AND e."Value" = u."Symbol"
            JOIN reference.wikidata_item i ON i."Id" = e."ItemId" AND i."IsCurrent"
            JOIN reference.wikidata_wfo_link l ON l."ItemId" = i."Id" AND l."IsCurrent" AND l."WfoTaxonId" IS NOT NULL
            JOIN reference.wfo_taxon w ON w."Id" = l."WfoTaxonId" AND w."IsCurrent"
            WHERE u."IsCurrent"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                if (!viaWikidata.TryGetValue(id, out var entry)) viaWikidata[id] = entry = ([], new(StringComparer.Ordinal));
                entry.Qids.Add(reader.GetString(1));
                if (entry.Taxa.All(x => x.Id != reader.GetInt64(2))) entry.Taxa.Add(Candidate(reader, 2));
            }

        // Step 2: exact normalized name at the same rank, for symbols step 1 did not reach.
        var wanted = taxa.Where(x => !viaWikidata.ContainsKey(x.Id) && x.CanonicalName != null && x.Rank != null)
            .Select(x => (Name: NameNormalizer.Normalize(x.CanonicalName!), Rank: x.Rank!)).ToHashSet();
        var byName = new Dictionary<(string Name, string Rank), List<WfoCandidate>>();
        if (wanted.Count > 0)
        {
            var ranks = wanted.Select(x => x.Rank).Distinct().ToArray();
            await using var command = Command(connection, """
                SELECT "Id", "TaxonId", "ScientificName", "ScientificNameAuthorship", "TaxonomicStatus", "TaxonRank"
                FROM reference.wfo_taxon WHERE "IsCurrent" AND "TaxonRank" = ANY(@ranks)
                """);
            command.Parameters.AddWithValue("ranks", ranks);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (NameNormalizer.Normalize(reader.GetString(2)), reader.GetString(5));
                if (!wanted.Contains(key)) continue;
                if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
                list.Add(Candidate(reader, 0));
            }
        }

        var outcomes = new List<Outcome>(taxa.Count);
        foreach (var taxon in taxa)
        {
            if (viaWikidata.TryGetValue(taxon.Id, out var linked))
            {
                var detail = new { qids = linked.Qids.ToArray(), candidates = Describe(linked.Taxa) };
                outcomes.Add(linked.Taxa.Count == 1
                    ? new(taxon.Id, taxon.Symbol, linked.Taxa[0].Id, "wikidata", "Linked", JsonSerializer.Serialize(new { qids = detail.qids }, Json))
                    : new(taxon.Id, taxon.Symbol, null, "wikidata", "Conflict", JsonSerializer.Serialize(detail, Json)));
                continue;
            }
            var candidates = taxon.CanonicalName != null && taxon.Rank != null
                && byName.TryGetValue((NameNormalizer.Normalize(taxon.CanonicalName), taxon.Rank), out var found) ? found : [];
            outcomes.Add(candidates.Count switch
            {
                0 => new(taxon.Id, taxon.Symbol, null, "name", "NotFound", null),
                1 => new(taxon.Id, taxon.Symbol, candidates[0].Id, "name", "Linked", null),
                _ => new(taxon.Id, taxon.Symbol, null, "name", "Ambiguous", JsonSerializer.Serialize(new { candidates = Describe(candidates) }, Json))
            });
        }

        await ExecuteAsync(connection, """
            DROP TABLE IF EXISTS usda_link_stage;
            CREATE TEMP TABLE usda_link_stage (usda_taxon_id bigint PRIMARY KEY, symbol text NOT NULL, wfo_taxon_id bigint,
                method text NOT NULL, status text NOT NULL, detail_json jsonb) ON COMMIT DROP;
            """, cancellationToken);
        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY usda_link_stage (usda_taxon_id, symbol, wfo_taxon_id, method, status, detail_json) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var outcome in outcomes)
            {
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(outcome.UsdaTaxonId, NpgsqlDbType.Bigint, cancellationToken);
                await copy.WriteAsync(outcome.Symbol, NpgsqlDbType.Text, cancellationToken);
                if (outcome.WfoTaxonId is { } wfo) await copy.WriteAsync(wfo, NpgsqlDbType.Bigint, cancellationToken);
                else await copy.WriteNullAsync(cancellationToken);
                await copy.WriteAsync(outcome.Method, NpgsqlDbType.Text, cancellationToken);
                await copy.WriteAsync(outcome.Status, NpgsqlDbType.Text, cancellationToken);
                if (outcome.DetailJson != null) await copy.WriteAsync(outcome.DetailJson, NpgsqlDbType.Jsonb, cancellationToken);
                else await copy.WriteNullAsync(cancellationToken);
            }
            await copy.CompleteAsync(cancellationToken);
        }

        var report = new UsdaLinkReport { Taxa = taxa.Count };
        // The accepted taxon follows the Wikidata spec's section 3.3: the taxon itself if Accepted, its current accepted
        // taxon if a Synonym, otherwise null.
        await using (var command = Command(connection, """
            WITH computed AS (
                SELECT s.*, CASE WHEN w."TaxonomicStatus" = 'Accepted' THEN w."Id"
                                 WHEN w."TaxonomicStatus" = 'Synonym' THEN a."Id" END AS accepted_id
                FROM usda_link_stage s
                LEFT JOIN reference.wfo_taxon w ON w."Id" = s.wfo_taxon_id AND w."IsCurrent"
                LEFT JOIN reference.wfo_taxon a ON a."Id" = w."AcceptedTaxonId" AND a."IsCurrent"
            ), changed AS (
                INSERT INTO reference.usda_wfo_link AS l ("UsdaTaxonId", "Symbol", "WfoTaxonId", "AcceptedWfoTaxonId", "Method", "Status", "DetailJson")
                SELECT usda_taxon_id, symbol, wfo_taxon_id, accepted_id, method, status, detail_json FROM computed
                ON CONFLICT ("UsdaTaxonId") DO UPDATE SET "Symbol" = EXCLUDED."Symbol", "WfoTaxonId" = EXCLUDED."WfoTaxonId",
                    "AcceptedWfoTaxonId" = EXCLUDED."AcceptedWfoTaxonId", "Method" = EXCLUDED."Method", "Status" = EXCLUDED."Status",
                    "DetailJson" = EXCLUDED."DetailJson"
                WHERE (l."Symbol", l."WfoTaxonId", l."AcceptedWfoTaxonId", l."Method", l."Status", l."DetailJson")
                    IS DISTINCT FROM (EXCLUDED."Symbol", EXCLUDED."WfoTaxonId", EXCLUDED."AcceptedWfoTaxonId", EXCLUDED."Method",
                        EXCLUDED."Status", EXCLUDED."DetailJson")
                RETURNING 1
            )
            SELECT count(*) FROM changed
            """))
            report.LinksChanged = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        // Retired symbols lose their link; their facts are gone too.
        await using (var command = Command(connection, """
            WITH removed AS (DELETE FROM reference.usda_wfo_link l
                WHERE NOT EXISTS (SELECT 1 FROM usda_link_stage s WHERE s.usda_taxon_id = l."UsdaTaxonId") RETURNING 1)
            SELECT count(*) FROM removed
            """))
            report.LinksRemoved = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        await using (var command = Command(connection, """
            SELECT l."Status", l."Method", count(*), count(*) FILTER (WHERE w."TaxonomicStatus" = 'Synonym'), count(l."AcceptedWfoTaxonId")
            FROM reference.usda_wfo_link l LEFT JOIN reference.wfo_taxon w ON w."Id" = l."WfoTaxonId"
            GROUP BY 1, 2 ORDER BY 1, 2
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var (status, method, count) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
                report.ByStatus[status] = report.ByStatus.GetValueOrDefault(status) + count;
                report.ByStatusAndMethod[$"{status}/{method}"] = count;
                report.LinkedToSynonym += reader.GetInt64(3);
                report.LinkedWithAcceptedTaxon += reader.GetInt64(4);
            }
        report.SymbolsWithWikidataIds = (await ScalarAsync(connection, """
            SELECT count(DISTINCT u."Id") FROM reference.usda_taxon u
            JOIN reference.wikidata_external_id e ON e."Property" = 'P1772' AND e."Value" = u."Symbol"
            WHERE u."IsCurrent"
            """, cancellationToken));
        var canonical = taxa.ToDictionary(x => x.Id, x => x.CanonicalName);
        foreach (var outcome in outcomes.Where(x => x.Status is "Conflict" or "Ambiguous").OrderBy(x => x.Symbol, StringComparer.Ordinal))
        {
            var conflict = outcome.Status == "Conflict";
            if (conflict) report.Conflicts++; else report.Ambiguous++;
            var examples = conflict ? report.ConflictExamples : report.AmbiguousExamples;
            if (examples.Count >= MaximumExamples) continue;
            using var detail = JsonDocument.Parse(outcome.DetailJson!);
            examples.Add(new(outcome.Symbol, canonical[outcome.UsdaTaxonId],
                detail.RootElement.GetProperty("candidates").EnumerateArray().Select(x => x.GetProperty("wfoId").GetString()!).ToArray()));
        }
        output.WriteLine($"USDA links: {string.Join(", ", report.ByStatusAndMethod.Select(x => $"{x.Key} {x.Value:N0}"))}; {report.LinksChanged:N0} changed.");
        return report;
    }

    private static WfoCandidate Candidate(NpgsqlDataReader reader, int offset) => new(reader.GetInt64(offset), reader.GetString(offset + 1),
        reader.GetString(offset + 2), reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4));

    private static UsdaLinkCandidate[] Describe(IEnumerable<WfoCandidate> candidates) => candidates
        .OrderBy(x => x.WfoId, StringComparer.Ordinal)
        .Select(x => new UsdaLinkCandidate(x.WfoId, x.ScientificName, x.Authorship, x.Status)).ToArray();

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = 0 };

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
