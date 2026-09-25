using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.DataImport;
using Microsoft.EntityFrameworkCore;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WfoSupplementalTests
{
    private const string NameHeader = "wfo_id,name_canonical,authors_string,rank,nomenclatural_status\n";

    [PostgresFact("ACBF_TEST_WFO_SNAPSHOT")]
    public async Task Full_snapshot_supports_supplemental_identifier_lookups()
    {
        var connection = Environment.GetEnvironmentVariable("ACBF_TEST_WFO_SNAPSHOT")!;
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>().UseNpgsql(connection).Options);
        var oldId = await db.WfoDeduplicatedIds.AsNoTracking().Where(x => x.ReplacementTaxonId != null)
            .OrderBy(x => x.Id).Select(x => x.DeprecatedWfoId).FirstAsync();
        var resolution = await new WfoIdResolver(db).ResolveAsync(oldId);
        Assert.Equal("Resolved", resolution.Status);
        using var factory = new InfrastructureTests.ApiFactory(connectionString: connection);
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        var result = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/" + oldId);
        Assert.Equal(resolution.ResolvedWfoId, result.GetProperty("resolvedWfoId").GetString());
        Assert.True(result.GetProperty("wasRedirected").GetBoolean());

        var ipni = await db.WfoIpniMappings.AsNoTracking().Where(x => x.WfoTaxonId != null)
            .OrderBy(x => x.Id).Select(x => x.NormalizedIpniId).FirstAsync();
        var mappings = await db.WfoIpniMappings.AsNoTracking().Where(x => x.NormalizedIpniId == ipni)
            .Select(x => x.WfoId).ToListAsync();
        var targets = new HashSet<string>();
        foreach (var mapping in mappings)
        {
            var target = await new WfoIdResolver(db).ResolveAsync(mapping);
            if (target.Status == "Resolved") targets.Add(target.ResolvedWfoId);
        }
        var response = await client.GetAsync("/api/reference/plants/by-ipni/" + Uri.EscapeDataString(ipni));
        Assert.NotEmpty(targets);
        Assert.Equal(targets.Count == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        if (targets.Count == 1)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(targets.Single(), body.GetProperty("resolvedWfoId").GetString());
        }
    }

    [Fact]
    public void Known_missing_second_header_preserves_all_six_fields()
    {
        var report = new SupplementalReport();
        var rows = WfoSupplementalSource.Read(new StringReader(NameHeader +
            "wfo-4000048768,wfo-4000048766,?,,genus,deprecated\n" +
            "wfo-old,wfo-new,\"Name, with comma\",\"Nées \"\"quoted\"\"\nsecond line\",species,unexpected\n"),
            WfoSupplementalKind.Deduplicated, report).ToArray();
        Assert.Equal(new[] { "wfo-4000048768", "wfo-4000048766", "?", null, "genus", "deprecated" }, rows[0]);
        Assert.Equal("Name, with comma", rows[1][2]);
        Assert.Equal("Nées \"quoted\"\nsecond line", rows[1][3]);
        Assert.Single(report.Warnings);
    }

    [Theory]
    [InlineData("wfo-old,?,,genus,deprecated\n")]
    [InlineData("wfo-old,,?,,genus,deprecated\n")]
    [InlineData("wfo-old,not-an-id,?,,genus,deprecated\n")]
    [InlineData("wfo-old,wfo-new,bad\"quote,,genus,deprecated\n")]
    public void Dedupe_quirk_does_not_allow_arbitrary_malformed_records(string row)
    {
        var report = new SupplementalReport();
        Assert.Throws<InvalidDataException>(() => WfoSupplementalSource.Read(new StringReader(NameHeader + row),
            WfoSupplementalKind.Deduplicated, report).ToList());
        Assert.Equal(1, report.RowsRejected);
    }

    [Fact]
    public void Explicit_six_column_header_is_supported_but_unknown_order_fails()
    {
        var header = "wfo_id,replacement_wfo_id,name_canonical,authors_string,rank,nomenclatural_status\n";
        var report = new SupplementalReport();
        Assert.Single(WfoSupplementalSource.Read(new StringReader(header + "wfo-old,wfo-new,?,,genus,deprecated\n"),
            WfoSupplementalKind.Deduplicated, report));
        Assert.Empty(report.Warnings);
        Assert.Throws<InvalidDataException>(() => WfoSupplementalSource.Read(new StringReader("wfo_id,unknown\n"),
            WfoSupplementalKind.Deduplicated, report).ToArray());
    }

    [Fact]
    public void Ipni_normalization_keeps_raw_source()
    {
        var raw = " urn:lsid:ipni.org:names:310980-1 ";
        var row = Assert.Single(WfoSupplementalSource.Read(new StringReader($"ipni_id,wfo_id\n{raw},wfo-1\n"),
            WfoSupplementalKind.Ipni, new SupplementalReport()));
        Assert.Equal(raw, row[0]);
        Assert.Equal("310980-1", row[2]);
    }

    [Fact]
    public async Task Resolution_follows_explicit_edges_even_if_old_id_exists_and_bounds_cycles_and_depth()
    {
        var edges = new Dictionary<string, string> { ["a"] = "b", ["b"] = "c" };
        Task<WfoIdResolution> Resolve(string id) => WfoIdResolver.TraverseAsync(id,
            value => Task.FromResult(edges.GetValueOrDefault(value)), value => Task.FromResult(value is "a" or "c"));
        var resolved = await Resolve("a");
        Assert.Equal("c", resolved.ResolvedWfoId);
        Assert.True(resolved.WasRedirected);
        Assert.Equal("Resolved", resolved.Status);
        Assert.False((await Resolve("c")).WasRedirected);
        Assert.Equal("NotFound", (await Resolve("missing")).Status);
        edges["c"] = "a";
        Assert.Equal("Cycle", (await Resolve("a")).Status);
        edges["a"] = "a";
        Assert.Equal("Cycle", (await Resolve("a")).Status);
        var deep = await WfoIdResolver.TraverseAsync("0", value => Task.FromResult<string?>((int.Parse(value) + 1).ToString()), _ => Task.FromResult(false));
        Assert.Equal("DepthLimit", deep.Status);
        var boundary = await WfoIdResolver.TraverseAsync("0", value => Task.FromResult<string?>(value == "64" ? null : (int.Parse(value) + 1).ToString()), _ => Task.FromResult(true));
        Assert.Equal("Resolved", boundary.Status);
    }

    [Fact]
    public void Discovery_ignores_numeric_prefix_and_fails_on_ambiguity_and_uses_package_metadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "acbf-supplemental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "backbone"));
        var source = Path.Combine(directory, "999_ipni_to_wfo.csv");
        var second = Path.Combine(directory, "123_ipni_to_wfo.csv");
        var metadata = Path.Combine(directory, "backbone", "fileInfo.json");
        try
        {
            File.WriteAllText(source, "ipni_id,wfo_id\n");
            Assert.Equal(source, WfoSupplementalSource.Discover(directory, WfoSupplementalKind.Ipni));
            Assert.Null(WfoSupplementalSource.Version(source));
            File.WriteAllText(metadata, "{\"version\":\"2026-09\"}");
            Assert.Equal("2026-09", WfoSupplementalSource.Version(source));
            Assert.Equal("override", WfoSupplementalSource.Version(source, "override"));
            File.WriteAllText(second, "ipni_id,wfo_id\n");
            Assert.Throws<InvalidDataException>(() => WfoSupplementalSource.Discover(directory, WfoSupplementalKind.Ipni));
            Assert.Throws<FileNotFoundException>(() => WfoSupplementalSource.Discover(directory, WfoSupplementalKind.Deprecated));
        }
        finally
        {
            File.Delete(source); File.Delete(second); File.Delete(metadata);
            Directory.Delete(Path.Combine(directory, "backbone")); Directory.Delete(directory);
        }
    }

    [PostgresFact]
    public async Task Supplemental_imports_preserve_backbone_resolve_identifiers_and_rollback_invalid_graphs()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        var backbone = await database.ImportAsync(WfoParserTests.Tsv(WfoParserTests.Row("wfo-current", "Acer"),
            WfoParserTests.Row("wfo-other", "Other"), WfoParserTests.Row("wfo-old", "Retained old")), "test-release");
        var path = Path.Combine(Path.GetTempPath(), "acbf-supplemental-" + Guid.NewGuid().ToString("N") + ".csv");
        async Task<SupplementalResult> Import(WfoSupplementalKind kind, string text, bool force = false)
        {
            await File.WriteAllTextAsync(path, text);
            return await new WfoSupplementalImporter(database.ConnectionString, TextWriter.Null)
                .ImportAsync(path, kind, "test-release", force);
        }
        try
        {
            var ipniText = "ipni_id,wfo_id\nurn:lsid:ipni.org:names:310980-1,wfo-old\n310980-1,wfo-current\nunknown,wfo-missing\n";
            var ipni = await Import(WfoSupplementalKind.Ipni, ipniText);
            Assert.Equal(3, ipni.Report.RowsImported);
            Assert.Equal(1, ipni.Report.Statistics["WfoIdsNotFound"]);
            var deprecated = await Import(WfoSupplementalKind.Deprecated, NameHeader + "wfo-old,Old name,,genus,deprecated\nwfo-gone,Gone,,genus,deprecated\n");
            Assert.Equal(1, deprecated.Report.Statistics["DeprecatedIdsAbsent"]);
            var dedupeText = NameHeader + "wfo-old,wfo-middle,Old,,genus,deprecated\nwfo-middle,wfo-current,Middle,,genus,deprecated\n";
            var dedupe = await Import(WfoSupplementalKind.Deduplicated, dedupeText);
            Assert.Equal(0, dedupe.Report.Statistics["MappingsReachingCycles"]);
            Assert.Equal(1, dedupe.Report.Statistics["ReplacementIdsUnresolved"]);
            Assert.True((await Import(WfoSupplementalKind.Ipni, ipniText)).AlreadyImported);
            Assert.False((await Import(WfoSupplementalKind.Ipni, ipniText, true)).AlreadyImported);

            await using var db = database.Context();
            Assert.Equal(3, await db.WfoIpniMappings.CountAsync());
            Assert.Equal(3, await db.WfoTaxa.CountAsync(x => x.ImportId == backbone.ImportId && x.IsCurrent));
            Assert.Null((await db.WfoDeprecatedNames.AsNoTracking().SingleAsync(x => x.WfoId == "wfo-gone")).WfoTaxonId);
            Assert.Equal("wfo-current", (await new WfoIdResolver(db).ResolveAsync("wfo-old")).ResolvedWfoId);
            Assert.Equal("NotFound", (await new WfoIdResolver(db).ResolveAsync("wfo-gone")).Status);

            using var factory = new InfrastructureTests.ApiFactory(connectionString: database.ConnectionString);
            using var client = factory.CreateHttpsClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
            foreach (var url in new[] { "wfo-old", "by-ipni/310980-1", "by-ipni/urn:lsid:ipni.org:names:310980-1" })
            {
                var result = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/" + url);
                Assert.Equal("wfo-current", result.GetProperty("resolvedWfoId").GetString());
                Assert.Equal("Acer", result.GetProperty("taxon").GetProperty("scientificName").GetString());
                Assert.False(result.GetProperty("taxon").TryGetProperty("importId", out _));
            }
            var direct = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/wfo-current");
            Assert.False(direct.GetProperty("wasRedirected").GetBoolean());
            var redirected = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/wfo-old");
            Assert.True(redirected.GetProperty("wasRedirected").GetBoolean());
            var stats = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/stats");
            Assert.Equal(backbone.ImportId, stats.GetProperty("lastImport").GetProperty("id").GetInt64());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/reference/plants/by-ipni/unknown")).StatusCode);

            foreach (var invalid in new[] { "wfo-current,wfo-old", "wfo-self,wfo-self" })
                await Assert.ThrowsAsync<InvalidDataException>(() => Import(WfoSupplementalKind.Deduplicated,
                    NameHeader + invalid + ",Invalid,,genus,deprecated\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => Import(WfoSupplementalKind.Deduplicated,
                NameHeader + "wfo-invalid,Only five,,genus,deprecated\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => Import(WfoSupplementalKind.Ipni,
                "ipni_id,wfo_id\nx,wfo-current\nx,wfo-current\n"));
            Assert.Equal(2, await db.WfoDeduplicatedIds.CountAsync());
            Assert.Equal(4, await db.WfoImports.CountAsync(x => x.Status == "Failed"));
            Assert.Equal("wfo-current", (await new WfoIdResolver(db).ResolveAsync("wfo-old")).ResolvedWfoId);

            var deepChain = NameHeader + string.Concat(Enumerable.Range(0, 65)
                .Select(i => $"wfo-depth-{i},wfo-depth-{i + 1},Depth,,genus,deprecated\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => Import(WfoSupplementalKind.Deduplicated, deepChain));
            Assert.Equal(2, await db.WfoDeduplicatedIds.CountAsync());
            // Also bound lookups if invalid mappings are introduced outside the importer.
            var invalidMapping = new WfoDeduplicatedId
            {
                DeprecatedWfoId = "wfo-cycle", ReplacementWfoId = "wfo-cycle", ImportId = dedupe.ImportId
            };
            db.WfoDeduplicatedIds.Add(invalidMapping);
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/reference/plants/wfo-cycle")).StatusCode);
            db.WfoDeduplicatedIds.Remove(invalidMapping);
            await db.SaveChangesAsync();

            await Import(WfoSupplementalKind.Ipni, "ipni_id,wfo_id\n310980-1,wfo-other\n");
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/reference/plants/by-ipni/310980-1")).StatusCode);
            // A backbone refresh updates optional supplemental links without discarding raw identifiers.
            await database.ImportAsync(WfoParserTests.Tsv(WfoParserTests.Row("wfo-other", "Other")), "next");
            Assert.All(await db.WfoDeduplicatedIds.AsNoTracking().ToListAsync(), x => Assert.Null(x.ReplacementTaxonId));
            Assert.Equal("NotFound", (await new WfoIdResolver(db).ResolveAsync("wfo-old")).Status);
        }
        finally { File.Delete(path); }
    }
}
