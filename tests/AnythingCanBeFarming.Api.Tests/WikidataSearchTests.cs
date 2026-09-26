using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WikidataSearchTests
{
    [PostgresFact]
    public async Task Search_ranks_matches_and_reports_one_row_per_accepted_taxon()
    {
        await using var database = await SeedAsync();
        using var factory = new InfrastructureTests.ApiFactory(connectionString: database.ConnectionString);
        using var client = Client(factory);

        var japanese = await SearchAsync(client, "Japanese maple");
        Assert.Equal(("Acer palmatum", "commonName", "Japanese maple", "Japanese maple"), Summary(japanese[0]));
        Assert.Equal("Accepted", japanese[0].GetProperty("taxonomicStatus").GetString());
        Assert.Equal("wfo-0000000002", japanese[0].GetProperty("taxonId").GetString());

        // A common-name exact match beats a substring match, and a taxon without an accepted taxon appears as itself.
        var red = await SearchAsync(client, "red maple");
        Assert.Equal([("Acer rubrum", "commonName", "red maple", "red maple"), ("Mystery plant", "commonName", "fired maple", "fired maple")],
            red.Select(Summary));
        Assert.Equal("Unchecked", red[1].GetProperty("taxonomicStatus").GetString());

        // A synonym match collapses into its accepted taxon.
        var synonym = Assert.Single(await SearchAsync(client, "polymorphum"));
        Assert.Equal(("Acer palmatum", "synonym", "Acer polymorphum", "momiji"), Summary(synonym));
        Assert.Equal(("Acer palmatum", "commonName", "fullmoon maple", "fullmoon maple"), Summary(Assert.Single(await SearchAsync(client, "FULLMOON"))));

        // Exact genus first, then accepted species by name, then infraspecific; one row per accepted taxon.
        var acer = await SearchAsync(client, "acer");
        Assert.Equal(["Acer", "Acer palmatum", "Acer rubrum", "Acer palmatum subsp. amoenum"], acer.Select(Name));
        Assert.Equal(JsonValueKind.Null, acer[0].GetProperty("commonName").ValueKind);
        Assert.Equal("scientificName", acer[1].GetProperty("matchedOn").GetString());

        var hosta = await SearchAsync(client, "hosta");
        Assert.Equal(["Hosta", "Hosta plantaginea"], hosta.Select(Name));
        Assert.Equal("hosta", hosta[0].GetProperty("commonName").GetString());

        var maple = await SearchAsync(client, "maple");
        Assert.Equal(maple.Length, maple.Select(x => x.GetProperty("taxonId").GetString()).Distinct().Count());
        Assert.Equal(["Acer palmatum", "Acer rubrum", "Mystery plant"], maple.Select(Name).Order(StringComparer.Ordinal));

        Assert.Equal(("Rudbeckia hirta", "commonName", "black-eyed Susan", "black-eyed Susan"),
            Summary(Assert.Single(await SearchAsync(client, "Black-Eyed  Susan"))));
        Assert.Equal("Rudbeckia hirta", Name(Assert.Single(await SearchAsync(client, "eyed"))));
        Assert.Empty(await SearchAsync(client, "%_"));
    }

    [PostgresFact]
    public async Task Best_common_name_prefers_en_then_the_matched_name_then_shortest_then_ordinal()
    {
        await using var database = await SeedAsync();
        using var factory = new InfrastructureTests.ApiFactory(connectionString: database.ConnectionString);
        using var client = Client(factory);

        // en beats en-gb even when only the en-gb name matched.
        var smooth = Assert.Single(await SearchAsync(client, "smooth japanese"));
        Assert.Equal(("Acer palmatum", "commonName", "Smooth Japanese maple", "momiji"), Summary(smooth));
        // Among en names, the one that matched beats a shorter one.
        Assert.Equal("Japanese maple", (await SearchAsync(client, "japanese"))[0].GetProperty("commonName").GetString());
        // Same length: ordinal order decides ("bog maple" < "red maple").
        var rubrum = (await SearchAsync(client, "maple")).Single(x => Name(x) == "Acer rubrum");
        Assert.Equal("bog maple", rubrum.GetProperty("commonName").GetString());
        Assert.Equal("bog maple", rubrum.GetProperty("matchedText").GetString());
    }

    [PostgresFact]
    public async Task Search_validates_query_length_and_ties_are_deterministic()
    {
        await using var database = await SeedAsync();
        using var factory = new InfrastructureTests.ApiFactory(connectionString: database.ConnectionString);
        using var client = Client(factory);
        foreach (var q in new[] { "a", "%20a%20", "%20%20%20", new string('x', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/reference/plants/search?q={q}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/reference/plants/search?q={new string('x', 200)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reference/plants/search?q=%CC%81%CC%81")).StatusCode);

        var first = await client.GetStringAsync("/api/reference/plants/search?q=ac%20");
        Assert.Equal(first, await client.GetStringAsync("/api/reference/plants/search?q=ac"));
        for (var i = 0; i < 3; i++) Assert.Equal(first, await client.GetStringAsync("/api/reference/plants/search?q=ac"));
        Assert.Equal(["Acer"], (await SearchAsync(client, "acer", pageSize: 1)).Select(Name));
        // Equal tier, status, and depth: ordinal scientific name order.
        Assert.Equal(["Acer palmatum", "Acer rubrum"], (await SearchAsync(client, "acer ")).Skip(1).Take(2).Select(Name));
    }

    private static async Task<WfoPostgresTests.TestDatabase> SeedAsync()
    {
        var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await WikidataPostgresTests.ImportBackboneAsync(database, "2026-06",
            WikidataPostgresTests.Taxon("wfo-0000000001", "Acer", rank: "genus"),
            WikidataPostgresTests.Taxon("wfo-0000000002", "Acer palmatum"),
            WikidataPostgresTests.Taxon("wfo-0000000003", "Acer polymorphum", "Synonym", "wfo-0000000002"),
            WikidataPostgresTests.Taxon("wfo-0000000004", "Acer rubrum"),
            WikidataPostgresTests.Taxon("wfo-0000000005", "Acer palmatum subsp. amoenum", rank: "subspecies"),
            WikidataPostgresTests.Taxon("wfo-0000000006", "Rudbeckia hirta"),
            WikidataPostgresTests.Taxon("wfo-0000000007", "Hosta", rank: "genus"),
            WikidataPostgresTests.Taxon("wfo-0000000008", "Hosta plantaginea"),
            WikidataPostgresTests.Taxon("wfo-0000000009", "Mystery plant", "Unchecked"));
        var fake = new FakeWikidata();
        void Item(string qid, string wfo, params (string Name, string Language)[] names)
        {
            fake.Add(qid, wfo);
            fake.AddEntity(qid, 1, null, null, names.Select(x => ("P1843", FakeWikidata.Text(x.Name, x.Language))).ToArray());
        }
        Item("Q2", "wfo-0000000002", ("Japanese maple", "en"), ("momiji", "en"), ("Smooth Japanese maple", "en-gb"), ("Érable du Japon", "fr"));
        Item("Q3", "wfo-0000000003", ("fullmoon maple", "en"));
        Item("Q4", "wfo-0000000004", ("red maple", "en"), ("swamp maple", "en"), ("bog maple", "en"));
        Item("Q6", "wfo-0000000006", ("black-eyed Susan", "en"));
        Item("Q7", "wfo-0000000007", ("hosta", "en"), ("plantain lily", "en"));
        Item("Q9", "wfo-0000000009", ("fired maple", "en"));
        await WikidataPostgresTests.CrosswalkAsync(database, fake);
        await WikidataPostgresTests.DetailsAsync(database, fake);
        return database;
    }

    private static HttpClient Client(InfrastructureTests.ApiFactory factory)
    {
        var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        return client;
    }

    private static async Task<JsonElement[]> SearchAsync(HttpClient client, string q, int? pageSize = null)
    {
        var response = await client.GetAsync($"/api/reference/plants/search?q={Uri.EscapeDataString(q)}{(pageSize is { } size ? $"&pageSize={size}" : "")}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
    }

    private static string? Name(JsonElement result) => result.GetProperty("scientificName").GetString();

    private static (string?, string?, string?, string?) Summary(JsonElement result) =>
        (Name(result), result.GetProperty("matchedOn").GetString(), result.GetProperty("matchedText").GetString(),
            result.GetProperty("commonName").GetString());
}
