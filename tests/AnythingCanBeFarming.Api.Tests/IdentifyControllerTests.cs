using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.Api.PlantNet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class IdentifyControllerTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] Gif = "GIF89a\x01\x00\x01\x00"u8.ToArray();

    private readonly InfrastructureTests.ApiFactory factory = new();
    private readonly List<CapturedRequest> requests = [];
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> upstream =
        (_, _) => Task.FromResult(Json("""{"results":[]}"""));

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task Requires_a_token()
    {
        using var client = Client(authenticated: false);
        var response = await client.PostAsync("/api/identify", Form((Jpeg, "leaf")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(requests);
    }

    public static TheoryData<string, Func<MultipartFormDataContent>> InvalidForms => new()
    {
        { "no_images", () => Form() },
        { "no_images", () => OrgansOnly("leaf") },
        { "too_many_images", () => Form(Enumerable.Repeat((Jpeg, "leaf"), 6).ToArray()) },
        { "organ_count_mismatch", () => Form([Jpeg, Jpeg], ["leaf"]) },
        { "organ_count_mismatch", () => Form([Jpeg], []) },
        { "invalid_organ", () => Form((Jpeg, "Leaf")) },
        { "invalid_organ", () => Form((Jpeg, " leaf")) },
        { "invalid_organ", () => Form((Jpeg, "1")) },
        { "invalid_organ", () => Form((Jpeg, "root")) },
        { "image_too_large", () => Form((Jpeg.Concat(new byte[8 * 1024 * 1024]).ToArray(), "leaf")) },
        { "unsupported_image", () => Form((Gif, "leaf")) },
        { "unsupported_image", () => Form(("not really a jpeg"u8.ToArray(), "leaf")) },
        { "unsupported_image", () => Form((Jpeg[..2], "leaf")) },
        { "empty_image", () => Form(([], "leaf")) }
    };

    [Theory]
    [MemberData(nameof(InvalidForms))]
    public async Task Invalid_requests_return_400_with_a_code(string code, Func<MultipartFormDataContent> form)
    {
        using var client = Client();
        var response = await client.PostAsync("/api/identify", form());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Jpeg_and_png_are_forwarded_with_sniffed_types_indexed_names_and_ordered_organs()
    {
        using var client = Client();
        // Client-supplied content types and names are deliberately wrong; only the bytes count.
        var response = await client.PostAsync("/api/identify",
            Form([Png, Jpeg, Png], ["flower", "auto", "habit"], contentType: "application/octet-stream", fileName: "../IMG_0001.HEIC"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(requests);
        Assert.Equal("/v2/identify/all", request.Path);
        Assert.Equal(["image-0.png", "image-1.jpg", "image-2.png"], request.Files.Select(f => f.FileName));
        Assert.Equal(["image/png", "image/jpeg", "image/png"], request.Files.Select(f => f.ContentType));
        Assert.Equal(Png, request.Files[0].Content);
        Assert.Equal(Jpeg, request.Files[1].Content);
        Assert.Equal(["flower", "auto", "habit"], request.Organs);
    }

    [Fact]
    public async Task Query_requests_related_images_in_english_without_limiting_results()
    {
        using var client = Client();
        await client.PostAsync("/api/identify", Form((Jpeg, "leaf")));
        var query = Assert.Single(requests).Query;
        Assert.Equal("true", query["include-related-images"]);
        Assert.Equal("en", query["lang"]);
        Assert.False(query.ContainsKey("nb-results"));
        Assert.Equal("test-key", query["api-key"]);
    }

    [Fact]
    public async Task Maps_a_realistic_response_to_the_public_shape()
    {
        upstream = (_, _) => Task.FromResult(Json(Fixture));
        using var client = Client();
        var response = await client.PostAsync("/api/identify", Form([Jpeg, Png], ["auto", "flower"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["bestMatch", "remainingRequests", "predictedOrgans", "results"],
            body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Acer palmatum Thunb.", body.GetProperty("bestMatch").GetString());
        Assert.Equal(487, body.GetProperty("remainingRequests").GetInt32());

        // Entries with an unknown filename, a missing score, or an unknown organ are omitted.
        var predicted = body.GetProperty("predictedOrgans").EnumerateArray().ToArray();
        Assert.Equal(2, predicted.Length);
        Assert.Equal(0, predicted[0].GetProperty("imageIndex").GetInt32());
        Assert.Equal("leaf", predicted[0].GetProperty("organ").GetString());
        Assert.Equal(0.93, predicted[0].GetProperty("score").GetDouble());
        Assert.Equal(1, predicted[1].GetProperty("imageIndex").GetInt32());
        Assert.Equal("flower", predicted[1].GetProperty("organ").GetString());

        // Null score, null species, and empty scientific name are dropped; order is preserved.
        var results = body.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(["Acer palmatum Thunb.", "Acer rubrum L.", "Mystery plant"],
            results.Select(r => r.GetProperty("scientificName").GetString()));

        var first = results[0];
        Assert.Equal(0.8123, first.GetProperty("score").GetDouble());
        Assert.Equal("Acer palmatum", first.GetProperty("scientificNameWithoutAuthor").GetString());
        Assert.Equal("Thunb.", first.GetProperty("authorship").GetString());
        Assert.Equal("Acer", first.GetProperty("genus").GetString());
        Assert.Equal("Sapindaceae", first.GetProperty("family").GetString());
        Assert.Equal(["Japanese maple", "Smooth Japanese maple"],
            first.GetProperty("commonNames").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(3189846L, first.GetProperty("gbifId").GetInt64());
        Assert.Equal("783213-1", first.GetProperty("powoId").GetString());
        var images = first.GetProperty("referenceImages").EnumerateArray().ToArray();
        Assert.Equal(2, images.Length); // The HTTP-only image is dropped.
        Assert.Equal("leaf", images[0].GetProperty("organ").GetString());
        Assert.Equal("https://bs.plantnet.org/image/s/a", images[0].GetProperty("thumbnailUrl").GetString());
        Assert.Equal("https://bs.plantnet.org/image/m/a", images[0].GetProperty("imageUrl").GetString());
        Assert.Equal("https://bs.plantnet.org/image/o/a", images[0].GetProperty("fullUrl").GetString());
        Assert.Equal("Jane Doe", images[0].GetProperty("author").GetString());
        Assert.Equal("cc-by-sa", images[0].GetProperty("license").GetString());
        Assert.Equal("Jane Doe / Pl@ntNet, cc-by-sa", images[0].GetProperty("citation").GetString());
        // Only a medium size: every URL falls back to it; the non-HTTPS original is ignored.
        Assert.Equal("https://bs.plantnet.org/image/m/b", images[1].GetProperty("thumbnailUrl").GetString());
        Assert.Equal("https://bs.plantnet.org/image/m/b", images[1].GetProperty("imageUrl").GetString());
        Assert.Equal("https://bs.plantnet.org/image/m/b", images[1].GetProperty("fullUrl").GetString());
        Assert.Equal(JsonValueKind.Null, images[1].GetProperty("author").ValueKind);

        // Genus and family fall back to scientificName; missing lists become empty.
        var second = results[1];
        Assert.Equal("Acer", second.GetProperty("genus").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("family").ValueKind);
        Assert.Empty(second.GetProperty("commonNames").EnumerateArray());
        Assert.Empty(second.GetProperty("referenceImages").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("gbifId").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("powoId").ValueKind);

        var third = results[2];
        Assert.Equal("Mystery plant", third.GetProperty("scientificNameWithoutAuthor").GetString());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("authorship").ValueKind);
        Assert.Equal(JsonValueKind.Null, third.GetProperty("genus").ValueKind);
    }

    [Fact]
    public async Task Upstream_404_is_a_no_match_answer()
    {
        upstream = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"statusCode":404,"error":"Not Found","message":"Species not found"}""")
        });
        using var client = Client();
        var response = await client.PostAsync("/api/identify", Form((Jpeg, "auto")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("results").EnumerateArray());
        Assert.Empty(body.GetProperty("predictedOrgans").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bestMatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("remainingRequests").ValueKind);
    }

    [Theory]
    [InlineData(429, 429, "quota_exceeded", "Daily identification limit reached. Try again tomorrow.")]
    [InlineData(400, 502, "upstream_rejected", "Pl@ntNet couldn't process these photos.")]
    [InlineData(413, 502, "upstream_rejected", "Pl@ntNet couldn't process these photos.")]
    [InlineData(415, 502, "upstream_rejected", "Pl@ntNet couldn't process these photos.")]
    [InlineData(401, 502, "upstream_error", "Plant identification is temporarily unavailable.")]
    [InlineData(500, 502, "upstream_error", "Plant identification is temporarily unavailable.")]
    [InlineData(503, 502, "upstream_error", "Plant identification is temporarily unavailable.")]
    public async Task Upstream_failures_are_mapped(int upstreamStatus, int status, string code, string message)
    {
        upstream = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstreamStatus)
        {
            Content = new StringContent("Sensitive upstream body")
        });
        await AssertFailure(status, code, message);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    public async Task Bad_upstream_json_is_an_upstream_error(string json)
    {
        upstream = (_, _) => Task.FromResult(Json(json));
        await AssertFailure(502, "upstream_error", "Plant identification is temporarily unavailable.");
    }

    [Fact]
    public async Task Transport_failure_and_timeout_are_upstream_errors()
    {
        upstream = (_, _) => throw new HttpRequestException("Connection refused");
        await AssertFailure(502, "upstream_error", "Plant identification is temporarily unavailable.");
        // HttpClient.Timeout surfaces as a TaskCanceledException that is not the request's own cancellation.
        upstream = (_, _) => throw new TaskCanceledException("Timed out", new TimeoutException());
        await AssertFailure(502, "upstream_error", "Plant identification is temporarily unavailable.");
    }

    [Fact]
    public async Task Missing_api_key_returns_503()
    {
        await AssertFailure(503, "identification_unavailable", "Plant identification isn't configured.", apiKey: "");
        Assert.Empty(requests);
    }

    [Fact]
    public void Pl_ntNet_client_times_out_after_30_seconds()
    {
        using var app = App();
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PlantNetClient));
        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
    }

    private async Task AssertFailure(int status, string code, string message, string apiKey = "test-key")
    {
        using var client = Client(apiKey: apiKey);
        var response = await client.PostAsync("/api/identify", Form((Jpeg, "leaf")));
        Assert.Equal(status, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sensitive", text);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.Equal(message, body.GetProperty("message").GetString());
    }

    private WebApplicationFactory<Program> App(string apiKey = "test-key") => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("PlantNet:ApiKey", apiKey);
        builder.ConfigureServices(services => services.AddHttpClient<PlantNetClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async (request, token) =>
            {
                requests.Add(await CapturedRequest.From(request, token));
                return await upstream(request, token);
            })));
    });

    private HttpClient Client(bool authenticated = true, string apiKey = "test-key")
    {
        var client = App(apiKey).CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        if (authenticated) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        return client;
    }

    private static MultipartFormDataContent Form(params (byte[] Content, string Organ)[] photos) =>
        Form(photos.Select(p => p.Content).ToArray(), photos.Select(p => p.Organ).ToArray());

    private static MultipartFormDataContent Form(byte[][] images, string[] organs,
        string contentType = "image/jpeg", string fileName = "photo.jpg")
    {
        var form = new MultipartFormDataContent();
        foreach (var image in images)
        {
            var part = new ByteArrayContent(image);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "images", fileName);
        }
        foreach (var organ in organs) form.Add(new StringContent(organ), "organs");
        return form;
    }

    private static MultipartFormDataContent OrgansOnly(string organ) =>
        new() { { new StringContent(organ), "organs" } };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record CapturedFile(string FileName, string? ContentType, byte[] Content);

    private sealed record CapturedRequest(string Path, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query,
        List<CapturedFile> Files, List<string> Organs)
    {
        public static async Task<CapturedRequest> From(HttpRequestMessage request, CancellationToken token)
        {
            List<CapturedFile> files = [];
            List<string> organs = [];
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "images")
                        files.Add(new CapturedFile(part.Headers.ContentDisposition.FileName!.Trim('"'),
                            part.Headers.ContentType?.MediaType, await part.ReadAsByteArrayAsync(token)));
                    else if (name == "organs")
                        organs.Add(await part.ReadAsStringAsync(token));
                }
            }
            return new CapturedRequest(request.RequestUri!.AbsolutePath, QueryHelpers.ParseQuery(request.RequestUri.Query),
                files, organs);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private const string Fixture = """
        {
          "query": {"project": "all", "images": ["a1", "b2"], "organs": ["auto", "flower"], "includeRelatedImages": true},
          "language": "en",
          "preferedReferential": "k-world-flora",
          "bestMatch": "Acer palmatum Thunb.",
          "results": [
            {
              "score": 0.8123,
              "species": {
                "scientificNameWithoutAuthor": "Acer palmatum",
                "scientificNameAuthorship": "Thunb.",
                "scientificName": "Acer palmatum Thunb.",
                "genus": {"scientificNameWithoutAuthor": "Acer", "scientificNameAuthorship": "", "scientificName": "Acer"},
                "family": {"scientificNameWithoutAuthor": "Sapindaceae", "scientificNameAuthorship": "", "scientificName": "Sapindaceae"},
                "commonNames": ["Japanese maple", "Smooth Japanese maple"]
              },
              "images": [
                {
                  "organ": "leaf", "author": "Jane Doe", "license": "cc-by-sa",
                  "date": {"timestamp": 1591798765012, "string": "June 10, 2020"},
                  "url": {"o": "https://bs.plantnet.org/image/o/a", "m": "https://bs.plantnet.org/image/m/a", "s": "https://bs.plantnet.org/image/s/a"},
                  "citation": "Jane Doe / Pl@ntNet, cc-by-sa"
                },
                {"organ": "flower", "url": {"o": "http://insecure.example/o/b", "m": "https://bs.plantnet.org/image/m/b"}},
                {"organ": "bark", "author": "HTTP only", "url": {"o": "http://insecure.example/o/c", "m": "http://insecure.example/m/c"}}
              ],
              "gbif": {"id": 3189846},
              "powo": {"id": "783213-1"},
              "iucn": {"id": "193845", "category": "LC"}
            },
            {"score": null, "species": {"scientificName": "Dropped for null score"}},
            {
              "score": 0.0912,
              "species": {
                "scientificNameWithoutAuthor": "Acer rubrum",
                "scientificNameAuthorship": "L.",
                "scientificName": "Acer rubrum L.",
                "genus": {"scientificName": "Acer"}
              }
            },
            {"score": 0.05},
            {"score": 0.04, "species": {"scientificName": ""}},
            {"score": 0.01, "species": {"scientificName": "Mystery plant"}, "images": null}
          ],
          "remainingIdentificationRequests": 487,
          "version": "2025-01-17 (7.3)",
          "predictedOrgans": [
            {"image": "a1", "filename": "image-0.jpg", "organ": "leaf", "score": 0.93},
            {"image": "b2", "filename": "image-1.png", "organ": "flower", "score": 0.88},
            {"image": "c3", "filename": "someone-elses.jpg", "organ": "leaf", "score": 0.5},
            {"image": "a1", "filename": "image-0.jpg", "organ": "leaf"},
            {"image": "a1", "filename": "image-0.jpg", "organ": "stem", "score": 0.4}
          ]
        }
        """;
}
