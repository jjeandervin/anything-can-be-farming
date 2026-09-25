using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.Api.PlantNet;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class PlantNetClientTests
{
    private const string ApiKey = "test+key&with=special?characters";
    private static readonly PlantNetImageUpload Image = new([1, 2, 3], "leaf.png", "image/png");

    [Fact]
    public async Task Registration_binds_key_and_configures_https_client()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlantNet:ApiKey"] = ApiKey
        }).Build();
        var services = new ServiceCollection();
        services.AddPlantNet(configuration);
        services.AddHttpClient<PlantNetClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
        {
            Assert.Equal("https://my-api.plantnet.org/v2/languages", request.RequestUri!.GetLeftPart(UriPartial.Path));
            Assert.Equal(ApiKey, QueryHelpers.ParseQuery(request.RequestUri.Query)["api-key"]);
            Assert.Contains(request.Headers.Accept, header => header.MediaType == "application/json");
            return Task.FromResult(Json("[\"en\",\"fr\"]"));
        }));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(["en", "fr"], await provider.GetRequiredService<PlantNetClient>().GetLanguagesAsync());
    }

    [Fact]
    public async Task Missing_key_allows_health_but_fails_authenticated_requests_before_sending()
    {
        var calls = 0;
        using var http = Http((request, _) =>
        {
            calls++;
            Assert.Equal("/v2/_status", request.RequestUri!.AbsolutePath);
            Assert.Empty(request.RequestUri.Query);
            return Task.FromResult(Json("{\"status\":\"ok\"}"));
        });
        var client = Client(http, "");
        Assert.Equal("ok", (await client.GetStatusAsync()).Status);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetQuotaAsync());
        Assert.Contains("PlantNet:ApiKey", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Identification_uploads_repeated_files_and_organs_and_reads_nested_results()
    {
        using var http = Http(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v2/identify/all", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal(ApiKey, query["api-key"]);
            Assert.Equal("true", query["include-related-images"]);
            Assert.Equal("false", query["no-reject"]);
            Assert.Equal("3", query["nb-results"]);
            Assert.Equal("fr", query["lang"]);
            Assert.Equal("true", query["detailed"]);
            Assert.Equal("kt", query["type"]);
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal(4, parts.Length);
            Assert.All(parts.Take(2), part => Assert.Equal("images", part.Headers.ContentDisposition!.Name!.Trim('"')));
            Assert.Equal("leaf.png", parts[0].Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal("image/png", parts[0].Headers.ContentType!.MediaType);
            Assert.Equal(Image.Content, await parts[0].ReadAsByteArrayAsync(token));
            Assert.All(parts.Skip(2), part => Assert.Equal("organs", part.Headers.ContentDisposition!.Name!.Trim('"')));
            Assert.Equal("leaf", await parts[2].ReadAsStringAsync(token));
            Assert.Equal("flower", await parts[3].ReadAsStringAsync(token));
            return Json("""
                {"preferedReferential":"k-world-flora","bestMatch":"Acer rubrum L.",
                 "remainingIdentificationRequests":499,"results":[{"score":0.98,
                 "species":{"scientificName":"Acer rubrum L.","commonNames":["Red maple"],
                 "genus":{"scientificName":"Acer"},"family":{"scientificName":"Sapindaceae"}},
                 "gbif":{"id":3189866},"powo":{"id":"urn:lsid:example"},
                 "images":[{"url":{"o":"https://example.org/maple.jpg"},"license":"CC-BY"}]}]}
                """);
        });
        var result = await Client(http).IdentifyAsync([Image, Image], options: new()
        {
            Organs = ["leaf", "flower"], Language = "fr", IncludeRelatedImages = true,
            NoReject = false, NumberOfResults = 3, Detailed = true, Type = "kt"
        });
        Assert.Equal("k-world-flora", result.PreferredReferential);
        Assert.Equal(499, result.RemainingIdentificationRequests);
        var match = Assert.Single(result.Results!);
        Assert.Equal(0.98, match.Score);
        Assert.Equal("Acer", match.Species!.Genus!.ScientificName);
        Assert.Equal("Red maple", Assert.Single(match.Species.CommonNames!));
        Assert.Equal(3189866, match.Gbif!.Id);
        Assert.Equal("https://example.org/maple.jpg", Assert.Single(match.Images!).Url!.O);
    }

    [Fact]
    public async Task Url_identification_encodes_each_image_as_a_separate_query_parameter()
    {
        string[] images = ["https://example.org/leaf.png?size=large&x=1", "https://example.org/flower.png"];
        using var http = Http((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.Equal(images.AsEnumerable(), query["images"].AsEnumerable());
            Assert.Equal(new[] { "leaf", "flower" }.AsEnumerable(), query["organs"].AsEnumerable());
            Assert.Equal(ApiKey, query["api-key"]);
            return Task.FromResult(Json("{}"));
        });
#pragma warning disable CS0618 // Verify the deprecated operation in the supplied contract.
        await Client(http).IdentifyUrlsAsync(images, options: new() { Organs = ["leaf", "flower"] });
#pragma warning restore CS0618
    }

    [Fact]
    public async Task Species_preserves_empty_pagination_and_encodes_project_and_prefix()
    {
        using var http = Http((request, _) =>
        {
            Assert.Equal("/v2/projects/project%2Fwith%3Fcharacters/species", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.True(query.ContainsKey("page"));
            Assert.True(query.ContainsKey("pageSize"));
            Assert.Equal("", query["page"]);
            Assert.Equal("", query["pageSize"]);
            Assert.Equal("Acer & érable", query["prefix"]);
            Assert.Equal("true", query["images"]);
            Assert.False(query.ContainsKey("lang"));
            return Task.FromResult(Json("[{\"id\":\"acer\",\"genus\":\"Acer\",\"family\":\"Sapindaceae\"}]"));
        });
        var species = await Client(http).GetProjectSpeciesAsync("project/with?characters",
            new() { Page = "", PageSize = "", Prefix = "Acer & érable", Images = true });
        Assert.Equal("Acer", Assert.Single(species).Genus);
    }

    [Fact]
    public async Task Coordinates_and_dates_use_invariant_formats()
    {
        using var http = Http((request, _) =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath == "/v2/projects")
            {
                Assert.Equal("48.85", query["lat"]);
                Assert.Equal("2.35", query["lon"]);
                return Task.FromResult(Json("[]"));
            }
            Assert.Equal("2026-09-24", query["day"]);
            return Task.FromResult(Json("{\"quota\":{\"identify\":{\"count\":5,\"total\":500,\"remaining\":495}}}"));
        });
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var client = Client(http);
            await client.GetProjectsAsync(latitude: 48.85, longitude: 2.35);
            Assert.Equal(495, (await client.GetDailyQuotaAsync(new DateOnly(2026, 9, 24))).Quota!.Identify!.Remaining);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Theory]
    [InlineData("diseases")]
    [InlineData("varieties")]
    [InlineData("embeddings")]
    public async Task Specialized_uploads_use_their_contracts(string service)
    {
        using var http = Http((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(service == "embeddings" ? "/v2/embeddings" : $"/v2/{service}/identify", request.RequestUri!.AbsolutePath);
            var part = Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content));
            Assert.Equal(service == "embeddings" ? "image" : "images", part.Headers.ContentDisposition!.Name!.Trim('"'));
            return Task.FromResult(Json(service switch
            {
                "diseases" => "{\"results\":[{\"name\":\"EPPO-code\",\"score\":0.8}]}",
                "varieties" => "{\"results\":[{\"varieties\":[{\"name\":\"cultivar\",\"score\":0.9}]}]}",
                _ => "{\"embeddings\":[0.25,-0.5]}"
            }));
        });
        var client = Client(http);
        if (service == "diseases")
            Assert.Equal("EPPO-code", Assert.Single((await client.IdentifyDiseasesAsync([Image])).Results!).Name);
        else if (service == "varieties")
            Assert.Equal("cultivar", Assert.Single(Assert.Single((await client.IdentifyVarietiesAsync([Image])).Results!).Varieties!).Name);
        else
            Assert.Equal([0.25, -0.5], (await client.GetEmbeddingsAsync(Image)).Embeddings);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Failures_preserve_status_without_echoing_key_or_response_body(int status)
    {
        using var http = Http((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent($"Sensitive upstream error for {request.RequestUri} {ApiKey}")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).GetQuotaAsync());
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain(ApiKey, error.ToString());
        Assert.DoesNotContain("Sensitive", error.ToString());
        Assert.DoesNotContain("api-key", error.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not JSON")]
    public async Task Invalid_response_is_reported(string body)
    {
        using var http = Http((_, _) => Task.FromResult(Json(body)));
        await Assert.ThrowsAnyAsync<JsonException>(() => Client(http).GetQuotaAsync());
    }

    [Fact]
    public async Task Cancellation_reaches_http_transport()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = Http(async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return Json("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GetQuotaAsync(cancellation.Token));
    }

    [Fact]
    public async Task Invalid_uploads_fail_before_network_access()
    {
        using var http = Http((_, _) => throw new InvalidOperationException("Unexpected network request."));
        var client = Client(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.IdentifyAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.IdentifyAsync(Enumerable.Repeat(Image, 6).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.IdentifyAsync([Image], options: new() { Organs = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.IdentifyAsync([Image], options: new() { Organs = ["unknown"] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.IdentifyAsync([Image with { ContentType = "text/plain" }]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.IdentifyAsync([Image], options: new() { NumberOfResults = 0 }));
    }

    private static PlantNetClient Client(HttpClient http, string key = ApiKey) =>
        new(http, Options.Create(new PlantNetOptions { ApiKey = key }));

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send)) { BaseAddress = new Uri("https://my-api.plantnet.org/") };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
