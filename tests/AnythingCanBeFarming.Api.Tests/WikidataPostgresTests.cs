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
    private static async Task<List<string>> RowVersionsAsync(WfoPostgresTests.TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT 'item:' || "Qid" || ':' || xmin::text FROM reference.wikidata_item
            UNION ALL SELECT 'link:' || "Qid" || ':' || "WfoId" || ':' || xmin::text FROM reference.wikidata_wfo_link
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
