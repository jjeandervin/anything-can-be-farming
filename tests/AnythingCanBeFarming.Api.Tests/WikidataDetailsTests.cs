using System.Text.Json;
using AnythingCanBeFarming.DataImport;
using Microsoft.AspNetCore.WebUtilities;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WikidataDetailsTests
{
    private const string Response = """
        {"entities":{
          "Q159657":{"type":"item","id":"Q159657","lastrevid":2212345678,
            "labels":{"en":{"language":"en","value":"Acer palmatum"}},
            "sitelinks":{"enwiki":{"site":"enwiki","title":"Acer palmatum","badges":[]}},
            "claims":{
              "P225":[{"mainsnak":{"snaktype":"value","property":"P225","datavalue":{"value":"Acer palmatum","type":"string"},"datatype":"string"},"type":"statement","rank":"normal"}],
              "P105":[{"mainsnak":{"snaktype":"value","property":"P105","datavalue":{"value":{"entity-type":"item","numeric-id":7432,"id":"Q7432"},"type":"wikibase-entityid"}},"rank":"normal"}],
              "P1843":[
                {"mainsnak":{"snaktype":"value","datavalue":{"value":{"text":"Japanese maple","language":"en"},"type":"monolingualtext"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":{"text":"Japanese maple","language":"en"},"type":"monolingualtext"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":{"text":"Japanese Maple","language":"en-gb"},"type":"monolingualtext"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":{"text":"Érable du Japon","language":"fr"},"type":"monolingualtext"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":{"text":"<b>Wrong</b>","language":"en"},"type":"monolingualtext"}},"rank":"deprecated"},
                {"mainsnak":{"snaktype":"somevalue"},"rank":"normal"}],
              "P18":[
                {"mainsnak":{"snaktype":"value","datavalue":{"value":"Second.jpg","type":"string"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":"Best.jpg","type":"string"}},"rank":"preferred"}],
              "P846":[
                {"mainsnak":{"snaktype":"value","datavalue":{"value":"3189846","type":"string"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":"8351233","type":"string"}},"rank":"normal"},
                {"mainsnak":{"snaktype":"value","datavalue":{"value":"0000","type":"string"}},"rank":"deprecated"}],
              "P961":[{"mainsnak":{"snaktype":"value","datavalue":{"value":"786332-1","type":"string"}},"rank":"normal"}],
              "P1772":[{"mainsnak":{"snaktype":"novalue"},"rank":"normal"}],
              "P5037":[{"mainsnak":{"snaktype":"value","datavalue":{"value":"urn:lsid:ipni.org:names:786332-1","type":"string"}},"rank":"normal"}],
              "P7715":[{"mainsnak":{"snaktype":"value","datavalue":{"value":"wfo-0000514950","type":"string"}},"rank":"preferred"}]}},
          "Q2":{"type":"item","id":"Q20","lastrevid":5,"redirects":{"from":"Q2","to":"Q20"},"labels":{},"sitelinks":{},"claims":{}},
          "Q3":{"id":"Q3","missing":""},
          "Q4":{"type":"item","id":"Q4","lastrevid":7,"labels":{},"sitelinks":{},"claims":[]},
          "P5":{"type":"property","id":"P5","datatype":"string"}
        },"success":1}
        """;

    [Fact]
    public void Parses_entities_with_all_values_and_ignores_deprecated_and_valueless_claims()
    {
        var warnings = new List<string>();
        var entities = WikidataEntityParser.Parse(JsonDocument.Parse(Response).RootElement, warnings);
        Assert.Equal(["Q159657", "Q2", "Q3", "Q4"], entities.Select(x => x.RequestedQid));
        Assert.Single(warnings);

        var maple = entities[0];
        Assert.False(maple.IsRedirect);
        Assert.False(maple.Missing);
        Assert.Equal(2212345678, maple.LastRevId);
        Assert.Equal("Acer palmatum", maple.LabelEn);
        Assert.Equal("Acer palmatum", maple.EnwikiTitle);
        Assert.Equal("Acer palmatum", maple.TaxonName);
        Assert.Equal("Q7432", maple.TaxonRankQid);
        Assert.Equal("Best.jpg", maple.ImageFile);
        Assert.Equal([new("en", "Japanese maple"), new("en-gb", "Japanese Maple"), new("fr", "Érable du Japon")], maple.CommonNames);
        Assert.Equal([new("P846", "3189846"), new("P846", "8351233"), new("P961", "786332-1"), new("P5037", "urn:lsid:ipni.org:names:786332-1")],
            maple.ExternalIds);
        Assert.Equal(["wfo-0000514950"], maple.WfoIds);
    }

    [Fact]
    public void Parses_redirected_missing_and_bare_entities()
    {
        var entities = WikidataEntityParser.Parse(JsonDocument.Parse(Response).RootElement, new List<string>());
        var redirect = entities[1];
        Assert.True(redirect.IsRedirect);
        Assert.Equal(("Q2", "Q20", 5L), (redirect.RequestedQid, redirect.Qid, redirect.LastRevId));
        var missing = entities[2];
        Assert.True(missing.Missing);
        Assert.Null(missing.LastRevId);
        var bare = entities[3];
        Assert.Equal(7, bare.LastRevId);
        Assert.Null(bare.LabelEn);
        Assert.Null(bare.EnwikiTitle);
        Assert.Null(bare.ImageFile);
        Assert.Null(bare.TaxonName);
        Assert.Empty(bare.CommonNames);
        Assert.Empty(bare.ExternalIds);
    }

    [Fact]
    public async Task Entity_requests_use_the_documented_parameters_and_reject_oversized_batches()
    {
        var fake = new FakeWikidata();
        fake.AddEntity("Q1", 10, "Acer");
        using var clients = fake.Clients();
        await clients.Api.GetEntitiesAsync(["Q1", "Q2"], infoOnly: false, new List<string>(), default);
        await clients.Api.GetEntitiesAsync(["Q1"], infoOnly: true, new List<string>(), default);
        var full = QueryHelpers.ParseQuery(fake.Requests[0].RequestUri!.Query);
        Assert.Equal("Q1|Q2", full["ids"]);
        Assert.Equal("info|labels|claims|sitelinks", full["props"]);
        Assert.Equal("en", full["languages"]);
        Assert.Equal("enwiki", full["sitefilter"]);
        Assert.Equal("json", full["format"]);
        Assert.Equal("5", full["maxlag"]);
        Assert.Equal("info", QueryHelpers.ParseQuery(fake.Requests[1].RequestUri!.Query)["props"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => clients.Api.GetEntitiesAsync(
            Enumerable.Range(1, 51).Select(i => $"Q{i}").ToArray(), infoOnly: true, new List<string>(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => clients.Api.GetEntitiesAsync(["Q1|Q2"], infoOnly: true, new List<string>(), default));
    }

    [Fact]
    public async Task Maxlag_on_entity_requests_waits_for_retry_after_then_succeeds()
    {
        var fake = new FakeWikidata();
        fake.AddEntity("Q1", 10);
        var maxlag = FakeWikidata.Json(new { error = new { code = "maxlag" } });
        maxlag.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
        fake.ApiResponses.Enqueue(() => maxlag);
        using var clients = fake.Clients();
        var entity = Assert.Single(await clients.Api.GetEntitiesAsync(["Q1"], infoOnly: true, new List<string>(), default));
        Assert.Equal(10, entity.LastRevId);
        Assert.Equal([TimeSpan.FromSeconds(7)], fake.Delays);
    }
}
