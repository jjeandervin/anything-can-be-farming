using AnythingCanBeFarming.DataImport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WikidataPostgresTests
{
    [PostgresFact]
    public async Task Migration_creates_wikidata_tables_and_trigram_indexes()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'"));
        foreach (var table in new[] { "source_import", "wikidata_item", "wikidata_wfo_link", "wikidata_external_id", "wikidata_common_name" })
            Assert.Equal(1L, await ScalarAsync(connection,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'reference' AND table_name = '{table}'"));

        foreach (var index in new[] { "IX_wfo_taxon_ScientificName_trgm", "IX_wfo_taxon_Genus_trgm", "IX_wikidata_common_name_NormalizedName_trgm" })
        {
            await using var command = new NpgsqlCommand(
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'reference' AND indexname = @name", connection);
            command.Parameters.AddWithValue("name", index);
            var definition = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(definition);
            Assert.Contains("USING gin", definition);
            Assert.Contains("gin_trgm_ops", definition);
        }

        await using var db = database.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await db.WikidataWfoLinks.CountAsync());
    }

    [PostgresFact]
    public async Task Crosswalk_publishes_and_an_unchanged_rerun_touches_nothing()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = Sample();
        var first = await CrosswalkAsync(database, fake);
        Assert.Equal(5, first.Report.RowsRead);
        Assert.Equal(0, first.Report.DeprecatedStatementsSkipped); // The query service filters deprecated statements.
        Assert.Equal(1, first.Report.DuplicateStatementsCollapsed);
        Assert.Equal(4, first.Report.Links);
        Assert.Equal(3, first.Report.DistinctQids);
        Assert.Equal(3, first.Report.DistinctWfoIds);
        Assert.Equal(4, first.Report.LinksInserted);
        Assert.Equal(3, first.Report.ItemsInserted);
        var multiple = first.Report.ItemsWithMultipleWfoIds!;
        Assert.Equal(1, multiple.Count);
        Assert.Equal(new MultiValueExample("Q2", ["wfo-0000000002", "wfo-0000000003"]), Assert.Single(multiple.Examples), Comparer);
        Assert.Equal("wfo-0000000001", Assert.Single(first.Report.WfoIdsOnMultipleItems!.Examples).Key);

        await using (var db = database.Context())
        {
            var link = await db.WikidataWfoLinks.SingleAsync(x => x.Qid == "Q1");
            Assert.Equal("preferred", link.StatementRank);
            Assert.True(link.IsCurrent);
            Assert.Equal(first.ImportId, link.ImportId);
            var item = await db.WikidataItems.SingleAsync(x => x.Qid == "Q1");
            Assert.Equal(item.Id, link.ItemId);
            var import = await db.SourceImports.SingleAsync();
            Assert.Equal(("Wikidata", "Crosswalk", "Succeeded", 5L, 4L), (import.Source, import.Kind, import.Status, import.RowsRead, import.RowsInserted));
            Assert.False(System.Text.Json.JsonDocument.Parse(import.ParametersJson!).RootElement.GetProperty("allowShrink").GetBoolean());
            Assert.Contains("\"Partitions\"", import.ValidationJson);
        }

        var before = await RowVersionsAsync(database);
        var second = await CrosswalkAsync(database, fake);
        Assert.Equal((0, 0, 0, 0, 0, 0), (second.Report.LinksInserted, second.Report.LinksUpdated, second.Report.LinksRetired,
            second.Report.ItemsInserted, second.Report.ItemsReactivated, second.Report.ItemsRetired));
        Assert.Equal(before, await RowVersionsAsync(database));
    }

    [PostgresFact]
    public async Task Upstream_removal_retires_links_and_items_without_deleting_and_they_can_return()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = Sample();
        await CrosswalkAsync(database, fake);
        fake.Statements.RemoveAll(x => x.Qid == "Q3");
        fake.Statements.RemoveAll(x => x.WfoId == "wfo-0000000003");
        fake.Statements.Add(("Q2", "wfo-0000000002", FakeWikidata.Preferred));
        var removal = await CrosswalkAsync(database, fake);
        Assert.Equal((2, 1, 1), (removal.Report.LinksRetired, removal.Report.LinksUpdated, removal.Report.ItemsRetired));
        await using (var db = database.Context())
        {
            Assert.Equal(4, await db.WikidataWfoLinks.CountAsync());
            Assert.Equal(3, await db.WikidataItems.CountAsync());
            Assert.False((await db.WikidataItems.SingleAsync(x => x.Qid == "Q3")).IsCurrent);
            var retired = await db.WikidataWfoLinks.SingleAsync(x => x.Qid == "Q2" && x.WfoId == "wfo-0000000003");
            Assert.False(retired.IsCurrent);
            Assert.Equal(removal.ImportId, retired.ImportId);
            Assert.Equal("preferred", (await db.WikidataWfoLinks.SingleAsync(x => x.Qid == "Q2" && x.WfoId == "wfo-0000000002")).StatementRank);
        }

        var restored = await CrosswalkAsync(database, Sample());
        Assert.Equal((0, 3, 1), (restored.Report.LinksInserted, restored.Report.LinksUpdated, restored.Report.ItemsReactivated));
        await using (var db = database.Context())
            Assert.True(await db.WikidataWfoLinks.AllAsync(x => x.IsCurrent));
    }

    [PostgresFact]
    public async Task Shrink_guard_fails_publication_and_allow_shrink_overrides_it()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await CrosswalkAsync(database, Sample());
        var before = await RowVersionsAsync(database);
        var shrunk = new FakeWikidata();
        shrunk.Add("Q1", "wfo-0000000001");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CrosswalkAsync(database, shrunk));
        Assert.Contains("fewer than 50%", error.Message);
        Assert.Contains("--allow-shrink", error.Message);
        Assert.Equal(before, await RowVersionsAsync(database));
        await using (var db = database.Context())
        {
            var failed = await db.SourceImports.OrderByDescending(x => x.Id).FirstAsync();
            Assert.Equal("Failed", failed.Status);
            Assert.NotNull(failed.CompletedAt);
            Assert.Contains("fewer than 50%", failed.ErrorMessage);
            Assert.Equal(0, failed.RowsInserted);
        }

        var empty = await Assert.ThrowsAsync<InvalidDataException>(() => CrosswalkAsync(database, new FakeWikidata(), allowShrink: true));
        Assert.Contains("no links", empty.Message);

        var allowed = await CrosswalkAsync(database, shrunk, allowShrink: true);
        Assert.Equal(3, allowed.Report.LinksRetired);
        await using (var db = database.Context())
            Assert.Equal(1, await db.WikidataWfoLinks.CountAsync(x => x.IsCurrent));
    }

    [PostgresFact]
    public async Task Upstream_failure_records_failure_and_leaves_published_data_intact()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await CrosswalkAsync(database, Sample());
        var before = await RowVersionsAsync(database);
        var broken = Sample();
        broken.FailingPartitions.UnionWith(["1", "01"]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CrosswalkAsync(database, broken));
        Assert.Contains("01", error.Message);
        Assert.Equal(before, await RowVersionsAsync(database));

        var changedProperty = Sample();
        changedProperty.Datatypes["P7715"] = "string";
        await Assert.ThrowsAsync<InvalidDataException>(() => CrosswalkAsync(database, changedProperty));
        Assert.Empty(changedProperty.SparqlPartitions);
        await using var db = database.Context();
        Assert.Equal(["Succeeded", "Failed", "Failed"], await db.SourceImports.OrderBy(x => x.Id).Select(x => x.Status).ToListAsync());
        Assert.DoesNotContain(await db.SourceImports.Select(x => x.ErrorMessage).ToListAsync(), x => x != null && x.Contains("wikidata.org"));
    }

    [PostgresFact]
    public async Task Interrupted_runs_are_marked_abandoned_and_concurrent_runs_are_refused()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await using (var db = database.Context())
        {
            db.SourceImports.Add(new() { Source = "Wikidata", Kind = "Details", Status = "Running", StartedAt = DateTimeOffset.UtcNow });
            db.SourceImports.Add(new() { Source = "Other", Kind = "Crosswalk", Status = "Running", StartedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var holder = await SourceImportSession.OpenAsync(database.ConnectionString, "Wikidata", default))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CrosswalkAsync(database, Sample()));
            Assert.Contains("Another Wikidata import is running", error.Message);
        }
        await CrosswalkAsync(database, Sample());
        await using (var db = database.Context())
            Assert.Equal(["Abandoned", "Running", "Succeeded"], await db.SourceImports.OrderBy(x => x.Id).Select(x => x.Status).ToListAsync());
    }

    [PostgresFact]
    public async Task Resolution_follows_deduplication_to_current_and_accepted_taxa_and_refreshes_after_backbone_import()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var backbone = await ImportBackboneAsync(database, "2026-06",
            Taxon("wfo-0000000001", "Acer palmatum"),
            Taxon("wfo-0000000002", "Acer polymorphum", "Synonym", "wfo-0000000001"),
            Taxon("wfo-0000000003", "Acer dubium", "Unchecked"),
            Taxon("wfo-0000000004", "Acer rubrum"),
            Taxon("wfo-0000000005", "Acer", rank: "genus"));
        await using (var db = database.Context())
        {
            foreach (var (from, to) in new[]
            {
                ("wfo-0000000010", "wfo-0000000011"), ("wfo-0000000011", "wfo-0000000002"),
                ("wfo-0000000020", "wfo-0000000021"), ("wfo-0000000021", "wfo-0000000020")
            })
                db.WfoDeduplicatedIds.Add(new() { DeprecatedWfoId = from, ReplacementWfoId = to, ImportId = backbone });
            await db.SaveChangesAsync();
        }
        var fake = new FakeWikidata();
        foreach (var (qid, wfo) in new[]
        {
            ("Q1", "wfo-0000000001"), ("Q2", "wfo-0000000002"), ("Q3", "wfo-0000000003"), ("Q4", "wfo-0000000010"),
            ("Q5", "wfo-0000000099"), ("Q6", "wfo-0000000020"), ("Q7", "WFO-0000000001"), ("Q8", "wfo-1x"),
            ("Q9", "wfo-0000000001\n")
        }) fake.Add(qid, wfo);

        var crosswalk = await CrosswalkAsync(database, fake);
        var resolution = crosswalk.Report.Resolution!;
        Assert.Equal(9, resolution.CurrentLinks);
        Assert.Equal(new Dictionary<string, long> { ["Cycle"] = 1, ["NotFound"] = 4, ["Resolved"] = 4 }, resolution.ResolutionStatus);
        Assert.Equal(4, resolution.LinksResolvedToCurrentTaxon);
        Assert.Equal(3, resolution.LinksWithAcceptedTaxon);
        Assert.Equal(1, resolution.LinksRedirectedThroughDeduplication); // The cycle ends where it started.
        Assert.Equal(3, resolution.MalformedWfoIds);
        Assert.Equal(3, crosswalk.Report.WarningCount);
        Assert.Equal(new CoverageSummary(2, 1, 50), resolution.Coverage);

        var taxa = await TaxonIdsAsync(database);
        var links = await LinksAsync(database);
        Assert.Equal(("wfo-0000000001", "Resolved", taxa["wfo-0000000001"], taxa["wfo-0000000001"]), links["Q1"]);
        Assert.Equal(("wfo-0000000002", "Resolved", taxa["wfo-0000000002"], taxa["wfo-0000000001"]), links["Q2"]);
        Assert.Equal(("wfo-0000000003", "Resolved", taxa["wfo-0000000003"], (long?)null), links["Q3"]);
        Assert.Equal(("wfo-0000000002", "Resolved", taxa["wfo-0000000002"], taxa["wfo-0000000001"]), links["Q4"]);
        Assert.Equal(("wfo-0000000099", "NotFound", (long?)null, (long?)null), links["Q5"]);
        Assert.Equal("Cycle", links["Q6"].Status);
        Assert.Null(links["Q6"].Taxon);
        foreach (var malformed in new[] { "Q7", "Q8", "Q9" })
            Assert.Equal(((string?)null, "NotFound", (long?)null, (long?)null), links[malformed]);

        // Nothing changed upstream: resolving again rewrites nothing.
        Assert.Equal(0, (await WikidataLinkResolver.RunAsync(database.ConnectionString, TextWriter.Null, default)).LinksChanged);

        // Backbone refresh: palmatum becomes a synonym of rubrum and dubium disappears.
        await ImportBackboneAsync(database, "2026-12",
            Taxon("wfo-0000000001", "Acer palmatum", "Synonym", "wfo-0000000004"),
            Taxon("wfo-0000000002", "Acer polymorphum", "Synonym", "wfo-0000000004"),
            Taxon("wfo-0000000004", "Acer rubrum"),
            Taxon("wfo-0000000005", "Acer", rank: "genus"));
        var refreshed = await WikidataLinkResolver.RunAsync(database.ConnectionString, TextWriter.Null, default);
        Assert.Equal(4, refreshed.LinksChanged);
        links = await LinksAsync(database);
        Assert.Equal(("wfo-0000000001", "Resolved", taxa["wfo-0000000001"], taxa["wfo-0000000004"]), links["Q1"]);
        Assert.Equal(taxa["wfo-0000000004"], links["Q2"].Accepted);
        Assert.Equal(taxa["wfo-0000000004"], links["Q4"].Accepted);
        Assert.Equal(("wfo-0000000003", "NotFound", (long?)null, (long?)null), links["Q3"]);
        Assert.Equal(new CoverageSummary(1, 1, 100), refreshed.Coverage);
    }

    [PostgresFact]
    public async Task Details_store_item_values_and_an_unchanged_rerun_only_checks_revisions()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = SampleWithEntities();
        await CrosswalkAsync(database, fake);
        var first = await DetailsAsync(database, fake);
        Assert.Equal((3, 3, 3, 0), (first.Report.ItemsChecked, first.Report.ItemsFetched, first.Report.ItemsUpdated, first.Report.ItemsUnchanged));
        Assert.Equal(4, first.Report.CommonNamesInserted);
        Assert.Equal(4, first.Report.ExternalIdsInserted);
        Assert.Equal(0, first.Report.P7715Mismatches);
        Assert.Equal((3, 3, 2, 1, 1), (first.Report.CurrentItems, first.Report.ItemsWithDetails, first.Report.ItemsWithEnglishCommonName,
            first.Report.ItemsWithEnwikiTitle, first.Report.ItemsWithImage));
        Assert.Equal([new ValueCount("en", 2), new ValueCount("en-gb", 1), new ValueCount("fr", 1)], first.Report.CommonNamesByLanguage);
        Assert.Equal(new Dictionary<string, long> { ["P846"] = 2, ["P961"] = 1, ["P1772"] = 1 }, first.Report.ExternalIdCounts);
        Assert.All(fake.EntityRequests, x => Assert.False(x.InfoOnly));

        await using (var db = database.Context())
        {
            var maple = await db.WikidataItems.SingleAsync(x => x.Qid == "Q1");
            Assert.Equal(("Acer palmatum", "Q7432", "Acer palmatum", "Acer palmatum", "Best.jpg", 100L),
                (maple.TaxonName, maple.TaxonRankQid, maple.LabelEn, maple.EnwikiTitle, maple.ImageFile, maple.LastRevId));
            Assert.Equal(first.ImportId, maple.DetailsImportId);
            Assert.NotNull(maple.DetailsFetchedAt);
            var names = await db.WikidataCommonNames.Where(x => x.ItemId == maple.Id).OrderBy(x => x.Language).ThenBy(x => x.Name)
                .Select(x => new { x.Language, x.Name, x.NormalizedName }).ToListAsync();
            Assert.Equal([new { Language = "en", Name = "  Japanese  Maple ", NormalizedName = "japanese maple" },
                new { Language = "en-gb", Name = "Japanese maple", NormalizedName = "japanese maple" },
                new { Language = "fr", Name = "Érable du Japon", NormalizedName = "erable du japon" }], names);
            var import = await db.SourceImports.SingleAsync(x => x.Kind == "Details");
            Assert.Equal(("Succeeded", 3L, 3L, 8L), (import.Status, import.RowsRead, import.RowsUpdated, import.RowsInserted));
        }

        var before = await RowVersionsAsync(database, includeDetails: true);
        fake.EntityRequests.Clear();
        var second = await DetailsAsync(database, fake);
        Assert.Equal((3, 0, 0, 3), (second.Report.ItemsChecked, second.Report.ItemsFetched, second.Report.ItemsUpdated, second.Report.ItemsUnchanged));
        Assert.All(fake.EntityRequests, x => Assert.True(x.InfoOnly));
        Assert.Equal(before, await RowVersionsAsync(database, includeDetails: true));

        fake.EntityRequests.Clear();
        var full = await DetailsAsync(database, fake, full: true);
        Assert.Equal((3, 0, 3), (full.Report.ItemsFetched, full.Report.ItemsUpdated, full.Report.ItemsUnchanged));
        Assert.All(fake.EntityRequests, x => Assert.False(x.InfoOnly));
        Assert.Equal(before, await RowVersionsAsync(database, includeDetails: true));

        var limited = await DetailsAsync(database, fake, full: true, limit: 2);
        Assert.Equal(2, limited.Report.ItemsChecked);
    }

    [PostgresFact]
    public async Task Details_replace_common_names_and_external_ids_wholesale_per_changed_item()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = SampleWithEntities();
        await CrosswalkAsync(database, fake);
        await DetailsAsync(database, fake);
        var untouched = (await RowVersionsAsync(database, includeDetails: true)).Where(x => x.StartsWith("name:Q2") || x.StartsWith("id:Q2")).ToList();

        var maple = fake.Entities["Q1"];
        maple.Revision = 101;
        maple.Claims.RemoveAll(x => x.Property is "P1843" or "P846");
        maple.Claims.Add(("P1843", FakeWikidata.Text("Japanese maple", "en")));
        maple.Claims.Add(("P1843", FakeWikidata.Text("Momiji", "ja")));
        maple.Claims.Add(("P846", FakeWikidata.Value("3189846")));
        var changed = await DetailsAsync(database, fake);
        Assert.Equal((1, 2, 3), (changed.Report.ItemsUpdated, changed.Report.ItemsUnchanged, changed.Report.CommonNamesDeleted));
        Assert.Equal((2, 1, 0), (changed.Report.CommonNamesInserted, changed.Report.ExternalIdsDeleted, changed.Report.ExternalIdsInserted));

        await using var db = database.Context();
        var id = await db.WikidataItems.Where(x => x.Qid == "Q1").Select(x => x.Id).SingleAsync();
        Assert.Equal(["en:Japanese maple", "ja:Momiji"], await db.WikidataCommonNames.Where(x => x.ItemId == id)
            .OrderBy(x => x.Language).Select(x => x.Language + ":" + x.Name).ToListAsync());
        Assert.Equal(["P846:3189846", "P961:786332-1"], await db.WikidataExternalIds.Where(x => x.ItemId == id)
            .OrderBy(x => x.Property).Select(x => x.Property + ":" + x.Value).ToListAsync());
        Assert.Equal(untouched, (await RowVersionsAsync(database, includeDetails: true)).Where(x => x.StartsWith("name:Q2") || x.StartsWith("id:Q2")));
    }

    [PostgresFact]
    public async Task Details_interrupted_mid_run_keeps_committed_batches_and_resumes()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = new FakeWikidata();
        for (var i = 1; i <= 5; i++)
        {
            fake.Add($"Q{i}", $"wfo-000000000{i}");
            fake.AddEntity($"Q{i}", 10 * i, $"Taxon {i}", null, ("P1843", FakeWikidata.Text($"name {i}", "en")),
                ("P7715", FakeWikidata.Value($"wfo-000000000{i}")));
        }
        await CrosswalkAsync(database, fake);
        fake.FullRequestOverride = count => count == 3 ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) : null;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DetailsAsync(database, fake, commitSize: 2));
        Assert.Contains("HTTP 400", error.Message);
        await using (var db = database.Context())
        {
            Assert.Equal(4, await db.WikidataItems.CountAsync(x => x.DetailsFetchedAt != null));
            Assert.Equal(4, await db.WikidataCommonNames.CountAsync());
            var failed = await db.SourceImports.SingleAsync(x => x.Kind == "Details");
            Assert.Equal(("Failed", 4L), (failed.Status, failed.RowsRead));
            Assert.DoesNotContain("wikidata.org", failed.ErrorMessage);
        }

        fake.FullRequestOverride = null;
        fake.EntityRequests.Clear();
        var resumed = await DetailsAsync(database, fake, commitSize: 2);
        Assert.Equal((5, 1, 1, 4), (resumed.Report.ItemsChecked, resumed.Report.ItemsFetched, resumed.Report.ItemsUpdated, resumed.Report.ItemsUnchanged));
        Assert.Single(fake.EntityRequests, x => !x.InfoOnly);
        await using (var db = database.Context())
        {
            Assert.Equal(5, await db.WikidataCommonNames.CountAsync());
            Assert.Equal(["Failed", "Succeeded"], await db.SourceImports.Where(x => x.Kind == "Details").OrderBy(x => x.Id)
                .Select(x => x.Status).ToListAsync());
        }
    }

    [PostgresFact]
    public async Task Details_store_redirects_under_the_target_and_report_missing_items_and_p7715_mismatches()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var fake = SampleWithEntities();
        await CrosswalkAsync(database, fake);
        fake.Entities.Remove("Q3");
        fake.Entities["Q20"] = fake.Entities["Q2"];
        fake.Entities.Remove("Q2");
        fake.Redirects["Q2"] = "Q20";
        fake.Entities["Q1"].Claims.Add(("P7715", FakeWikidata.Value("wfo-0000000009")));

        var result = await DetailsAsync(database, fake);
        Assert.Equal((1, 1, 1, 1), (result.Report.MissingItems, result.Report.RedirectedItems, result.Report.ItemsInserted, result.Report.ItemsRetired));
        Assert.Equal(2, result.Report.P7715Mismatches);
        Assert.Equal(4, result.Report.WarningCount);
        Assert.Contains(result.Report.Warnings, x => x.Contains("Q3") && x.Contains("missing"));
        Assert.Contains(result.Report.Warnings, x => x.Contains("Q2 redirects to Q20"));

        await using var db = database.Context();
        var old = await db.WikidataItems.SingleAsync(x => x.Qid == "Q2");
        Assert.False(old.IsCurrent);
        Assert.Null(old.DetailsFetchedAt);
        var target = await db.WikidataItems.SingleAsync(x => x.Qid == "Q20");
        Assert.True(target.IsCurrent);
        Assert.Equal(200, target.LastRevId);
        Assert.Equal(["red maple"], await db.WikidataCommonNames.Where(x => x.ItemId == target.Id).Select(x => x.Name).ToListAsync());
        Assert.True(await db.WikidataWfoLinks.Where(x => x.Qid == "Q2").AllAsync(x => x.IsCurrent));
        Assert.Null((await db.WikidataItems.SingleAsync(x => x.Qid == "Q3")).DetailsFetchedAt);
    }

    internal static FakeWikidata SampleWithEntities()
    {
        var fake = Sample();
        fake.AddEntity("Q1", 100, "Acer palmatum", "Acer palmatum",
            ("P225", FakeWikidata.Value("Acer palmatum")),
            ("P105", FakeWikidata.Item("Q7432")),
            ("P1843", FakeWikidata.Text("  Japanese  Maple ", "en")),
            ("P1843", FakeWikidata.Text("Japanese maple", "en-gb")),
            ("P1843", FakeWikidata.Text("Érable du Japon", "fr")),
            ("P1843", FakeWikidata.Text("Wrong", "en", "deprecated")),
            ("P18", FakeWikidata.Value("Second.jpg")),
            ("P18", FakeWikidata.Value("Best.jpg", "preferred")),
            ("P846", FakeWikidata.Value("3189846")),
            ("P846", FakeWikidata.Value("8351233")),
            ("P961", FakeWikidata.Value("786332-1")),
            ("P7715", FakeWikidata.Value("wfo-0000000001", "preferred")));
        fake.AddEntity("Q2", 200, "Acer rubrum", null,
            ("P1843", FakeWikidata.Text("red maple", "en")),
            ("P1772", FakeWikidata.Value("ACRU")),
            ("P7715", FakeWikidata.Value("wfo-0000000002")),
            ("P7715", FakeWikidata.Value("wfo-0000000003")),
            ("P18", FakeWikidata.NoValue()));
        fake.AddEntity("Q3", 300, null, null, ("P7715", FakeWikidata.Value("wfo-0000000001")));
        return fake;
    }

    internal static async Task<SourceImportResult<DetailsReport>> DetailsAsync(WfoPostgresTests.TestDatabase database, FakeWikidata fake,
        bool full = false, int? limit = null, int commitSize = 500)
    {
        using var clients = fake.Clients();
        return await new WikidataDetailsImporter(database.ConnectionString, clients.Api, TextWriter.Null) { CommitSize = commitSize }
            .ImportAsync(full, limit);
    }

    private static string[] Taxon(string id, string name, string status = "Accepted", string? accepted = null, string rank = "species")
    {
        var row = WfoParserTests.Row(id, name);
        row[4] = rank;
        row[18] = status;
        if (accepted != null) row[19] = accepted;
        return row;
    }

    private static async Task<long> ImportBackboneAsync(WfoPostgresTests.TestDatabase database, string version, params string[][] rows) =>
        (await database.ImportAsync(WfoParserTests.Tsv(rows), version)).ImportId;

    private static async Task<Dictionary<string, long>> TaxonIdsAsync(WfoPostgresTests.TestDatabase database)
    {
        await using var db = database.Context();
        return await db.WfoTaxa.ToDictionaryAsync(x => x.TaxonId, x => x.Id);
    }

    private static async Task<Dictionary<string, (string? Resolved, string Status, long? Taxon, long? Accepted)>> LinksAsync(
        WfoPostgresTests.TestDatabase database)
    {
        await using var db = database.Context();
        return (await db.WikidataWfoLinks.AsNoTracking().ToListAsync())
            .ToDictionary(x => x.Qid, x => (x.ResolvedWfoId, x.ResolutionStatus, x.WfoTaxonId, x.AcceptedWfoTaxonId));
    }

    internal static FakeWikidata Sample()
    {
        var fake = new FakeWikidata();
        fake.Add("Q1", "wfo-0000000001");
        fake.Add("Q1", "wfo-0000000001", FakeWikidata.Preferred);
        fake.Add("Q2", "wfo-0000000002");
        fake.Add("Q2", "wfo-0000000003");
        fake.Add("Q3", "wfo-0000000001");
        fake.Add("Q4", "wfo-0000000004", FakeWikidata.Deprecated);
        return fake;
    }

    internal static async Task<SourceImportResult<CrosswalkReport>> CrosswalkAsync(WfoPostgresTests.TestDatabase database,
        FakeWikidata fake, bool allowShrink = false)
    {
        using var clients = fake.Clients();
        return await new WikidataCrosswalkImporter(database.ConnectionString, clients.Sparql, clients.Api, TextWriter.Null)
            .ImportAsync(allowShrink);
    }

    // xmin changes whenever PostgreSQL rewrites a row, even if the values are identical.
    private static async Task<List<string>> RowVersionsAsync(WfoPostgresTests.TestDatabase database, bool includeDetails = false)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT 'item:' || "Qid" || ':' || xmin::text FROM reference.wikidata_item
            UNION ALL SELECT 'link:' || "Qid" || ':' || "WfoId" || ':' || xmin::text FROM reference.wikidata_wfo_link
            {(includeDetails ? """
            UNION ALL SELECT 'name:' || i."Qid" || ':' || n."Language" || ':' || n."Name" || ':' || n.xmin::text
                FROM reference.wikidata_common_name n JOIN reference.wikidata_item i ON i."Id" = n."ItemId"
            UNION ALL SELECT 'id:' || i."Qid" || ':' || e."Property" || ':' || e."Value" || ':' || e.xmin::text
                FROM reference.wikidata_external_id e JOIN reference.wikidata_item i ON i."Id" = e."ItemId"
            """ : "")}
            ORDER BY 1
            """, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static readonly IEqualityComparer<MultiValueExample> Comparer = EqualityComparer<MultiValueExample>.Create(
        (a, b) => a!.Key == b!.Key && a.Values.SequenceEqual(b.Values), x => x.Key.GetHashCode());

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
