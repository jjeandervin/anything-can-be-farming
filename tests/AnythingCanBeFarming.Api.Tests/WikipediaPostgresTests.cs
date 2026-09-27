using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.DataImport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WikipediaPostgresTests
{
    private const string MapleLead = "Acer palmatum, commonly known as Japanese maple, is a species of woody plant native to Japan, Korea, China, "
        + "eastern Mongolia, and southeast Russia.\nMany different cultivars of this maple have been selected.";

    [PostgresFact]
    public async Task First_run_stages_leads_with_attribution_and_an_unchanged_rerun_fetches_nothing()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await SeedItemsAsync(database, ("Q1", "Acer palmatum"), ("Q2", "Carica jamaicensis"), ("Q3", "Tillandsia polita var. elongata"),
            ("Q4", "Oxalis micrantha"), ("Q5", "Acer nonexistentissimum"), ("Q6", "Empty page"), ("Q7", "Hosta"), ("Q8", "Hosta"),
            ("Q9", "Musa × paradisiaca"), ("Q10", null));
        var fake = Sample();
        fake.ExtractsPerResponse = 2;

        var first = await LeadsAsync(database, fake);
        var report = first.Report;
        Assert.Equal((8, 8, 8, 0), (report.TitlesSelected, report.ArticlesInserted, report.ArticlesChecked, report.ArticlesRetired));
        Assert.Equal((9, 6, 2, 8), (report.LinksInserted, report.LeadsFetched, report.ArticlesUnchanged, report.ArticlesUpdated));
        Assert.Equal((2, 1, 1), (report.RedirectsFollowed, report.RedirectsToDifferentItem, report.MissingPages));
        Assert.True(report.ContinuationRequests > 0);
        Assert.Equal(new Dictionary<string, long>
        {
            ["Ok"] = 4, ["Disambiguation"] = 1, ["EmptyLead"] = 1, ["ItemMismatch"] = 1, ["Missing"] = 1
        }, report.StatusCounts);
        Assert.Equal(8, report.CurrentArticles);
        // Ok leads are 50, 52, 62, and 206 characters long.
        Assert.Equal(new LeadLengths(4, 50, 50, 52, MapleLead.Length, MapleLead.Length), report.OkLeadLengths);
        Assert.Equal(new StubCounts(3, 3, 2), report.OkStubs);
        Assert.Contains(report.Warnings, x => x.Contains("\"Acer nonexistentissimum\" does not exist"));

        await using (var db = database.Context())
        {
            var maple = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Acer palmatum");
            Assert.Equal(("enwiki", "Acer palmatum", 1000L, 500L, "Q1", "Species of maple"),
                (maple.Wiki, maple.Title, maple.PageId!.Value, maple.LastRevId!.Value, maple.WikibaseItem, maple.ShortDescription));
            Assert.Equal((MapleLead, MapleLead.Length, "Ok"), (maple.LeadText, maple.LeadChars, maple.Status));
            Assert.Equal(("https://en.wikipedia.org/wiki/Acer_palmatum", "CC BY-SA 4.0", "https://creativecommons.org/licenses/by-sa/4.0/"),
                (maple.Url, maple.License, maple.LicenseUrl));
            Assert.NotNull(maple.FetchedAt);
            Assert.True(maple.IsCurrent);
            Assert.Equal(first.ImportId, maple.ImportId);

            var papaya = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Carica jamaicensis");
            Assert.Equal(("Papaya", "Q34887", "ItemMismatch", "https://en.wikipedia.org/wiki/Papaya"), (papaya.Title, papaya.WikibaseItem, papaya.Status, papaya.Url));
            Assert.Equal("The papaya is the plant species Carica papaya.", papaya.LeadText);
            var variety = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Tillandsia polita var. elongata");
            Assert.Equal(("Tillandsia polita", "Ok"), (variety.Title, variety.Status));
            var disambiguation = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Oxalis micrantha");
            Assert.Equal(("Disambiguation", 300L), (disambiguation.Status, disambiguation.LastRevId!.Value));
            Assert.Null(disambiguation.LeadText);
            Assert.Null(disambiguation.FetchedAt);
            var missing = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Acer nonexistentissimum");
            Assert.Equal("Missing", missing.Status);
            Assert.Null(missing.Url);
            var empty = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Empty page");
            Assert.Equal(("EmptyLead", "", 0), (empty.Status, empty.LeadText, empty.LeadChars!.Value));
            var hybrid = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Musa × paradisiaca");
            Assert.Equal("https://en.wikipedia.org/wiki/Musa_%C3%97_paradisiaca", hybrid.Url);

            var hosta = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Hosta");
            Assert.Equal("Ok", hosta.Status); // Q7 and Q8 carry the same sitelink; the page names Q7.
            Assert.Equal(2, await db.WikipediaItemArticles.CountAsync(x => x.ArticleId == hosta.Id));

            var import = await db.SourceImports.SingleAsync();
            Assert.Equal(("Wikipedia", "Leads", "Succeeded", 8L, 17L), (import.Source, import.Kind, import.Status, import.RowsRead, import.RowsInserted));
            Assert.Contains("\"GardenCheck\"", import.ValidationJson);
        }

        var before = await RowVersionsAsync(database);
        fake.Requests.Clear();
        var second = await LeadsAsync(database, fake);
        Assert.Equal((8, 0, 8, 0), (second.Report.ArticlesChecked, second.Report.LeadsFetched, second.Report.ArticlesUnchanged, second.Report.ArticlesUpdated));
        Assert.Equal((0, 0, 0, 0), (second.Report.ArticlesInserted, second.Report.LinksInserted, second.Report.ArticlesRetired, second.Report.LinksRemoved));
        Assert.Equal(0, fake.LeadRequests);
        Assert.Equal(1, fake.Requests.Count(x => x.Kind == "check"));
        Assert.Equal(before, await RowVersionsAsync(database));
    }

    [PostgresFact]
    public async Task A_changed_revision_triggers_exactly_one_refetch()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await SeedItemsAsync(database, ("Q1", "Acer palmatum"), ("Q7", "Hosta"), ("Q2", "Carica jamaicensis"));
        var fake = Sample();
        await LeadsAsync(database, fake);
        fake.Requests.Clear();
        fake.Pages["Acer palmatum"].Revision = 501;
        fake.Pages["Acer palmatum"].Lead = "Acer palmatum is a species of maple.";
        fake.Pages["Hosta"].ShortDescription = "Genus of plants"; // Metadata refreshes without a lead refetch.

        var changed = await LeadsAsync(database, fake);
        Assert.Equal((1, 2, 2), (changed.Report.LeadsFetched, changed.Report.ArticlesUnchanged, changed.Report.ArticlesUpdated));
        Assert.Equal(["Acer palmatum"], fake.LeadTitles);
        await using var db = database.Context();
        var maple = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Acer palmatum");
        Assert.Equal(("Acer palmatum is a species of maple.", 501L, 36, changed.ImportId),
            (maple.LeadText, maple.LastRevId!.Value, maple.LeadChars!.Value, maple.ImportId));
        var hosta = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Hosta");
        Assert.Equal(("Genus of plants", 700L), (hosta.ShortDescription, hosta.LastRevId!.Value));
    }

    [PostgresFact]
    public async Task A_removed_sitelink_retires_the_article_without_deleting_it_and_a_returning_one_is_reactivated()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await SeedItemsAsync(database, ("Q1", "Acer palmatum"), ("Q7", "Hosta"));
        var fake = Sample();
        await LeadsAsync(database, fake);
        await ExecuteAsync(database, """UPDATE reference.wikidata_item SET "EnwikiTitle" = NULL WHERE "Qid" = 'Q1'""");
        fake.Pages.Remove("Acer palmatum");
        fake.Requests.Clear();

        var removal = await LeadsAsync(database, fake);
        Assert.Equal((1, 1, 1, 1), (removal.Report.TitlesSelected, removal.Report.ArticlesRetired, removal.Report.LinksRemoved, removal.Report.ArticlesChecked));
        Assert.DoesNotContain("Acer palmatum", fake.Requests.SelectMany(x => x.Titles));
        await using (var db = database.Context())
        {
            var maple = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Acer palmatum");
            Assert.Equal((false, "Ok", MapleLead, removal.ImportId), (maple.IsCurrent, maple.Status, maple.LeadText, maple.ImportId));
            Assert.Equal(0, await db.WikipediaItemArticles.CountAsync(x => x.ArticleId == maple.Id));
            Assert.Equal(1, removal.Report.StatusCounts.Values.Sum());
        }

        // A page deleted on Wikipedia while its sitelink remains keeps its text and becomes Missing.
        await ExecuteAsync(database, """UPDATE reference.wikidata_item SET "EnwikiTitle" = 'Acer palmatum' WHERE "Qid" = 'Q1'""");
        var restored = await LeadsAsync(database, fake);
        Assert.Equal((1, 1), (restored.Report.ArticlesReactivated, restored.Report.LinksInserted));
        await using (var db = database.Context())
        {
            var maple = await db.WikipediaArticles.SingleAsync(x => x.RequestedTitle == "Acer palmatum");
            Assert.Equal((true, "Missing", MapleLead, 500L), (maple.IsCurrent, maple.Status, maple.LeadText, maple.LastRevId!.Value));
        }
    }

    [PostgresFact]
    public async Task An_interrupted_run_keeps_committed_batches_and_resumes_through_the_revision_check()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = new FakeWikipedia();
        var items = Enumerable.Range(1, 5).Select(i => ($"Q{i}", (string?)$"Plant {i}")).ToArray();
        await SeedItemsAsync(database, items);
        for (var i = 1; i <= 5; i++) fake.Add($"Plant {i}", 10 * i, $"Q{i}", $"Plant {i} is a species of plant in a family of plants that grow somewhere.");
        fake.LeadRequestOverride = count => count == 3 ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) : null;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LeadsAsync(database, fake, commitSize: 2));
        Assert.Contains("HTTP 400", error.Message);
        string pending;
        await using (var db = database.Context())
        {
            Assert.Equal(4, await db.WikipediaArticles.CountAsync(x => x.FetchedAt != null && x.Status == "Ok"));
            pending = (await db.WikipediaArticles.SingleAsync(x => x.Status == "Pending")).RequestedTitle;
            var failed = await db.SourceImports.SingleAsync();
            Assert.Equal(("Failed", 5L), (failed.Status, failed.RowsRead));
            Assert.DoesNotContain("wikipedia.org", failed.ErrorMessage);
        }

        fake.LeadRequestOverride = null;
        fake.Requests.Clear();
        var resumed = await LeadsAsync(database, fake, commitSize: 2);
        Assert.Equal((5, 1, 4, 1), (resumed.Report.ArticlesChecked, resumed.Report.LeadsFetched, resumed.Report.ArticlesUnchanged, resumed.Report.ArticlesUpdated));
        Assert.Equal([pending], fake.LeadTitles);
        Assert.Equal(new Dictionary<string, long> { ["Ok"] = 5 }, resumed.Report.StatusCounts);
        await using (var db = database.Context())
            Assert.Equal(["Failed", "Succeeded"], await db.SourceImports.OrderBy(x => x.Id).Select(x => x.Status).ToListAsync());
    }

    [PostgresFact]
    public async Task Full_refetches_every_lead_but_rewrites_only_changed_rows_and_limit_stops_early()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await SeedItemsAsync(database, ("Q1", "Acer palmatum"), ("Q7", "Hosta"), ("Q4", "Oxalis micrantha"), ("Q2", "Carica jamaicensis"));
        var fake = Sample();
        await LeadsAsync(database, fake);
        var before = await RowVersionsAsync(database);
        fake.Requests.Clear();

        var full = await LeadsAsync(database, fake, full: true);
        Assert.Equal((4, 3, 0), (full.Report.ArticlesChecked, full.Report.LeadsFetched, full.Report.ArticlesUpdated));
        Assert.Equal(["Acer palmatum", "Carica jamaicensis", "Hosta"], fake.LeadTitles.Order());
        Assert.Equal(before, await RowVersionsAsync(database));

        fake.Requests.Clear();
        var limited = await LeadsAsync(database, fake, full: true, limit: 2);
        Assert.Equal(2, limited.Report.ArticlesChecked);
        Assert.Equal(2, fake.Requests.Single(x => x.Kind == "check").Titles.Length);
        await using var db = database.Context();
        using var parameters = System.Text.Json.JsonDocument.Parse(
            (await db.SourceImports.OrderByDescending(x => x.Id).Select(x => x.ParametersJson).FirstAsync())!);
        Assert.True(parameters.RootElement.GetProperty("full").GetBoolean());
        Assert.Equal(2, parameters.RootElement.GetProperty("limit").GetInt32());
    }

    [PostgresFact]
    public async Task Report_resolves_the_garden_check_and_ranks_through_wfo_and_wikidata_links()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await WikidataPostgresTests.ImportBackboneAsync(database, "2026-09",
            WikidataPostgresTests.Taxon("wfo-0000000001", "Acer palmatum"),
            WikidataPostgresTests.Taxon("wfo-0000000002", "Acer polymorphum", "Synonym", "wfo-0000000001"),
            WikidataPostgresTests.Taxon("wfo-0000000003", "Hosta", rank: "genus"),
            WikidataPostgresTests.Taxon("wfo-0000000004", "Tillandsia polita var. elongata", rank: "variety"),
            WikidataPostgresTests.Taxon("wfo-0000000005", "Carica jamaicensis"));
        var wikidata = new FakeWikidata();
        foreach (var (qid, wfo, title) in new[]
        {
            ("Q1", "wfo-0000000001", "Acer palmatum"), ("Q20", "wfo-0000000002", "Acer polymorphum"), ("Q7", "wfo-0000000003", "Hosta"),
            ("Q3", "wfo-0000000004", "Tillandsia polita var. elongata"), ("Q2", "wfo-0000000005", "Carica jamaicensis")
        })
        {
            wikidata.Add(qid, wfo);
            wikidata.AddEntity(qid, 1, title, title, ("P7715", FakeWikidata.Value(wfo)));
        }
        await WikidataPostgresTests.CrosswalkAsync(database, wikidata);
        await WikidataPostgresTests.DetailsAsync(database, wikidata);
        var fake = Sample();
        fake.Add("Acer polymorphum", 900, "Q999", "Acer polymorphum is an obsolete name.");

        var report = (await LeadsAsync(database, fake)).Report;
        Assert.Equal([new RankCoverage("species", 3, 1), new RankCoverage("genus", 1, 1), new RankCoverage("infraspecific", 1, 1)], report.ByRank);
        var garden = report.GardenCheck.ToDictionary(x => x.Qid ?? x.Name);
        // Acer palmatum's own item comes first; the synonym's item follows through its accepted taxon.
        Assert.Equal(["Q1", "Q20"], report.GardenCheck.Where(x => x.Name == "Acer palmatum").Select(x => x.Qid));
        Assert.Equal(("Acer palmatum", "Ok", MapleLead[..150]), (garden["Q1"].Title, garden["Q1"].Status, garden["Q1"].LeadStart));
        Assert.Equal(("Hosta", "Ok"), (garden["Q7"].Title, garden["Q7"].Status));
        Assert.Equal("ItemMismatch", garden["Q20"].Status);
        foreach (var name in new[] { "ACSA3 (Acer saccharum)", "Rudbeckia hirta", "Echinacea purpurea", "Cornus florida" })
            Assert.Equal(new GardenCheck(name, null, null, null, null), garden[name]);
    }

    // Sample pages. Hosta's lead is under 200 characters and matches the one-sentence stub pattern.
    internal static FakeWikipedia Sample()
    {
        var fake = new FakeWikipedia();
        fake.Add("Acer palmatum", 500, "Q1", MapleLead, "Species of maple");
        fake.Add("Papaya", 600, "Q34887", "The papaya is the plant species Carica papaya.", "Species of tropical fruit plant");
        fake.Redirects["Carica jamaicensis"] = "Papaya";
        fake.Add("Tillandsia polita", 650, "Q3", "Tillandsia polita is a species of flowering plant.");
        fake.Redirects["Tillandsia polita var. elongata"] = "Tillandsia polita";
        fake.Add("Oxalis micrantha", 300, "Q4", "Oxalis micrantha may refer to:", "Topics referred to by the same term", disambiguation: true);
        fake.Add("Empty page", 400, "Q6", "");
        fake.Add("Hosta", 700, "Q7", "Hosta is a genus of plants commonly known as hostas.", "Genus of flowering plants");
        fake.Add("Musa × paradisiaca", 800, "Q9", "Musa × paradisiaca is a species of banana. It is widely grown.");
        return fake;
    }

    private static async Task SeedItemsAsync(WfoPostgresTests.TestDatabase database, params (string Qid, string? Title)[] items)
    {
        await using var db = database.Context();
        foreach (var (qid, title) in items) db.WikidataItems.Add(new WikidataItem { Qid = qid, EnwikiTitle = title, IsCurrent = true });
        await db.SaveChangesAsync();
    }

    private static async Task<SourceImportResult<LeadsReport>> LeadsAsync(WfoPostgresTests.TestDatabase database, FakeWikipedia fake,
        bool full = false, int? limit = null, int commitSize = 500)
    {
        using var clients = fake.Clients();
        return await new WikipediaLeadsImporter(database.ConnectionString, clients.Api, TextWriter.Null) { CommitSize = commitSize }
            .ImportAsync(full, limit);
    }

    private static async Task ExecuteAsync(WfoPostgresTests.TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // xmin changes whenever PostgreSQL rewrites a row, even if the values are identical.
    private static async Task<List<string>> RowVersionsAsync(WfoPostgresTests.TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT 'article:' || "RequestedTitle" || ':' || xmin::text FROM reference.wikipedia_article
            UNION ALL SELECT 'link:' || "ItemId" || ':' || "ArticleId" || ':' || xmin::text FROM reference.wikipedia_item_article
            ORDER BY 1
            """, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }
}
