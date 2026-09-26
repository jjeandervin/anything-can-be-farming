using System.Text.Json;
using AnythingCanBeFarming.Data;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed class DetailsReport
{
    public const int MaximumExamples = 20;
    public long ItemsChecked { get; set; }
    public long ItemsFetched { get; set; }
    public long ItemsUnchanged { get; set; }
    public long ItemsUpdated { get; set; }
    public long ItemsInserted { get; set; }
    public long ItemsRetired { get; set; }
    public long MissingItems { get; set; }
    public long RedirectedItems { get; set; }
    public long CommonNamesInserted { get; set; }
    public long CommonNamesDeleted { get; set; }
    public long ExternalIdsInserted { get; set; }
    public long ExternalIdsDeleted { get; set; }
    public long P7715Mismatches { get; set; }
    public long BatchesCommitted { get; set; }
    public long CurrentItems { get; set; }
    public long ItemsWithDetails { get; set; }
    public long ItemsWithEnglishCommonName { get; set; }
    public List<ValueCount> CommonNamesByLanguage { get; } = [];
    public long ItemsWithEnwikiTitle { get; set; }
    public long ItemsWithImage { get; set; }
    public Dictionary<string, long> ExternalIdCounts { get; } = [];
    public long WarningCount { get; set; }
    public List<string> Warnings { get; } = [];

    public void Warn(string message)
    {
        WarningCount++;
        if (Warnings.Count < MaximumExamples) Warnings.Add(message);
    }
}

// Incremental by lastrevid; commits every CommitSize items so an interrupted run resumes where it stopped.
// The crosswalk stays authoritative for WFO links; P7715 here is only a consistency check.
public sealed class WikidataDetailsImporter(string connectionString, WikidataApiClient api, TextWriter output)
{
    public int CommitSize { get; init; } = 500;

    public async Task<SourceImportResult<DetailsReport>> ImportAsync(bool full, int? limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1) throw new InvalidDataException("--limit must be a positive number.");
        await using var session = await SourceImportSession.OpenAsync(connectionString, WikidataCrosswalkImporter.Source, cancellationToken);
        var import = await session.StartAsync("Details", new { command = "details", full, limit }, cancellationToken);
        output.WriteLine($"Import {import.Id}: Wikidata details ({(full ? "full" : "incremental")}{(limit is { } n ? $", limit {n:N0}" : "")})");
        var report = new DetailsReport();
        try
        {
            await api.VerifyPropertiesAsync(cancellationToken);
            await session.ExecuteAsync("""
                DROP TABLE IF EXISTS details_item, details_name, details_external, details_wfo, details_redirect;
                CREATE TEMP TABLE details_item (qid text PRIMARY KEY, taxon_name text, taxon_rank_qid text, label_en text,
                    enwiki_title text, image_file text, last_rev_id bigint);
                CREATE TEMP TABLE details_name (qid text NOT NULL, language text NOT NULL, name text NOT NULL, normalized_name text NOT NULL);
                CREATE TEMP TABLE details_external (qid text NOT NULL, property text NOT NULL, value text NOT NULL);
                CREATE TEMP TABLE details_wfo (qid text NOT NULL, wfo_id text NOT NULL);
                CREATE TEMP TABLE details_redirect (from_qid text NOT NULL, to_qid text NOT NULL);
                """, cancellationToken);
            long lastId = 0;
            while (limit is not { } max || report.ItemsChecked < max)
            {
                var size = (int)Math.Min(CommitSize, (limit ?? long.MaxValue) - report.ItemsChecked);
                var chunk = await NextChunkAsync(session, lastId, size, cancellationToken);
                if (chunk.Count == 0) break;
                lastId = chunk[^1].Id;
                report.ItemsChecked += chunk.Count;
                var entities = await FetchAsync(chunk, full, report, cancellationToken);
                await PublishAsync(session, import.Id, entities, report, cancellationToken);
                report.BatchesCommitted++;
                if (report.BatchesCommitted % 20 == 0)
                    output.WriteLine($"Checked {report.ItemsChecked:N0} items; fetched {report.ItemsFetched:N0}, updated {report.ItemsUpdated:N0}…");
            }
            report.ItemsUnchanged = Math.Max(0, report.ItemsChecked - report.ItemsUpdated - report.MissingItems - report.RedirectedItems);
            await SummarizeAsync(session, report, cancellationToken);
            Apply(import, report);
            await session.CompleteAsync(import, "Succeeded", report, null);
        }
        catch (Exception exception)
        {
            // Committed batches stay published; the next run skips them through the lastrevid check.
            var (status, message) = SourceImportSession.Describe(exception, cancellationToken);
            report.ItemsUnchanged = Math.Max(0, report.ItemsChecked - report.ItemsUpdated - report.MissingItems - report.RedirectedItems);
            Apply(import, report);
            await session.CompleteAsync(import, status, report, message);
            output.WriteLine(JsonSerializer.Serialize(report, Indented));
            throw new InvalidDataException($"Import {import.Id}: {message}", exception);
        }
        output.WriteLine(JsonSerializer.Serialize(report, Indented));
        return new(import.Id, report);
    }

    private sealed record ItemState(long Id, string Qid, long? LastRevId, bool Fetched);

    private static async Task<List<ItemState>> NextChunkAsync(SourceImportSession session, long lastId, int size, CancellationToken cancellationToken)
    {
        await using var command = session.Command("""
            SELECT "Id", "Qid", "LastRevId", "DetailsFetchedAt" IS NOT NULL FROM reference.wikidata_item
            WHERE "IsCurrent" AND "Id" > @last ORDER BY "Id" LIMIT @size
            """);
        command.Parameters.AddWithValue("last", lastId);
        command.Parameters.AddWithValue("size", size);
        var items = new List<ItemState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetBoolean(3)));
        return items;
    }

    private async Task<List<WikidataEntity>> FetchAsync(List<ItemState> chunk, bool full, DetailsReport report,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var toFetch = chunk.Where(x => full || !x.Fetched).Select(x => x.Qid).ToList();
        if (!full)
        {
            var known = chunk.Where(x => x.Fetched).ToDictionary(x => x.Qid);
            foreach (var batch in known.Keys.Chunk(WikidataApiClient.MaximumIdsPerRequest))
            {
                var seen = new HashSet<string>();
                foreach (var info in await api.GetEntitiesAsync(batch, infoOnly: true, warnings, cancellationToken))
                {
                    if (!known.TryGetValue(info.RequestedQid, out var item) || !seen.Add(info.RequestedQid)) continue;
                    if (info.Missing) Missing(info.RequestedQid, report);
                    else if (info.IsRedirect || info.LastRevId != item.LastRevId) toFetch.Add(info.RequestedQid);
                }
                foreach (var qid in batch.Where(x => !seen.Contains(x)))
                    report.Warn($"Wikidata returned no entry for {qid}; it was left unchanged.");
            }
        }
        var entities = new List<WikidataEntity>();
        foreach (var batch in toFetch.Chunk(WikidataApiClient.MaximumIdsPerRequest))
            foreach (var entity in await api.GetEntitiesAsync(batch, infoOnly: false, warnings, cancellationToken))
            {
                if (entity.Missing) { Missing(entity.RequestedQid, report); continue; }
                report.ItemsFetched++;
                if (entity.IsRedirect)
                {
                    report.RedirectedItems++;
                    report.Warn($"Item {entity.RequestedQid} redirects to {entity.Qid}; details are stored under {entity.Qid} and {entity.RequestedQid} is retired.");
                }
                entities.Add(entity);
            }
        foreach (var warning in warnings) report.Warn(warning);
        return entities;
    }

    private static void Missing(string qid, DetailsReport report)
    {
        report.MissingItems++;
        report.Warn($"Item {qid} is missing (deleted) on Wikidata; its stored details were kept.");
    }

    private static async Task PublishAsync(SourceImportSession session, long importId, List<WikidataEntity> entities, DetailsReport report,
        CancellationToken cancellationToken)
    {
        var import = ("import", (object)importId);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        await session.ExecuteAsync("TRUNCATE details_item, details_name, details_external, details_wfo, details_redirect", cancellationToken);
        // Two requested QIDs can land on the same target (a redirect and the target itself); store it once.
        var targets = entities.DistinctBy(x => x.Qid).ToList();
        await CopyAsync(session.Connection, "details_item (qid, taxon_name, taxon_rank_qid, label_en, enwiki_title, image_file, last_rev_id)",
            targets.Select(x => new object?[] { x.Qid, x.TaxonName, x.TaxonRankQid, x.LabelEn, x.EnwikiTitle, x.ImageFile, x.LastRevId }), cancellationToken);
        await CopyAsync(session.Connection, "details_name (qid, language, name, normalized_name)",
            targets.SelectMany(x => x.CommonNames.Select(n => new object?[] { x.Qid, n.Language, n.Name, NameNormalizer.Normalize(n.Name) })), cancellationToken);
        await CopyAsync(session.Connection, "details_external (qid, property, value)",
            targets.SelectMany(x => x.ExternalIds.Select(e => new object?[] { x.Qid, e.Property, e.Value })), cancellationToken);
        await CopyAsync(session.Connection, "details_wfo (qid, wfo_id)",
            targets.SelectMany(x => x.WfoIds.Select(w => new object?[] { x.Qid, w })), cancellationToken);
        await CopyAsync(session.Connection, "details_redirect (from_qid, to_qid)",
            entities.Where(x => x.IsRedirect).Select(x => new object?[] { x.RequestedQid, x.Qid }), cancellationToken);

        // Only rows whose content changed are rewritten, so a rerun without upstream edits changes nothing.
        var items = await session.ScalarsAsync("""
            WITH upserted AS (
                INSERT INTO reference.wikidata_item AS item ("Qid", "TaxonName", "TaxonRankQid", "LabelEn", "EnwikiTitle",
                    "ImageFile", "LastRevId", "DetailsFetchedAt", "IsCurrent", "DetailsImportId")
                SELECT qid, taxon_name, taxon_rank_qid, label_en, enwiki_title, image_file, last_rev_id, now(), true, @import
                FROM details_item
                ON CONFLICT ("Qid") DO UPDATE SET "TaxonName" = EXCLUDED."TaxonName", "TaxonRankQid" = EXCLUDED."TaxonRankQid",
                    "LabelEn" = EXCLUDED."LabelEn", "EnwikiTitle" = EXCLUDED."EnwikiTitle", "ImageFile" = EXCLUDED."ImageFile",
                    "LastRevId" = EXCLUDED."LastRevId", "DetailsFetchedAt" = EXCLUDED."DetailsFetchedAt",
                    "DetailsImportId" = EXCLUDED."DetailsImportId"
                WHERE item."DetailsFetchedAt" IS NULL OR
                    (item."TaxonName", item."TaxonRankQid", item."LabelEn", item."EnwikiTitle", item."ImageFile", item."LastRevId")
                    IS DISTINCT FROM (EXCLUDED."TaxonName", EXCLUDED."TaxonRankQid", EXCLUDED."LabelEn", EXCLUDED."EnwikiTitle",
                        EXCLUDED."ImageFile", EXCLUDED."LastRevId")
                RETURNING xmax = 0 AS inserted)
            SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM upserted
            """, cancellationToken, import);
        report.ItemsInserted += items[0];
        report.ItemsUpdated += items[1];

        // Each fetched item's names and IDs become exactly the fetched set.
        report.CommonNamesDeleted += (await session.ScalarsAsync("""
            WITH deleted AS (
                DELETE FROM reference.wikidata_common_name cn USING reference.wikidata_item i, details_item d
                WHERE cn."ItemId" = i."Id" AND i."Qid" = d.qid AND NOT EXISTS (SELECT 1 FROM details_name n
                    WHERE n.qid = d.qid AND n.language = cn."Language" AND n.name = cn."Name")
                RETURNING 1)
            SELECT count(*) FROM deleted
            """, cancellationToken))[0];
        report.CommonNamesInserted += (await session.ScalarsAsync("""
            WITH inserted AS (
                INSERT INTO reference.wikidata_common_name AS cn ("ItemId", "Language", "Name", "NormalizedName")
                SELECT i."Id", n.language, n.name, n.normalized_name
                FROM details_name n JOIN reference.wikidata_item i ON i."Qid" = n.qid
                ON CONFLICT ("ItemId", "Language", "Name") DO UPDATE SET "NormalizedName" = EXCLUDED."NormalizedName"
                WHERE cn."NormalizedName" <> EXCLUDED."NormalizedName"
                RETURNING xmax = 0 AS inserted)
            SELECT count(*) FILTER (WHERE inserted) FROM inserted
            """, cancellationToken))[0];
        report.ExternalIdsDeleted += (await session.ScalarsAsync("""
            WITH deleted AS (
                DELETE FROM reference.wikidata_external_id e USING reference.wikidata_item i, details_item d
                WHERE e."ItemId" = i."Id" AND i."Qid" = d.qid AND NOT EXISTS (SELECT 1 FROM details_external x
                    WHERE x.qid = d.qid AND x.property = e."Property" AND x.value = e."Value")
                RETURNING 1)
            SELECT count(*) FROM deleted
            """, cancellationToken))[0];
        report.ExternalIdsInserted += (await session.ScalarsAsync("""
            WITH inserted AS (
                INSERT INTO reference.wikidata_external_id ("ItemId", "Property", "Value")
                SELECT i."Id", x.property, x.value FROM details_external x JOIN reference.wikidata_item i ON i."Qid" = x.qid
                ON CONFLICT ("ItemId", "Property", "Value") DO NOTHING
                RETURNING 1)
            SELECT count(*) FROM inserted
            """, cancellationToken))[0];
        report.ItemsRetired += (await session.ScalarsAsync("""
            WITH retired AS (
                UPDATE reference.wikidata_item SET "IsCurrent" = false, "DetailsImportId" = @import
                WHERE "IsCurrent" AND "Qid" IN (SELECT from_qid FROM details_redirect)
                RETURNING 1)
            SELECT count(*) FROM retired
            """, cancellationToken, import))[0];

        await using (var command = session.Command("""
            SELECT d.qid FROM details_item d
            WHERE EXISTS (SELECT w.wfo_id FROM details_wfo w WHERE w.qid = d.qid
                          EXCEPT SELECT l."WfoId" FROM reference.wikidata_wfo_link l WHERE l."Qid" = d.qid AND l."IsCurrent")
               OR EXISTS (SELECT l."WfoId" FROM reference.wikidata_wfo_link l WHERE l."Qid" = d.qid AND l."IsCurrent"
                          EXCEPT SELECT w.wfo_id FROM details_wfo w WHERE w.qid = d.qid)
            ORDER BY length(d.qid), d.qid
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                report.P7715Mismatches++;
                report.Warn($"P7715 on {reader.GetString(0)} differs from the crosswalk's links; the crosswalk is kept.");
            }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CopyAsync(NpgsqlConnection connection, string target, IEnumerable<object?[]> rows,
        CancellationToken cancellationToken)
    {
        await using var copy = await connection.BeginBinaryImportAsync($"COPY {target} FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var row in rows)
        {
            await copy.StartRowAsync(cancellationToken);
            foreach (var value in row)
            {
                switch (value)
                {
                    case null: await copy.WriteNullAsync(cancellationToken); break;
                    case long number: await copy.WriteAsync(number, NpgsqlDbType.Bigint, cancellationToken); break;
                    default: await copy.WriteAsync((string)value, NpgsqlDbType.Text, cancellationToken); break;
                }
            }
        }
        await copy.CompleteAsync(cancellationToken);
    }

    private static async Task SummarizeAsync(SourceImportSession session, DetailsReport report, CancellationToken cancellationToken)
    {
        // A first run loads hundreds of thousands of names; refresh planner statistics so search uses the trigram index.
        await session.ExecuteAsync("""
            ANALYZE reference.wikidata_item; ANALYZE reference.wikidata_common_name; ANALYZE reference.wikidata_external_id;
            """, cancellationToken);
        var totals = await session.ScalarsAsync("""
            SELECT count(*), count("DetailsFetchedAt"), count("EnwikiTitle"), count("ImageFile"),
                (SELECT count(DISTINCT cn."ItemId") FROM reference.wikidata_common_name cn
                 JOIN reference.wikidata_item i ON i."Id" = cn."ItemId"
                 WHERE i."IsCurrent" AND (cn."Language" = 'en' OR cn."Language" LIKE 'en-%'))
            FROM reference.wikidata_item WHERE "IsCurrent"
            """, cancellationToken);
        (report.CurrentItems, report.ItemsWithDetails, report.ItemsWithEnwikiTitle, report.ItemsWithImage, report.ItemsWithEnglishCommonName) =
            (totals[0], totals[1], totals[2], totals[3], totals[4]);
        await using (var command = session.Command($"""
            SELECT cn."Language", count(*) FROM reference.wikidata_common_name cn
            JOIN reference.wikidata_item i ON i."Id" = cn."ItemId" WHERE i."IsCurrent"
            GROUP BY cn."Language" ORDER BY count(*) DESC, cn."Language" LIMIT {DetailsReport.MaximumExamples}
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) report.CommonNamesByLanguage.Add(new(reader.GetString(0), reader.GetInt64(1)));
        await using (var command = session.Command("""
            SELECT e."Property", count(*) FROM reference.wikidata_external_id e
            JOIN reference.wikidata_item i ON i."Id" = e."ItemId" WHERE i."IsCurrent"
            GROUP BY e."Property" ORDER BY e."Property"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) report.ExternalIdCounts[reader.GetString(0)] = reader.GetInt64(1);
    }

    private static void Apply(SourceImport import, DetailsReport report)
    {
        import.RowsRead = report.ItemsFetched;
        import.RowsInserted = report.ItemsInserted + report.CommonNamesInserted + report.ExternalIdsInserted;
        import.RowsUpdated = report.ItemsUpdated;
        import.RowsRetired = report.ItemsRetired + report.CommonNamesDeleted + report.ExternalIdsDeleted;
        import.WarningCount = report.WarningCount;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
