using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.DataImport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute(string environmentVariable = "ACBF_TEST_POSTGRES")
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
            Skip = $"Set {environmentVariable} to enable this PostgreSQL integration test.";
    }
}

public sealed class WfoPostgresTests
{
    [PostgresFact("ACBF_TEST_WFO_SNAPSHOT")]
    public async Task Imported_snapshot_API_counts_match_its_import_report()
    {
        var connection = Environment.GetEnvironmentVariable("ACBF_TEST_WFO_SNAPSHOT")!;
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>().UseNpgsql(connection).Options);
        var import = await db.WfoImports.AsNoTracking().Where(x => x.DatasetKind == "Backbone" && x.Status == "Succeeded")
            .OrderByDescending(x => x.Id).FirstAsync();
        var report = JsonSerializer.Deserialize<ImportReport>(import.ValidationJson!)!;
        using var factory = new InfrastructureTests.ApiFactory(connectionString: connection);
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        var response = await client.GetAsync("/api/reference/plants/stats");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stats = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(import.RowsImported, stats.GetProperty("currentRecords").GetInt64());
        Assert.Equal(report.UniqueFamilies, stats.GetProperty("uniqueFamilies").GetInt64());
        Assert.Equal(report.UniqueGenera, stats.GetProperty("uniqueGenera").GetInt64());
        // Read the stored JSON directly because ImportReport exposes read-only dictionary properties.
        using var json = JsonDocument.Parse(import.ValidationJson!);
        var relations = json.RootElement.GetProperty("Relationships");
        foreach (var (field, prefix) in new[]
        {
            ("ParentNameUsageId", "Parent"), ("AcceptedNameUsageId", "AcceptedName"), ("OriginalNameUsageId", "OriginalName")
        })
        {
            Assert.Equal(relations.GetProperty(field).GetProperty("Resolved").GetInt64(),
                stats.GetProperty($"resolved{prefix}Relationships").GetInt64());
            Assert.Equal(relations.GetProperty(field).GetProperty("Unresolved").GetInt64(),
                stats.GetProperty($"unresolved{prefix}Relationships").GetInt64());
        }
        var results = (await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=acer"))!;
        Assert.NotEmpty(results);
        Assert.InRange(results.Length, 1, 20);
        var taxonId = results[0].GetProperty("taxonId").GetString();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reference/plants/{taxonId}");
        Assert.Equal(taxonId, detail.GetProperty("taxon").GetProperty("taxonId").GetString());
        Assert.True(detail.GetProperty("taxon").GetProperty("isCurrent").GetBoolean());
    }

    [Theory]
    [InlineData("/api/reference/plants/search?q=acer")]
    [InlineData("/api/reference/plants/wfo-1")]
    [InlineData("/api/reference/plants/stats")]
    [InlineData("/api/reference/plants/by-ipni/310980-1")]
    public async Task Every_reference_endpoint_requires_authentication(string path)
    {
        using var factory = new InfrastructureTests.ApiFactory();
        using var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [PostgresFact]
    public async Task Snapshots_bulk_load_atomically_preserve_identity_and_support_authenticated_queries()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var factory = new InfrastructureTests.ApiFactory(connectionString: database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        var emptyResponse = await client.GetAsync("/api/reference/plants/stats");
        Assert.True(emptyResponse.IsSuccessStatusCode, await emptyResponse.Content.ReadAsStringAsync());
        var empty = await emptyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, empty.GetProperty("totalRecords").GetInt64());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastImport").ValueKind);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=acer"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/reference/plants/wfo-absent")).StatusCode);

        var genus = WfoParserTests.Row("wfo-genus", "Acer");
        genus[4] = "genus";
        var species = Enumerable.Range(0, 25).Select(i =>
        {
            var row = WfoParserTests.Row($"wfo-species-{i}", $"Acer species {i:D2}");
            row[1] = $"urn:lsid:ipni.org:names:{i}";
            row[5] = "wfo-genus"; row[7] = "Sapindaceae"; row[11] = "Acer"; row[13] = $"epithet{i}";
            row[22] = "Untrusted <script>alert(\"hi\")</script>\t" + new string('x', 10000);
            row[23] = "2022-04-16"; row[24] = "bad-date";
            return row;
        }).ToArray();
        var synonym = WfoParserTests.Row("wfo-synonym", "Old maple name");
        synonym[18] = "Synonym"; synonym[19] = species[0][0]; synonym[20] = species[1][0];
        var unresolved = WfoParserTests.Row("wfo-unresolved", "Unresolved maple");
        unresolved[5] = "wfo-missing"; unresolved[19] = "wfo-missing"; unresolved[20] = "wfo-missing";
        unresolved[18] = "Unchecked";
        var all = new[] { genus, synonym, unresolved }.Concat(species).ToArray();
        var first = await database.ImportAsync(WfoParserTests.Tsv(all), "2026-06");
        Assert.Equal(28, first.Report.RowsImported);
        Assert.Equal(0, first.Report.RowsRejected);
        Assert.Equal(28, first.Report.WarningCount); // 25 invalid dates and three unresolved references.
        await using var db = database.Context();
        var originalId = await db.WfoTaxa.Where(x => x.TaxonId == species[0][0]).Select(x => x.Id).SingleAsync();
        var saved = await db.WfoTaxa.AsNoTracking().SingleAsync(x => x.TaxonId == species[0][0]);
        Assert.Equal(species[0][22], saved.TaxonRemarks);
        Assert.Equal(species[0][1], saved.ScientificNameId);
        Assert.Equal(new DateOnly(2022, 4, 16), saved.WfoCreatedAt);
        Assert.Null(saved.WfoModifiedAt);

        var search = (await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=ACER"))!;
        Assert.Equal(20, search.Length);
        Assert.False(search[0].TryGetProperty("taxonRemarks", out _));
        Assert.Equal(26, (await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=acer&pageSize=100"))!.Length);
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=epithet24"))!);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=%25"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reference/plants/search?q=acer&pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reference/plants/search")).StatusCode);
        var details = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/wfo-synonym");
        Assert.Equal(species[0][0], details.GetProperty("taxon").GetProperty("acceptedTaxon").GetProperty("taxonId").GetString());
        Assert.Equal(species[1][0], details.GetProperty("taxon").GetProperty("originalTaxon").GetProperty("taxonId").GetString());
        Assert.Equal(2, details.GetProperty("taxon").GetProperty("acceptedTaxon").EnumerateObject().Count());
        var stats = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/stats");
        Assert.Equal(28, stats.GetProperty("currentRecords").GetInt64());
        Assert.Equal(25, stats.GetProperty("resolvedParentRelationships").GetInt64());
        Assert.Equal(1, stats.GetProperty("unresolvedAcceptedNameRelationships").GetInt64());
        Assert.Equal("2026-06", stats.GetProperty("lastImportedWfoVersion").GetString());

        var skipped = await database.ImportAsync(WfoParserTests.Tsv(all), "2026-06");
        Assert.True(skipped.AlreadyImported);
        Assert.Equal(first.ImportId, skipped.ImportId);
        Assert.Equal(1, await db.WfoImports.CountAsync());
        var forced = await database.ImportAsync(WfoParserTests.Tsv(all), "2026-06", force: true);
        Assert.False(forced.AlreadyImported);
        Assert.Equal(originalId, await db.WfoTaxa.Where(x => x.TaxonId == species[0][0]).Select(x => x.Id).SingleAsync());

        await Assert.ThrowsAsync<InvalidDataException>(() => database.ImportAsync(WfoParserTests.Tsv(genus, genus), "duplicate"));
        await Assert.ThrowsAsync<InvalidDataException>(() => database.ImportAsync(WfoParserTests.Tsv(genus) + "too\tfew\n", "malformed"));
        Assert.Equal(28, await db.WfoTaxa.CountAsync(x => x.IsCurrent));
        Assert.Equal(2, await db.WfoImports.CountAsync(x => x.Status == "Failed"));

        // A database failure after publication starts must roll back updates and IsCurrent changes.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reference.fail_taxon_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."ScientificName" = 'trigger-failure' THEN RAISE EXCEPTION 'test failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_taxon_update BEFORE INSERT OR UPDATE ON reference.wfo_taxon
            FOR EACH ROW EXECUTE FUNCTION reference.fail_taxon_update();
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => database.ImportAsync(
            WfoParserTests.Tsv(WfoParserTests.Row(species[0][0], "trigger-failure")), "failure"));
        Assert.Equal(28, await db.WfoTaxa.CountAsync(x => x.IsCurrent));
        Assert.Equal("Acer species 00", await db.WfoTaxa.Where(x => x.Id == originalId).Select(x => x.ScientificName).SingleAsync());

        // The genus is absent in the next release: preserve its raw ID, but clear the resolved FK.
        species[0][3] = "Acer updated";
        var second = await database.ImportAsync(WfoParserTests.Tsv(species[0]), "2026-09");
        Assert.Equal(1, second.Report.RowsImported);
        Assert.Equal(28, await db.WfoTaxa.CountAsync());
        Assert.Equal(1, await db.WfoTaxa.CountAsync(x => x.IsCurrent));
        saved = await db.WfoTaxa.AsNoTracking().SingleAsync(x => x.TaxonId == species[0][0]);
        Assert.Equal(originalId, saved.Id);
        Assert.Equal("wfo-genus", saved.ParentNameUsageId);
        Assert.Null(saved.ParentId);
        Assert.Equal(second.ImportId, saved.ImportId);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/reference/plants/search?q=Old%20maple"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/reference/plants/wfo-genus")).StatusCode);
        stats = await client.GetFromJsonAsync<JsonElement>("/api/reference/plants/stats");
        Assert.Equal(28, stats.GetProperty("totalRecords").GetInt64());
        Assert.Equal(1, stats.GetProperty("currentRecords").GetInt64());
        Assert.Equal("2026-09", stats.GetProperty("lastImportedWfoVersion").GetString());
    }

    internal sealed class TestDatabase(string adminConnection, string name, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        private readonly string sourcePath = Path.Combine(Path.GetTempPath(), "acbf-wfo-test-" + Guid.NewGuid().ToString("N") + ".tsv");

        public AcbfDbContext Context() => new(new DbContextOptionsBuilder<AcbfDbContext>().UseNpgsql(ConnectionString).Options);
        public static async Task<TestDatabase> CreateAsync()
        {
            var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ACBF_TEST_POSTGRES")!);
            builder.Database = "postgres";
            var admin = builder.ConnectionString;
            var name = "acbf_wfo_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
            builder.Database = name;
            var database = new TestDatabase(admin, name, builder.ConnectionString);
            await using var db = database.Context();
            await db.Database.MigrateAsync();
            return database;
        }

        public async Task<ImportResult> ImportAsync(string source, string version, bool force = false)
        {
            await File.WriteAllTextAsync(sourcePath, source);
            return await new WfoImporter(ConnectionString, TextWriter.Null).ImportAsync(sourcePath, version, force, TextWriter.Null);
        }

        public async ValueTask DisposeAsync()
        {
            File.Delete(sourcePath);
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            // The database name was generated here and never points at a pre-existing user database.
            await using var command = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
