using System.Net;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.DataImport;
using Microsoft.AspNetCore.WebUtilities;

namespace AnythingCanBeFarming.Api.Tests;

// Fixtures/wikipedia holds real responses captured from en.wikipedia.org on 2026-09-26.
public sealed class WikipediaParserTests
{
    private static readonly string[] CheckTitles =
    [
        "Acer saccharum", "Acer palmatum", "Rudbeckia hirta", "Hosta", "Cornus florida", "Echinacea purpurea", "Carica jamaicensis",
        "Papaya", "Tillandsia polita var. elongata", "Oxalis micrantha", "Amphiodon", "Musa × paradisiaca", "Victoria's owl-clover",
        "Orbea (plant)", "Acer nonexistentissimum"
    ];

    [Fact]
    public void Check_maps_every_requested_title_regardless_of_response_order()
    {
        var infos = WikipediaResponseParser.ParseCheck(Fixture("check.json"), CheckTitles).ToDictionary(x => x.RequestedTitle);
        Assert.Equal(CheckTitles.Length, infos.Count);

        var maple = infos["Acer saccharum"];
        Assert.Equal(("Acer saccharum", false, false, 174932L, 1373602881L, "Q214733", "Species of flowering plant", false),
            (maple.Title, maple.Redirected, maple.Missing, maple.PageId, maple.LastRevId, maple.WikibaseItem, maple.ShortDescription, maple.IsDisambiguation));
        Assert.Equal(("Hosta", "Q623347", "Genus of flowering plants"), (infos["Hosta"].Title, infos["Hosta"].WikibaseItem, infos["Hosta"].ShortDescription));
        Assert.Equal("Q10757112", infos["Musa × paradisiaca"].WikibaseItem);
        Assert.Equal("Q15348700", infos["Victoria's owl-clover"].WikibaseItem);

        // A species redirected to another species' article, and a variety redirected to its species.
        var papaya = infos["Carica jamaicensis"];
        Assert.Equal(("Papaya", true, "Q34887", 59249L), (papaya.Title, papaya.Redirected, papaya.WikibaseItem, papaya.PageId));
        Assert.Equal(papaya with { RequestedTitle = "Papaya", Redirected = false }, infos["Papaya"]);
        Assert.Equal(("Tillandsia polita", true, "Q7802674"), (infos["Tillandsia polita var. elongata"].Title,
            infos["Tillandsia polita var. elongata"].Redirected, infos["Tillandsia polita var. elongata"].WikibaseItem));

        var disambiguation = infos["Oxalis micrantha"];
        Assert.True(disambiguation.IsDisambiguation);
        Assert.Equal(("Q15346340", 1327878456L), (disambiguation.WikibaseItem, disambiguation.LastRevId));
        Assert.Equal("Q17479657", infos["Amphiodon"].WikibaseItem); // The sitelinking item is Q17479656.

        var missing = infos["Acer nonexistentissimum"];
        Assert.Equal(("Acer nonexistentissimum", true, false), (missing.Title, missing.Missing, missing.Redirected));
        Assert.Null(missing.PageId);
        Assert.Null(missing.LastRevId);
        Assert.Null(missing.WikibaseItem);
    }

    [Fact]
    public void Check_follows_normalization_then_redirects_and_maps_several_titles_to_one_page()
    {
        string[] titles = ["carica_jamaicensis", "acer_negundo", "Papaya", "Acer negundo"];
        var infos = WikipediaResponseParser.ParseCheck(Fixture("check-normalized.json"), titles);
        Assert.Equal(titles, infos.Select(x => x.RequestedTitle));
        Assert.Equal([("Papaya", true), ("Acer negundo", false), ("Papaya", false), ("Acer negundo", false)],
            infos.Select(x => (x.Title!, x.Redirected)));
        Assert.Equal(["Q34887", "Q161166", "Q34887", "Q161166"], infos.Select(x => x.WikibaseItem!));
        Assert.All(infos, x => Assert.False(x.Missing));
    }

    [Fact]
    public void Check_handles_redirect_chains_encoded_and_converted_titles_invalid_titles_and_absent_pages()
    {
        using var document = JsonDocument.Parse("""
            {"batchcomplete":true,"query":{
              "normalized":[{"fromencoded":true,"from":"Acer%20x","to":"Acer x"},{"fromencoded":false,"from":"a","to":"A"}],
              "converted":[{"from":"A","to":"B"}],
              "redirects":[{"from":"Acer x","to":"Middle"},{"from":"Middle","to":"Final","tofragment":"Section"},{"from":"Loop","to":"Loop"}],
              "pages":[
                {"pageid":3,"ns":0,"title":"Final","lastrevid":30,"pageprops":{"wikibase_item":"Q3"}},
                {"pageid":2,"ns":0,"title":"B","lastrevid":20},
                {"title":"Talk:","invalidreason":"The requested page title is empty or contains only a namespace prefix.","invalid":true}]}}
            """);
        var infos = WikipediaResponseParser.ParseCheck(document.RootElement, ["Acer x", "a", "Talk:", "Loop", "Not returned"]);
        Assert.Equal(("Final", true, "Q3", 30L), (infos[0].Title, infos[0].Redirected, infos[0].WikibaseItem, infos[0].LastRevId));
        Assert.Equal(("B", false, 20L), (infos[1].Title, infos[1].Redirected, infos[1].LastRevId));
        Assert.Null(infos[1].WikibaseItem);
        Assert.All(infos.Skip(2), x => Assert.True(x.Missing));
    }

    [Fact]
    public void Leads_store_extracts_exactly_as_returned_with_their_revision()
    {
        string[] titles = ["Acer saccharum", "Hosta", "Carica jamaicensis", "Amphiodon", "Acer nonexistentissimum"];
        var leads = WikipediaResponseParser.ParseLeads([Fixture("leads.json")], titles).ToDictionary(x => x.RequestedTitle);
        var raw = RawExtracts("leads.json");
        Assert.Equal(raw["Acer saccharum"], leads["Acer saccharum"].Extract);
        Assert.StartsWith("Acer saccharum, the sugar maple, is a species of flowering plant", leads["Acer saccharum"].Extract);
        Assert.Equal(1373602881, leads["Acer saccharum"].LastRevId);
        // TextExtracts strips the pronunciation but leaves "(, " behind; the text is still stored unmodified.
        Assert.StartsWith("Hosta (, syn. Funkia) is a genus", leads["Hosta"].Extract);
        Assert.Equal(("Papaya", true, raw["Papaya"]), (leads["Carica jamaicensis"].Title, leads["Carica jamaicensis"].Redirected,
            leads["Carica jamaicensis"].Extract));
        Assert.Equal(185, leads["Amphiodon"].Extract!.Length);
        Assert.True(leads["Acer nonexistentissimum"].Missing);
        Assert.Null(leads["Acer nonexistentissimum"].Extract);
    }

    [Fact]
    public async Task Partial_extract_batches_follow_continue_and_merge_revisions_from_the_first_response()
    {
        var fake = new FakeWikipedia();
        fake.Responses.Enqueue(() => FakeWikipedia.Fixture("leads-continue-1.json"));
        fake.Responses.Enqueue(() => FakeWikipedia.Fixture("leads-continue-2.json"));
        using var clients = fake.Clients();
        var leads = await clients.Api.GetLeadsAsync(["Acer saccharum", "Hosta", "Cornus florida"], default);

        Assert.Equal(2, fake.HttpRequests.Count);
        Assert.Equal(1, clients.Api.ContinuationRequests);
        var first = QueryHelpers.ParseQuery(fake.HttpRequests[0].RequestUri!.Query);
        var second = QueryHelpers.ParseQuery(fake.HttpRequests[1].RequestUri!.Query);
        Assert.False(first.ContainsKey("excontinue"));
        Assert.Equal(("2", "||info"), (second["excontinue"].ToString(), second["continue"].ToString()));
        Assert.Equal(first["titles"], second["titles"]);

        Assert.Equal(["Acer saccharum", "Hosta", "Cornus florida"], leads.Select(x => x.RequestedTitle));
        Assert.All(leads, x => Assert.NotNull(x.Extract));
        Assert.StartsWith("Hosta (, syn. Funkia)", leads[1].Extract);
        Assert.Equal([1373602881L, 1370884806L, 1371112811L], leads.Select(x => x.LastRevId!.Value));
    }

    [Fact]
    public async Task A_batch_that_never_completes_fails_instead_of_looping()
    {
        var fake = new FakeWikipedia();
        for (var i = 0; i <= WikipediaApiClient.MaximumContinuations; i++)
            fake.Responses.Enqueue(() => FakeWikipedia.Fixture("leads-continue-1.json"));
        using var clients = fake.Clients();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => clients.Api.GetLeadsAsync(["Acer saccharum", "Hosta", "Cornus florida"], default));
        Assert.Contains("kept continuing", error.Message);
    }

    [Fact]
    public async Task Requests_use_the_documented_parameters_and_batch_limits()
    {
        var fake = new FakeWikipedia();
        fake.Add("Acer palmatum", 1, "Q1", "Lead.");
        using var clients = fake.Clients();
        await clients.Api.CheckAsync(["Acer palmatum", "Musa × paradisiaca"], default);
        await clients.Api.GetLeadsAsync(["Acer palmatum"], default);

        var check = QueryHelpers.ParseQuery(fake.HttpRequests[0].RequestUri!.Query);
        Assert.Equal(new Dictionary<string, string>
        {
            ["action"] = "query", ["prop"] = "info|pageprops", ["ppprop"] = "wikibase_item|wikibase-shortdesc|disambiguation", ["redirects"] = "1",
            ["titles"] = "Acer palmatum|Musa × paradisiaca", ["format"] = "json", ["formatversion"] = "2", ["maxlag"] = "5"
        }, check.ToDictionary(x => x.Key, x => x.Value.ToString()));
        var leads = QueryHelpers.ParseQuery(fake.HttpRequests[1].RequestUri!.Query);
        Assert.Equal(new Dictionary<string, string>
        {
            ["action"] = "query", ["prop"] = "extracts|info", ["exintro"] = "1", ["explaintext"] = "1", ["exsectionformat"] = "plain",
            ["exlimit"] = "20", ["redirects"] = "1", ["titles"] = "Acer palmatum", ["format"] = "json", ["formatversion"] = "2", ["maxlag"] = "5"
        }, leads.ToDictionary(x => x.Key, x => x.Value.ToString()));
        Assert.All(fake.HttpRequests, x => Assert.Equal(HttpMethod.Get, x.Method));
        Assert.All(fake.HttpRequests, x => Assert.StartsWith("AnythingCanBeFarming/", x.Headers.UserAgent.ToString()));

        var titles = Enumerable.Range(1, 51).Select(i => $"Title {i}").ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => clients.Api.CheckAsync(titles, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => clients.Api.GetLeadsAsync(titles[..21], default));
        await Assert.ThrowsAsync<ArgumentException>(() => clients.Api.CheckAsync(["A|B"], default));
        Assert.False(WikipediaApiClient.IsRequestable("Tab\there"));
        Assert.False(WikipediaApiClient.IsRequestable(" "));
    }

    [Fact]
    public async Task Maxlag_waits_for_retry_after_and_other_api_errors_are_sanitized()
    {
        var fake = new FakeWikipedia();
        fake.Add("Hosta", 1, "Q623347", "Hosta is a genus.");
        var maxlag = FakeWikipedia.Json(new { error = new { code = "maxlag", info = "Waiting for a database server: 6 seconds lagged." } });
        maxlag.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
        fake.Responses.Enqueue(() => maxlag);
        using var clients = fake.Clients();
        Assert.Equal("Q623347", Assert.Single(await clients.Api.CheckAsync(["Hosta"], default)).WikibaseItem);
        Assert.Equal([TimeSpan.FromSeconds(5)], fake.Delays);

        fake.Responses.Enqueue(() => FakeWikipedia.Json(new { error = new { code = "<script>", info = "https://en.wikipedia.org/secret" } }));
        var error = await Assert.ThrowsAsync<WikidataHttpException>(() => clients.Api.CheckAsync(["Hosta"], default));
        Assert.Equal("Wikipedia API error (unrecognized).", error.Message);
        fake.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Equal("Wikipedia returned HTTP 403.", (await Assert.ThrowsAsync<WikidataHttpException>(() => clients.Api.CheckAsync(["Hosta"], default))).Message);
    }

    [Fact]
    public async Task Very_long_queries_are_posted_instead_of_sent_in_the_url()
    {
        var fake = new FakeWikipedia();
        fake.Responses.Enqueue(() => FakeWikipedia.Fixture("check-normalized.json"));
        using var clients = fake.Clients();
        var titles = Enumerable.Range(1, 50).Select(i => $"{i} " + new string('×', 60)).ToArray();
        await clients.Api.CheckAsync(titles, default);
        var request = Assert.Single(fake.HttpRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/w/api.php", request.RequestUri!.AbsolutePath);
    }

    // Expected values are Wikipedia's own canonicalurl for each title (prop=info&inprop=url, 2026-09-26).
    [Theory]
    [InlineData("Musa × paradisiaca", "https://en.wikipedia.org/wiki/Musa_%C3%97_paradisiaca")]
    [InlineData("× Odontonia", "https://en.wikipedia.org/wiki/%C3%97_Odontonia")]
    [InlineData("Victoria's owl-clover", "https://en.wikipedia.org/wiki/Victoria%27s_owl-clover")]
    [InlineData("Orbea (plant)", "https://en.wikipedia.org/wiki/Orbea_(plant)")]
    [InlineData("AC/DC", "https://en.wikipedia.org/wiki/AC/DC")]
    [InlineData("Yacón", "https://en.wikipedia.org/wiki/Yac%C3%B3n")]
    [InlineData("C++", "https://en.wikipedia.org/wiki/C%2B%2B")]
    [InlineData("Rock & Roll", "https://en.wikipedia.org/wiki/Rock_%26_Roll")]
    [InlineData("Who Framed Roger Rabbit?", "https://en.wikipedia.org/wiki/Who_Framed_Roger_Rabbit%3F")]
    public void Urls_match_wikipedia_canonical_urls(string title, string expected) => Assert.Equal(expected, WikipediaUrl.For(title));

    [Theory]
    [InlineData(true, true, "Q2", "", "Missing")]
    [InlineData(false, true, "Q2", "", "Disambiguation")]
    [InlineData(false, false, "Q2", "", "ItemMismatch")]
    [InlineData(false, false, null, "A lead.", "ItemMismatch")]
    [InlineData(false, false, "Q1", " \n\t", "EmptyLead")]
    [InlineData(false, false, "Q1", null, "EmptyLead")]
    [InlineData(false, false, "Q1", "A lead.", "Ok")]
    [InlineData(false, false, "Q9", "A lead.", "Ok")]
    public void Status_rules_apply_in_order(bool missing, bool disambiguation, string? item, string? lead, string expected) =>
        Assert.Equal(expected, WikipediaLeadStatus.Evaluate(missing, disambiguation, item, ["Q1", "Q9"], lead));

    [Fact]
    public void Status_values_are_the_documented_set() =>
        Assert.Equal(["Pending", "Ok", "Missing", "Disambiguation", "ItemMismatch", "EmptyLead"],
            typeof(WikipediaArticleStatus).GetFields().Select(x => (string)x.GetValue(null)!));

    private static JsonElement Fixture(string name) => JsonDocument.Parse(File.ReadAllText(FakeWikipedia.FixturePath(name))).RootElement;

    private static Dictionary<string, string> RawExtracts(string name) =>
        Fixture(name).GetProperty("query").GetProperty("pages").EnumerateArray()
            .Where(x => x.TryGetProperty("extract", out _))
            .ToDictionary(x => x.GetProperty("title").GetString()!, x => x.GetProperty("extract").GetString()!);
}
