using System.Text.Json;
using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.DataImport;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static AnythingCanBeFarming.Api.Tests.WikidataPostgresTests;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class UsdaPostgresTests
{
    [PostgresFact]
    public async Task Fixture_import_publishes_then_a_repeat_is_skipped_and_force_reproduces_the_counts()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var diagnostics = new StringWriter();
        var first = await ImportAsync(database, UsdaParserTests.FixtureDirectory, diagnostics: diagnostics);
        var report = first.Report;
        Assert.False(first.AlreadyImported);
        Assert.Equal((0L, 6L, 300L, 30L, 0L), (report.RowsRejected, report.TaxaInserted, report.Facts, report.DistributionRows, report.DuplicateDistributionRowsCollapsed));
        Assert.Equal((1L, 3L, 3L), (report.SymbolsMissingFromTaxonFile, report.OccurrencesSkipped, report.FactsSkipped));
        Assert.Equal(new ValueCount("ABGU", 3), Assert.Single(report.MissingSymbolExamples));
        Assert.Equal(new Dictionary<string, long> { ["genus"] = 1, ["species"] = 4, ["variety"] = 1 }, report.TaxaByRank);
        Assert.Equal(4, report.TaxaWithCharacteristics);
        Assert.Equal((6L, 5L), (report.TaxaPresentInOhio, report.TaxaPresentInOhioAndNativeToContiguousUs));
        Assert.Equal(new Dictionary<string, long> { ["geonames"] = 21, ["literal"] = 1, ["wikidata"] = 8 }, report.DistributionByScheme);
        Assert.Empty(report.UnlabeledPlaces);
        Assert.Empty(report.UnknownTypeUris);
        Assert.Equal(0, report.TaxaWithMoreThanTwoHeights);
        Assert.Equal(["growth_habit_flopo"], report.UnmappedCodedValues.Keys);
        Assert.Equal(4, report.UnresolvedCodedValues["shade_tolerance"].Sum(x => x.Count));
        Assert.Equal(1, report.WarningsByKind["missingTaxon"]);
        Assert.Contains("\"missingTaxon\"", diagnostics.ToString());
        Assert.Equal(report.WarningCount, diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(64, report.FileHashes.Values.First().Length);

        await using (var db = database.Context())
        {
            var import = await db.SourceImports.SingleAsync();
            Assert.Equal(("USDA", "EolTraits", "Succeeded"), (import.Source, import.Kind, import.Status));
            using var parameters = JsonDocument.Parse(import.ParametersJson!);
            Assert.Equal("18945513", parameters.RootElement.GetProperty("zenodoRecord").GetString());
            Assert.Equal("8", parameters.RootElement.GetProperty("version").GetString());
            Assert.Equal(report.SourceHash, JsonDocument.Parse(import.ValidationJson!).RootElement.GetProperty("SourceHash").GetString());
            Assert.Equal(report.RowsRead.Values.Sum(), import.RowsRead);

            var names = await db.UsdaTaxa.ToDictionaryAsync(x => x.Symbol, x => x.CanonicalName);
            Assert.Equal("Acer", names["ACER"]);
            Assert.Equal("Acer saccharum var. schneckii", names["ACSAS2"]);
            Assert.Equal("Aceraceae", (await db.UsdaTaxa.SingleAsync(x => x.Symbol == "ACSA3")).FamilyUsda);
            var remark = await db.UsdaFacts.Where(x => x.Symbol == "ACSA3" && x.RemarkId != null)
                .Join(db.UsdaRemarks, f => f.RemarkId, r => r.Id, (f, r) => r.Text).FirstAsync();
            Assert.Contains("\\n", remark);
            Assert.Equal(["Duration"], await db.UsdaFacts.Where(x => x.Symbol == "ACSA3" && x.TypeUri.EndsWith("TO_0002725"))
                .Select(x => x.SourceTerm!).ToListAsync());
            var ohio = await db.UsdaDistributions.SingleAsync(x => x.Symbol == "ACSA3" && x.PlaceId == "5165418");
            Assert.Equal(("Present", "geonames", "http://www.geonames.org/5165418"), (ohio.Kind, ohio.PlaceScheme, ohio.PlaceRaw));
            Assert.Equal("Ohio", (await db.UsdaRemarks.SingleAsync(x => x.Id == ohio.RemarkId)).Text);
            // GeoNames 614540 is the country of Georgia; the source's own remark says it means the US state.
            var georgia = await db.UsdaDistributions.SingleAsync(x => x.Symbol == "ACSA3" && x.PlaceId == "614540");
            Assert.Equal("Georgia", (await db.UsdaRemarks.SingleAsync(x => x.Id == georgia.RemarkId)).Text);
            Assert.Equal("literal", (await db.UsdaDistributions.SingleAsync(x => x.PlaceRaw == "Pacific Basin excluding Hawaii")).PlaceScheme);
        }

        var labeled = await LabeledAsync(database, "ACSA3");
        Assert.Equal(["Low (tolerance)"], labeled["fire_tolerance"]);
        Assert.Equal(["Medium (tolerance)"], labeled["drought_tolerance"]);
        Assert.Equal(["PATO_0002393 (unresolved)"], labeled["shade_tolerance"]);
        Assert.Equal(["-47"], labeled["min_temperature_f"]);
        Assert.Equal(["100", "20"], labeled["height_ft"]);
        Assert.Equal(["Mid Spring"], labeled["bloom_period"]);
        Assert.Equal(["Green"], labeled["flower_color"]);
        Assert.Equal(["Shrub", "Tree"], labeled["growth_habit"]);
        Assert.Equal(["3.7", "7.9"], labeled["soil_ph"]);

        var before = await TaxonIdsAsync(database);
        var skipped = await ImportAsync(database, UsdaParserTests.FixtureDirectory);
        Assert.True(skipped.AlreadyImported);
        Assert.Equal(first.ImportId, skipped.ImportId);
        await using (var db = database.Context())
            Assert.Equal(1, await db.SourceImports.CountAsync());

        var forced = await ImportAsync(database, UsdaParserTests.FixtureDirectory, force: true);
        Assert.False(forced.AlreadyImported);
        Assert.Equal((0L, 0L, 0L), (forced.Report.TaxaInserted, forced.Report.TaxaUpdated, forced.Report.TaxaRetired));
        Assert.Equal((report.Facts, report.DistributionRows, report.TaxaWithCharacteristics, report.TaxaPresentInOhio),
            (forced.Report.Facts, forced.Report.DistributionRows, forced.Report.TaxaWithCharacteristics, forced.Report.TaxaPresentInOhio));
        Assert.Equal(0, forced.Report.Links!.LinksChanged);
        Assert.Equal(before, await TaxonIdsAsync(database));
        Assert.Equal(labeled, await LabeledAsync(database, "ACSA3"));
    }

    [PostgresFact]
    public async Task Absent_symbols_are_retired_and_facts_and_distribution_are_replaced_wholesale()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await ImportAsync(database, UsdaParserTests.FixtureDirectory);
        using var directory = UsdaParserTests.CopyFixture();
        RemoveTaxon(directory.Path, "ACSAS2");
        // ACSA3's flower color changes from green to red in the next release.
        var facts = Path.Combine(directory.Path, "measurement_or_fact_specific.tab");
        var occurrence = OccurrencesOf(directory.Path, "ACSA3");
        File.WriteAllLines(facts, File.ReadAllLines(facts).Select(line =>
        {
            var fields = line.Split('\t');
            return occurrence.Contains(fields[1]) && fields[5].EndsWith("TO_0000537")
                ? line.Replace("PATO_0000320", "PATO_0000322") : line;
        }));

        var second = await ImportAsync(database, directory.Path);
        Assert.Equal((0L, 0L, 1L), (second.Report.TaxaInserted, second.Report.TaxaUpdated, second.Report.TaxaRetired));
        Assert.Equal((5L, 1L), (second.Report.CurrentTaxa, second.Report.RetiredTaxa));
        Assert.Equal(2, second.Report.SymbolsMissingFromTaxonFile);
        await using var db = database.Context();
        var retired = await db.UsdaTaxa.SingleAsync(x => x.Symbol == "ACSAS2");
        Assert.False(retired.IsCurrent);
        Assert.NotEqual(second.ImportId, retired.ImportId);
        Assert.False(await db.UsdaFacts.AnyAsync(x => x.Symbol == "ACSAS2"));
        Assert.False(await db.UsdaDistributions.AnyAsync(x => x.Symbol == "ACSAS2"));
        Assert.False(await db.UsdaWfoLinks.AnyAsync(x => x.Symbol == "ACSAS2"));
        Assert.True(await db.UsdaFacts.AllAsync(x => x.ImportId == second.ImportId));
        Assert.Equal(["Red"], (await LabeledAsync(database, "ACSA3"))["flower_color"]);
        Assert.Equal(second.Report.Facts, await db.UsdaFacts.CountAsync());
    }

    [PostgresFact]
    public async Task Rejected_rows_and_database_failures_leave_the_published_data_intact()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await ImportAsync(database, UsdaParserTests.FixtureDirectory);
        var before = await CountsAsync(database);

        using var duplicate = UsdaParserTests.CopyFixture();
        var taxa = Path.Combine(duplicate.Path, "taxon.tab");
        File.AppendAllLines(taxa, [File.ReadAllLines(taxa)[1]]);
        var diagnostics = new StringWriter();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(database, duplicate.Path, diagnostics: diagnostics));
        Assert.Contains("rejected rows", error.Message);
        Assert.Equal(2, diagnostics.ToString().Split('\n').Count(x => x.Contains("Duplicate symbol")));
        Assert.Equal(before, await CountsAsync(database));

        using var missing = UsdaParserTests.CopyFixture();
        var facts = Path.Combine(missing.Path, "measurement_or_fact_specific.tab");
        var lines = File.ReadAllLines(facts);
        lines[1] = string.Join('\t', lines[1].Split('\t').Select((x, i) => i == 1 ? "O-nowhere" : x));
        File.WriteAllLines(facts, lines);
        await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(database, missing.Path));

        await using (var db = database.Context())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reference.fail_distribution() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test failure'; END $$;
                CREATE TRIGGER fail_distribution BEFORE INSERT ON reference.usda_distribution
                FOR EACH ROW EXECUTE FUNCTION reference.fail_distribution();
                """);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(database, UsdaParserTests.FixtureDirectory, force: true));
        Assert.Contains("PostgreSQL error", failure.Message);
        Assert.Equal(before, await CountsAsync(database));
        await using (var db = database.Context())
            Assert.Equal(["Succeeded", "Failed", "Failed", "Failed"], await db.SourceImports.OrderBy(x => x.Id).Select(x => x.Status).ToListAsync());
    }

    [PostgresFact]
    public async Task Links_prefer_wikidata_record_conflicts_and_ambiguity_follow_synonyms_and_refresh_after_a_backbone_import()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await ImportBackboneAsync(database, "2026-06",
            Taxon("wfo-0000000001", "Acer saccharum"),
            Taxon("wfo-0000000002", "Acer saccharum", "Synonym", "wfo-0000000001"),
            Taxon("wfo-0000000003", "Cornus florida"),
            Taxon("wfo-0000000004", "Benthamidia florida"),
            Taxon("wfo-0000000005", "Rudbeckia hirta"),
            Taxon("wfo-0000000006", "Rudbeckia hirta", "Unchecked"),
            Taxon("wfo-0000000007", "Echinacea purpurea", "Synonym", "wfo-0000000008"),
            Taxon("wfo-0000000008", "Brauneria purpurea"),
            Taxon("wfo-0000000009", "Acer", rank: "genus"));
        await AddWikidataAsync(database, ("Q1", "ACSA3", "wfo-0000000001"), ("Q2", "COFL2", "wfo-0000000003"), ("Q3", "COFL2", "wfo-0000000004"),
            ("Q4", "NOTUSDA", "wfo-0000000005"));

        var import = await ImportAsync(database, UsdaParserTests.FixtureDirectory);
        var report = import.Report.Links!;
        Assert.Equal(new Dictionary<string, long> { ["Ambiguous/name"] = 1, ["Conflict/wikidata"] = 1, ["Linked/name"] = 2,
            ["Linked/wikidata"] = 1, ["NotFound/name"] = 1 }, report.ByStatusAndMethod);
        Assert.Equal((1L, 1L, 1L, 2L), (report.Conflicts, report.Ambiguous, report.LinkedToSynonym, report.SymbolsWithWikidataIds));
        Assert.Equal(new UsdaLinkExample("COFL2", "Cornus florida", ["wfo-0000000003", "wfo-0000000004"]), Assert.Single(report.ConflictExamples), LinkExampleComparer);
        Assert.Equal(["wfo-0000000005", "wfo-0000000006"], Assert.Single(report.AmbiguousExamples).Candidates);

        var taxa = await WfoIdsAsync(database);
        var links = await LinksAsync(database);
        // A name match exists for ACSA3 too (two of them), but Wikidata wins.
        Assert.Equal(("wikidata", "Linked", taxa["wfo-0000000001"], taxa["wfo-0000000001"]), Strip(links["ACSA3"]));
        Assert.Contains("\"Q1\"", links["ACSA3"].Detail);
        Assert.Equal(("wikidata", "Conflict", (long?)null, (long?)null), Strip(links["COFL2"]));
        Assert.Contains("Benthamidia florida", links["COFL2"].Detail);
        Assert.Equal(("name", "Ambiguous", (long?)null, (long?)null), Strip(links["RUHI2"]));
        Assert.Equal(("name", "Linked", taxa["wfo-0000000007"], taxa["wfo-0000000008"]), Strip(links["ECPU"]));
        Assert.Equal(("name", "Linked", taxa["wfo-0000000009"], taxa["wfo-0000000009"]), Strip(links["ACER"]));
        Assert.Equal(("name", "NotFound", (long?)null, (long?)null), Strip(links["ACSAS2"]));
        Assert.Null(links["ACSAS2"].Detail);

        // Backbone refresh: the unchecked duplicate disappears and purple coneflower becomes accepted.
        await ImportBackboneAsync(database, "2026-12",
            Taxon("wfo-0000000001", "Acer saccharum"),
            Taxon("wfo-0000000003", "Cornus florida"),
            Taxon("wfo-0000000004", "Benthamidia florida"),
            Taxon("wfo-0000000005", "Rudbeckia hirta"),
            Taxon("wfo-0000000007", "Echinacea purpurea"),
            Taxon("wfo-0000000008", "Brauneria purpurea"),
            Taxon("wfo-0000000009", "Acer", rank: "genus"));
        await WikidataLinkResolver.RunAsync(database.ConnectionString, TextWriter.Null, default);
        var relinked = await UsdaLinker.RunAsync(database.ConnectionString, TextWriter.Null, default);
        Assert.Equal(2, relinked.LinksChanged);
        links = await LinksAsync(database);
        Assert.Equal(("name", "Linked", taxa["wfo-0000000005"], taxa["wfo-0000000005"]), Strip(links["RUHI2"]));
        Assert.Equal(("name", "Linked", taxa["wfo-0000000007"], taxa["wfo-0000000007"]), Strip(links["ECPU"]));
        Assert.Equal(0, (await UsdaLinker.RunAsync(database.ConnectionString, TextWriter.Null, default)).LinksChanged);
    }

    [PostgresFact]
    public async Task Places_without_a_label_are_reported()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        using var seeds = new UsdaParserTests.TemporaryDirectory();
        foreach (var name in new[] { UsdaSeeds.TraitTypesFile, UsdaSeeds.CodeLabelsFile })
            File.Copy(Path.Combine(UsdaParserTests.SeedDirectory, name), Path.Combine(seeds.Path, name));
        File.WriteAllLines(Path.Combine(seeds.Path, UsdaSeeds.PlaceLabelsFile),
            File.ReadLines(Path.Combine(UsdaParserTests.SeedDirectory, UsdaSeeds.PlaceLabelsFile)).Where(x => !x.Contains(",Ohio,")));
        var result = await new UsdaImporter(database.ConnectionString, seeds.Path, TextWriter.Null)
            .ImportAsync(UsdaParserTests.FixtureDirectory, new UsdaImportOptions(), TextWriter.Null);
        Assert.Equal(new PlaceCount("geonames", "5165418", 6), Assert.Single(result.Report.UnlabeledPlaces));
        Assert.Equal(1, result.Report.WarningsByKind["unlabeledPlace"]);
        Assert.Equal(0, result.Report.TaxaPresentInOhio);
        Assert.Equal(74, result.Report.Seeds["place_label"].Rows);
    }

    [PostgresFact("ACBF_TEST_WFO_SNAPSHOT")]
    public async Task Full_snapshot_usda_import_matches_its_report_and_links_sugar_maple()
    {
        var connection = Environment.GetEnvironmentVariable("ACBF_TEST_WFO_SNAPSHOT")!;
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>().UseNpgsql(connection).Options);
        var import = await db.SourceImports.AsNoTracking().Where(x => x.Source == "USDA" && x.Status == "Succeeded")
            .OrderByDescending(x => x.Id).FirstAsync();
        using var report = JsonDocument.Parse(import.ValidationJson!);
        Assert.Equal(report.RootElement.GetProperty("Facts").GetInt64(), await db.UsdaFacts.LongCountAsync());
        Assert.Equal(report.RootElement.GetProperty("DistributionRows").GetInt64(), await db.UsdaDistributions.LongCountAsync());
        Assert.Equal(report.RootElement.GetProperty("CurrentTaxa").GetInt64(), await db.UsdaTaxa.LongCountAsync(x => x.IsCurrent));
        Assert.Equal(0, report.RootElement.GetProperty("TaxaWithMoreThanTwoHeights").GetInt64());
        var link = await db.UsdaWfoLinks.SingleAsync(x => x.Symbol == "ACSA3");
        var accepted = await db.WfoTaxa.SingleAsync(x => x.Id == link.AcceptedWfoTaxonId);
        Assert.Equal(("Linked", "Acer saccharum", "Accepted"), (link.Status, accepted.ScientificName, accepted.TaxonomicStatus));
    }

    internal static Task<UsdaImportResult> ImportAsync(WfoPostgresTests.TestDatabase database, string directory, bool force = false,
        TextWriter? diagnostics = null) =>
        new UsdaImporter(database.ConnectionString, UsdaParserTests.SeedDirectory, TextWriter.Null)
            .ImportAsync(directory, new UsdaImportOptions(force, "8", "18945513"), diagnostics ?? TextWriter.Null);

    // Items carrying a USDA symbol (P1772) and one WFO link each, resolved the same way a crosswalk would.
    private static async Task AddWikidataAsync(WfoPostgresTests.TestDatabase database, params (string Qid, string Symbol, string WfoId)[] items)
    {
        await using (var db = database.Context())
        {
            var import = new SourceImport { Source = "Wikidata", Kind = "Crosswalk", Status = "Succeeded", StartedAt = DateTimeOffset.UtcNow };
            db.SourceImports.Add(import);
            await db.SaveChangesAsync();
            foreach (var (qid, symbol, wfoId) in items)
            {
                var item = new WikidataItem { Qid = qid, IsCurrent = true, CrosswalkImportId = import.Id };
                db.WikidataItems.Add(item);
                await db.SaveChangesAsync();
                db.WikidataWfoLinks.Add(new() { ItemId = item.Id, Qid = qid, WfoId = wfoId, StatementRank = "normal",
                    ResolutionStatus = "NotFound", IsCurrent = true, ImportId = import.Id });
                db.WikidataExternalIds.Add(new() { ItemId = item.Id, Property = "P1772", Value = symbol });
            }
            await db.SaveChangesAsync();
        }
        await WikidataLinkResolver.RunAsync(database.ConnectionString, TextWriter.Null, default);
    }

    private static void RemoveTaxon(string directory, string symbol)
    {
        var path = Path.Combine(directory, "taxon.tab");
        File.WriteAllLines(path, File.ReadAllLines(path).Where(x => !x.StartsWith(symbol + "\t")));
    }

    private static HashSet<string> OccurrencesOf(string directory, string symbol) =>
        File.ReadLines(Path.Combine(directory, "occurrence_specific.tab")).Select(x => x.Split('\t'))
            .Where(x => x[1] == symbol).Select(x => x[0]).ToHashSet();

    internal static async Task<Dictionary<string, List<string>>> LabeledAsync(WfoPostgresTests.TestDatabase database, string symbol)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT trait_key, value_display FROM reference.usda_fact_labeled WHERE symbol = @symbol AND trait_key IS NOT NULL
            ORDER BY trait_key, value_display
            """, connection);
        command.Parameters.AddWithValue("symbol", symbol);
        var result = new Dictionary<string, List<string>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!result.TryGetValue(reader.GetString(0), out var list)) result[reader.GetString(0)] = list = [];
            list.Add(reader.GetString(1));
        }
        return result;
    }

    private static async Task<Dictionary<string, long>> TaxonIdsAsync(WfoPostgresTests.TestDatabase database)
    {
        await using var db = database.Context();
        return await db.UsdaTaxa.ToDictionaryAsync(x => x.Symbol, x => x.Id);
    }

    private static async Task<Dictionary<string, long>> WfoIdsAsync(WfoPostgresTests.TestDatabase database)
    {
        await using var db = database.Context();
        return await db.WfoTaxa.ToDictionaryAsync(x => x.TaxonId, x => x.Id);
    }

    private static async Task<Dictionary<string, (string Method, string Status, long? Taxon, long? Accepted, string? Detail)>> LinksAsync(
        WfoPostgresTests.TestDatabase database)
    {
        await using var db = database.Context();
        return (await db.UsdaWfoLinks.AsNoTracking().ToListAsync())
            .ToDictionary(x => x.Symbol, x => (x.Method, x.Status, x.WfoTaxonId, x.AcceptedWfoTaxonId, x.DetailJson));
    }

    private static (string, string, long?, long?) Strip((string Method, string Status, long? Taxon, long? Accepted, string? Detail) link) =>
        (link.Method, link.Status, link.Taxon, link.Accepted);

    private static async Task<(int Taxa, int Current, int Facts, int Distribution, int Links, int Remarks)> CountsAsync(WfoPostgresTests.TestDatabase database)
    {
        await using var db = database.Context();
        return (await db.UsdaTaxa.CountAsync(), await db.UsdaTaxa.CountAsync(x => x.IsCurrent), await db.UsdaFacts.CountAsync(),
            await db.UsdaDistributions.CountAsync(), await db.UsdaWfoLinks.CountAsync(), await db.UsdaRemarks.CountAsync());
    }

    private static readonly IEqualityComparer<UsdaLinkExample> LinkExampleComparer = EqualityComparer<UsdaLinkExample>.Create(
        (a, b) => a!.Symbol == b!.Symbol && a.CanonicalName == b.CanonicalName && a.Candidates.SequenceEqual(b.Candidates), x => x.Symbol.GetHashCode());
}
