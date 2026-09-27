using System.Text.Json;
using AnythingCanBeFarming.Data;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record LeadLengths(long Articles, long Min, long P10, long Median, long P90, long Max);
public sealed record StubCounts(long StubLike, long UnderTwoHundredChars, long SingleSentenceSpeciesOrGenus);
public sealed record RankCoverage(string Rank, long Articles, long Ok);
public sealed record GardenCheck(string Name, string? Qid, string? Title, string? Status, string? LeadStart);

public sealed class LeadsReport
{
    public const int MaximumExamples = 20;
    public long TitlesSelected { get; set; }
    public long ArticlesInserted { get; set; }
    public long ArticlesReactivated { get; set; }
    public long ArticlesRetired { get; set; }
    public long LinksInserted { get; set; }
    public long LinksRemoved { get; set; }
    public long ArticlesChecked { get; set; }
    public long LeadsFetched { get; set; }
    public long ArticlesUnchanged { get; set; }
    public long ArticlesUpdated { get; set; }
    public long ContinuationRequests { get; set; }
    public long BatchesCommitted { get; set; }
    public long MissingPages { get; set; }
    public long RedirectsFollowed { get; set; }
    public long RedirectsToDifferentItem { get; set; }
    public long CurrentArticles { get; set; }
    public Dictionary<string, long> StatusCounts { get; } = [];
    public LeadLengths? OkLeadLengths { get; set; }
    public StubCounts? OkStubs { get; set; }
    public List<RankCoverage> ByRank { get; } = [];
    public List<GardenCheck> GardenCheck { get; } = [];
    public long WarningCount { get; set; }
    public List<string> Warnings { get; } = [];

    public void Warn(string message)
    {
        WarningCount++;
        if (Warnings.Count < MaximumExamples) Warnings.Add(message);
    }
}

// Stages English Wikipedia lead sections for the current Wikidata sitelinks. Incremental by lastrevid; commits every
// CommitSize articles, so an interrupted run resumes through the revision check. Lead text is stored exactly as returned.
public sealed class WikipediaLeadsImporter(string connectionString, WikipediaApiClient api, TextWriter output)
{
    public const string Source = "Wikipedia";
    public const string Wiki = "enwiki";
    public int CommitSize { get; init; } = 500;

    private static readonly string[] InfraspecificRanks = ["subspecies", "variety", "subvariety", "form", "subform", "prole", "lusus", "convar"];

    // ACSA3 resolves through its USDA link; every entry then goes WFO taxon → Wikidata link → sitelink, never by title.
    private static readonly (string Name, string? UsdaSymbol, string ScientificName)[] Garden =
    [
        ("ACSA3 (Acer saccharum)", "ACSA3", "Acer saccharum"), ("Acer palmatum", null, "Acer palmatum"),
        ("Rudbeckia hirta", null, "Rudbeckia hirta"), ("Echinacea purpurea", null, "Echinacea purpurea"),
        ("Hosta", null, "Hosta"), ("Cornus florida", null, "Cornus florida")
    ];

    public async Task<SourceImportResult<LeadsReport>> ImportAsync(bool full, int? limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1) throw new InvalidDataException("--limit must be a positive number.");
        await using var session = await SourceImportSession.OpenAsync(connectionString, Source, cancellationToken);
        var import = await session.StartAsync("Leads", new { command = "leads", full, limit }, cancellationToken);
        output.WriteLine($"Import {import.Id}: Wikipedia leads ({(full ? "full" : "incremental")}{(limit is { } n ? $", limit {n:N0}" : "")})");
        var report = new LeadsReport();
        var continuations = api.ContinuationRequests;
        try
        {
            await SelectAsync(session, import.Id, report, cancellationToken);
            output.WriteLine($"Selected {report.TitlesSelected:N0} titles; {report.ArticlesInserted:N0} new, {report.ArticlesRetired:N0} retired.");
            await session.ExecuteAsync("""
                DROP TABLE IF EXISTS leads_article;
                CREATE TEMP TABLE leads_article (id bigint PRIMARY KEY, title text, page_id bigint, last_rev_id bigint, wikibase_item text,
                    short_description text, lead_text text, status text NOT NULL, url text, fetched_at timestamptz);
                """, cancellationToken);
            long lastId = 0;
            while (limit is not { } max || report.ArticlesChecked < max)
            {
                var size = (int)Math.Min(CommitSize, (limit ?? long.MaxValue) - report.ArticlesChecked);
                var chunk = await NextChunkAsync(session, lastId, size, cancellationToken);
                if (chunk.Count == 0) break;
                lastId = chunk[^1].Id;
                report.ArticlesChecked += chunk.Count;
                var articles = await RefreshAsync(chunk, full, report, cancellationToken);
                await PublishAsync(session, import.Id, articles, report, cancellationToken);
                report.BatchesCommitted++;
                if (report.BatchesCommitted % 20 == 0)
                    output.WriteLine($"Checked {report.ArticlesChecked:N0} articles; fetched {report.LeadsFetched:N0} leads, updated {report.ArticlesUpdated:N0}…");
            }
            Finish(report, continuations);
            await SummarizeAsync(session, report, cancellationToken);
            Apply(import, report);
            await session.CompleteAsync(import, "Succeeded", report, null);
        }
        catch (Exception exception)
        {
            // Committed batches stay published; the next run skips them through the lastrevid check.
            var (status, message) = SourceImportSession.Describe(exception, cancellationToken);
            Finish(report, continuations);
            Apply(import, report);
            await session.CompleteAsync(import, status, report, message);
            output.WriteLine(JsonSerializer.Serialize(report, Indented));
            throw new InvalidDataException($"Import {import.Id}: {message}", exception);
        }
        output.WriteLine(JsonSerializer.Serialize(report, Indented));
        return new(import.Id, report);
    }

    private void Finish(LeadsReport report, long continuationsBefore)
    {
        report.ArticlesUnchanged = report.ArticlesChecked - report.LeadsFetched;
        report.ContinuationRequests = api.ContinuationRequests - continuationsBefore;
    }

    // §2.1: one article per distinct sitelink title; links mirror the current sitelinks. Articles are retired, never deleted.
    private static async Task SelectAsync(SourceImportSession session, long importId, LeadsReport report, CancellationToken cancellationToken)
    {
        (string, object)[] parameters = [("import", importId), ("wiki", Wiki)];
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        var selected = await session.ScalarsAsync("""
            WITH titles AS (
                SELECT DISTINCT "EnwikiTitle" AS title FROM reference.wikidata_item WHERE "IsCurrent" AND "EnwikiTitle" IS NOT NULL),
            upserted AS (
                INSERT INTO reference.wikipedia_article AS a ("Wiki", "RequestedTitle", "Status", "License", "LicenseUrl", "IsCurrent", "ImportId")
                SELECT @wiki, title, @pending, @license, @licenseUrl, true, @import FROM titles
                ON CONFLICT ("Wiki", "RequestedTitle") DO UPDATE SET "IsCurrent" = true, "ImportId" = EXCLUDED."ImportId"
                WHERE NOT a."IsCurrent"
                RETURNING xmax = 0 AS inserted)
            SELECT (SELECT count(*) FROM titles), count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM upserted
            """, cancellationToken, [.. parameters, ("pending", WikipediaArticleStatus.Pending),
                ("license", WikipediaLicense.Name), ("licenseUrl", WikipediaLicense.Url)]);
        (report.TitlesSelected, report.ArticlesInserted, report.ArticlesReactivated) = (selected[0], selected[1], selected[2]);
        report.ArticlesRetired = (await session.ScalarsAsync("""
            WITH retired AS (
                UPDATE reference.wikipedia_article a SET "IsCurrent" = false, "ImportId" = @import
                WHERE a."Wiki" = @wiki AND a."IsCurrent" AND NOT EXISTS (SELECT 1 FROM reference.wikidata_item i
                    WHERE i."IsCurrent" AND i."EnwikiTitle" = a."RequestedTitle")
                RETURNING 1)
            SELECT count(*) FROM retired
            """, cancellationToken, parameters))[0];
        report.LinksRemoved = (await session.ScalarsAsync("""
            WITH removed AS (
                DELETE FROM reference.wikipedia_item_article ia USING reference.wikipedia_article a
                WHERE ia."ArticleId" = a."Id" AND a."Wiki" = @wiki AND NOT EXISTS (SELECT 1 FROM reference.wikidata_item i
                    WHERE i."Id" = ia."ItemId" AND i."IsCurrent" AND i."EnwikiTitle" = a."RequestedTitle")
                RETURNING 1)
            SELECT count(*) FROM removed
            """, cancellationToken, parameters))[0];
        report.LinksInserted = (await session.ScalarsAsync("""
            WITH inserted AS (
                INSERT INTO reference.wikipedia_item_article ("ItemId", "ArticleId")
                SELECT i."Id", a."Id" FROM reference.wikidata_item i
                JOIN reference.wikipedia_article a ON a."Wiki" = @wiki AND a."RequestedTitle" = i."EnwikiTitle"
                WHERE i."IsCurrent"
                ON CONFLICT DO NOTHING
                RETURNING 1)
            SELECT count(*) FROM inserted
            """, cancellationToken, parameters))[0];
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record Article(long Id, string RequestedTitle, string? Title, long? PageId, long? LastRevId, string? WikibaseItem,
        string? ShortDescription, string? LeadText, string Status, string? Url, DateTimeOffset? FetchedAt, string[] Qids);

    private static async Task<List<Article>> NextChunkAsync(SourceImportSession session, long lastId, int size, CancellationToken cancellationToken)
    {
        await using var command = session.Command("""
            SELECT a."Id", a."RequestedTitle", a."Title", a."PageId", a."LastRevId", a."WikibaseItem", a."ShortDescription", a."LeadText",
                a."Status", a."Url", a."FetchedAt",
                ARRAY(SELECT i."Qid" FROM reference.wikipedia_item_article ia JOIN reference.wikidata_item i ON i."Id" = ia."ItemId"
                      WHERE ia."ArticleId" = a."Id" ORDER BY i."Qid")
            FROM reference.wikipedia_article a
            WHERE a."Wiki" = @wiki AND a."IsCurrent" AND a."Id" > @last ORDER BY a."Id" LIMIT @size
            """);
        command.Parameters.AddWithValue("wiki", Wiki);
        command.Parameters.AddWithValue("last", lastId);
        command.Parameters.AddWithValue("size", size);
        var articles = new List<Article>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            articles.Add(new(reader.GetInt64(0), reader.GetString(1), Text(reader, 2), Number(reader, 3), Number(reader, 4), Text(reader, 5),
                Text(reader, 6), Text(reader, 7), reader.GetString(8), Text(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<string[]>(11)));
        return articles;
    }

    // §2.2 and §2.3: check every article's metadata, fetch leads only where needed, then apply the §3 status rules.
    private async Task<List<Article>> RefreshAsync(List<Article> chunk, bool full, LeadsReport report, CancellationToken cancellationToken)
    {
        var checks = new Dictionary<string, WikipediaPageInfo>(StringComparer.Ordinal);
        foreach (var article in chunk.Where(x => !WikipediaApiClient.IsRequestable(x.RequestedTitle)))
            report.Warn($"Title {Quote(article.RequestedTitle)} cannot be requested from Wikipedia; it is treated as missing.");
        foreach (var batch in chunk.Select(x => x.RequestedTitle).Where(WikipediaApiClient.IsRequestable).Chunk(WikipediaApiClient.MaximumCheckTitles))
            foreach (var info in await api.CheckAsync(batch, cancellationToken)) checks[info.RequestedTitle] = info;

        var toFetch = chunk.Where(x => checks.TryGetValue(x.RequestedTitle, out var info) && !info.Missing && !info.IsDisambiguation
            && (full || x.FetchedAt == null || info.LastRevId != x.LastRevId)).Select(x => x.RequestedTitle).ToList();
        var leads = new Dictionary<string, WikipediaLead>(StringComparer.Ordinal);
        foreach (var batch in toFetch.Chunk(WikipediaApiClient.MaximumLeadTitles))
            foreach (var lead in await api.GetLeadsAsync(batch, cancellationToken)) leads[lead.RequestedTitle] = lead;

        var fetchedAt = DateTimeOffset.UtcNow;
        return chunk.Select(article => Refresh(article, checks.GetValueOrDefault(article.RequestedTitle),
            leads.GetValueOrDefault(article.RequestedTitle), fetchedAt, report)).ToList();
    }

    private static Article Refresh(Article article, WikipediaPageInfo? info, WikipediaLead? lead, DateTimeOffset fetchedAt, LeadsReport report)
    {
        // A page that is gone (or vanished between check and fetch) keeps its previous text and attribution.
        if (info is null or { Missing: true } || lead is { Missing: true })
        {
            report.MissingPages++;
            report.Warn($"{Quote(article.RequestedTitle)} does not exist on Wikipedia; any stored lead was kept.");
            return article with { Status = WikipediaArticleStatus.Missing };
        }
        var next = article with
        {
            Title = info.Title, PageId = info.PageId, WikibaseItem = info.WikibaseItem, ShortDescription = info.ShortDescription,
            Url = info.Title == null ? null : WikipediaUrl.For(info.Title)
        };
        if (info.IsDisambiguation)
            next = next with { LeadText = null, LastRevId = info.LastRevId, FetchedAt = null };
        else if (lead != null)
        {
            // The revision comes from the same response as the text; if it moved since the check, the next run catches up.
            next = next with { LeadText = lead.Extract, LastRevId = lead.LastRevId, FetchedAt = fetchedAt };
            report.LeadsFetched++;
        }
        var status = WikipediaLeadStatus.Evaluate(false, info.IsDisambiguation, next.WikibaseItem, next.Qids, next.LeadText);
        if (info.Redirected)
        {
            report.RedirectsFollowed++;
            if (status == WikipediaArticleStatus.ItemMismatch) report.RedirectsToDifferentItem++;
        }
        return next with { Status = status };
    }

    private static async Task PublishAsync(SourceImportSession session, long importId, List<Article> articles, LeadsReport report,
        CancellationToken cancellationToken)
    {
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        await session.ExecuteAsync("TRUNCATE leads_article", cancellationToken);
        await using (var copy = await session.Connection.BeginBinaryImportAsync(
            "COPY leads_article (id, title, page_id, last_rev_id, wikibase_item, short_description, lead_text, status, url, fetched_at) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var article in articles)
            {
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(article.Id, NpgsqlDbType.Bigint, cancellationToken);
                await WriteAsync(copy, article.Title, cancellationToken);
                await WriteAsync(copy, article.PageId, cancellationToken);
                await WriteAsync(copy, article.LastRevId, cancellationToken);
                await WriteAsync(copy, article.WikibaseItem, cancellationToken);
                await WriteAsync(copy, article.ShortDescription, cancellationToken);
                await WriteAsync(copy, article.LeadText, cancellationToken);
                await WriteAsync(copy, article.Status, cancellationToken);
                await WriteAsync(copy, article.Url, cancellationToken);
                if (article.FetchedAt is { } fetched) await copy.WriteAsync(fetched, NpgsqlDbType.TimestampTz, cancellationToken);
                else await copy.WriteNullAsync(cancellationToken);
            }
            await copy.CompleteAsync(cancellationToken);
        }
        // Only rows whose content changed are rewritten. A refetch of an identical revision keeps its original retrieval time.
        report.ArticlesUpdated += (await session.ScalarsAsync("""
            WITH updated AS (
                UPDATE reference.wikipedia_article a SET "Title" = s.title, "PageId" = s.page_id, "LastRevId" = s.last_rev_id,
                    "WikibaseItem" = s.wikibase_item, "ShortDescription" = s.short_description, "LeadText" = s.lead_text,
                    "Status" = s.status, "Url" = s.url, "FetchedAt" = s.fetched_at, "ImportId" = @import
                FROM leads_article s
                WHERE a."Id" = s.id AND (a."Title", a."PageId", a."LastRevId", a."WikibaseItem", a."ShortDescription", a."LeadText",
                        a."Status", a."Url", a."FetchedAt" IS NULL)
                    IS DISTINCT FROM (s.title, s.page_id, s.last_rev_id, s.wikibase_item, s.short_description, s.lead_text,
                        s.status, s.url, s.fetched_at IS NULL)
                RETURNING 1)
            SELECT count(*) FROM updated
            """, cancellationToken, ("import", importId)))[0];
        await transaction.CommitAsync(cancellationToken);
    }

    // §4, over all current articles (not only this run's). Lengths and stubs describe usable (Ok) leads.
    private static async Task SummarizeAsync(SourceImportSession session, LeadsReport report, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync("ANALYZE reference.wikipedia_article; ANALYZE reference.wikipedia_item_article;", cancellationToken);
        await ReadAsync(session, """
            SELECT "Status", count(*) FROM reference.wikipedia_article WHERE "Wiki" = @wiki AND "IsCurrent"
            GROUP BY "Status" ORDER BY count(*) DESC, "Status"
            """, reader => report.StatusCounts[reader.GetString(0)] = reader.GetInt64(1), cancellationToken, ("wiki", Wiki));
        report.CurrentArticles = report.StatusCounts.Values.Sum();
        var lengths = await session.ScalarsAsync("""
            SELECT count(*), min("LeadChars"), percentile_disc(0.1) WITHIN GROUP (ORDER BY "LeadChars"),
                percentile_disc(0.5) WITHIN GROUP (ORDER BY "LeadChars"), percentile_disc(0.9) WITHIN GROUP (ORDER BY "LeadChars"), max("LeadChars")
            FROM reference.wikipedia_article WHERE "Wiki" = @wiki AND "IsCurrent" AND "Status" = @ok
            """, cancellationToken, ("wiki", Wiki), ("ok", WikipediaArticleStatus.Ok));
        report.OkLeadLengths = new(lengths[0], lengths[1], lengths[2], lengths[3], lengths[4], lengths[5]);
        // Stub-like: under 200 characters, or one sentence of the form "X is a species/genus of …".
        var stubs = await session.ScalarsAsync("""
            SELECT count(*) FILTER (WHERE short OR single), count(*) FILTER (WHERE short), count(*) FILTER (WHERE single)
            FROM (SELECT "LeadChars" < 200 AS short,
                    "LeadText" ~ '^[^.]+ is a (species|genus) of' AND "LeadText" !~ '[.!?]\s+\S' AS single
                  FROM reference.wikipedia_article WHERE "Wiki" = @wiki AND "IsCurrent" AND "Status" = @ok) leads
            """, cancellationToken, ("wiki", Wiki), ("ok", WikipediaArticleStatus.Ok));
        report.OkStubs = new(stubs[0], stubs[1], stubs[2]);
        await ReadAsync(session, """
            SELECT rank, count(*), count(*) FILTER (WHERE status = @ok) FROM (
                SELECT DISTINCT a."Id", a."Status" AS status,
                    CASE WHEN t."Id" IS NULL THEN 'unlinked' WHEN t."TaxonRank" = 'species' THEN 'species'
                         WHEN t."TaxonRank" = 'genus' THEN 'genus' WHEN t."TaxonRank" = ANY(@infraspecific) THEN 'infraspecific'
                         ELSE 'other' END AS rank
                FROM reference.wikipedia_article a
                JOIN reference.wikipedia_item_article ia ON ia."ArticleId" = a."Id"
                LEFT JOIN reference.wikidata_wfo_link l ON l."ItemId" = ia."ItemId" AND l."IsCurrent"
                LEFT JOIN reference.wfo_taxon t ON t."Id" = l."WfoTaxonId"
                WHERE a."Wiki" = @wiki AND a."IsCurrent") ranked
            GROUP BY rank ORDER BY array_position(ARRAY['species', 'genus', 'infraspecific', 'other', 'unlinked'], rank)
            """, reader => report.ByRank.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2))), cancellationToken,
            ("wiki", Wiki), ("ok", WikipediaArticleStatus.Ok), ("infraspecific", InfraspecificRanks));
        foreach (var (name, symbol, scientificName) in Garden)
        {
            var found = false;
            await ReadAsync(session, """
                WITH taxon AS (
                    SELECT COALESCE(
                        (SELECT "AcceptedWfoTaxonId" FROM reference.usda_wfo_link WHERE "Symbol" = @symbol AND "AcceptedWfoTaxonId" IS NOT NULL),
                        (SELECT "Id" FROM reference.wfo_taxon WHERE "IsCurrent" AND "ScientificName" = @name AND "TaxonomicStatus" = 'Accepted'
                         ORDER BY "Id" LIMIT 1)) AS id)
                SELECT i."Qid", a."Title", a."Status", left(a."LeadText", 150)
                FROM taxon
                JOIN reference.wikidata_wfo_link l ON l."IsCurrent" AND (l."WfoTaxonId" = taxon.id OR l."AcceptedWfoTaxonId" = taxon.id)
                JOIN reference.wikidata_item i ON i."Id" = l."ItemId" AND i."IsCurrent"
                JOIN reference.wikipedia_item_article ia ON ia."ItemId" = i."Id"
                JOIN reference.wikipedia_article a ON a."Id" = ia."ArticleId" AND a."IsCurrent"
                ORDER BY l."WfoTaxonId" = taxon.id DESC, i."Id" LIMIT 3
                """, reader =>
                {
                    found = true;
                    report.GardenCheck.Add(new(name, reader.GetString(0), Text(reader, 1), reader.GetString(2), Text(reader, 3)));
                }, cancellationToken, ("symbol", symbol ?? ""), ("name", scientificName));
            if (!found) report.GardenCheck.Add(new(name, null, null, null, null));
        }
    }

    private static async Task ReadAsync(SourceImportSession session, string sql, Action<NpgsqlDataReader> row, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = session.Command(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) row(reader);
    }

    private static async Task WriteAsync(NpgsqlBinaryImporter copy, string? value, CancellationToken cancellationToken)
    {
        if (value == null) await copy.WriteNullAsync(cancellationToken);
        else await copy.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
    }

    private static async Task WriteAsync(NpgsqlBinaryImporter copy, long? value, CancellationToken cancellationToken)
    {
        if (value is { } number) await copy.WriteAsync(number, NpgsqlDbType.Bigint, cancellationToken);
        else await copy.WriteNullAsync(cancellationToken);
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? Number(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    // Titles are untrusted source text; JSON quoting escapes anything unprintable before it reaches the console or the report.
    private static string Quote(string title) => JsonSerializer.Serialize(title);

    private static void Apply(SourceImport import, LeadsReport report)
    {
        import.RowsRead = report.ArticlesChecked;
        import.RowsInserted = report.ArticlesInserted + report.LinksInserted;
        import.RowsUpdated = report.ArticlesUpdated + report.ArticlesReactivated;
        import.RowsRetired = report.ArticlesRetired + report.LinksRemoved;
        import.WarningCount = report.WarningCount;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
