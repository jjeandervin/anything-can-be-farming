using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AnythingCanBeFarming.Api.PlantNet;

/// <summary>Client for the supplied My Pl@ntNet API v2.2.2 specification.</summary>
public sealed class PlantNetClient(HttpClient httpClient, IOptions<PlantNetOptions> options)
{
    private static readonly HashSet<string> ValidOrgans =
    [
        "auto", "leaf", "flower", "fruit", "bark", "habit", "scan", "branch", "sheet",
        "other", "drawing", "seed", "bud", "anatomy", "aerial"
    ];

    public Task<PlantNetStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<PlantNetStatus>(HttpMethod.Get, "v2/_status", [], cancellationToken, authenticated: false);

    public Task<List<string>> GetLanguagesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<string>>("v2/languages", [], cancellationToken);

    public Task<List<PlantNetDisease>> GetDiseasesAsync(string? language = null, string? prefix = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<List<PlantNetDisease>>("v2/diseases", [("lang", language), ("prefix", prefix)], cancellationToken);

    public Task<List<PlantNetVariety>> GetVarietiesAsync(string? language = null, string? prefix = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<List<PlantNetVariety>>("v2/varieties", [("lang", language), ("prefix", prefix)], cancellationToken);

    public Task<List<PlantNetProject>> GetProjectsAsync(string? language = null, double? latitude = null,
        double? longitude = null, string? type = null, CancellationToken cancellationToken = default)
    {
        if (latitude is { } lat && (!double.IsFinite(lat) || lat is < -90 or > 90))
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is { } lon && (!double.IsFinite(lon) || lon is < -180 or > 180))
            throw new ArgumentOutOfRangeException(nameof(longitude));
        ValidateType(type);
        return GetAsync<List<PlantNetProject>>("v2/projects",
            [("lang", language), ("lat", latitude), ("lon", longitude), ("type", type)], cancellationToken);
    }

    public Task<List<PlantNetSpecies>> GetSpeciesAsync(PlantNetSpeciesOptions? options = null,
        string? type = null, CancellationToken cancellationToken = default)
    {
        ValidateType(type);
        var query = SpeciesQuery(options);
        query.Add(("type", type));
        return GetAsync<List<PlantNetSpecies>>("v2/species", query, cancellationToken);
    }

    public Task<List<PlantNetSpecies>> GetProjectSpeciesAsync(string project,
        PlantNetSpeciesOptions? options = null, CancellationToken cancellationToken = default) =>
        GetAsync<List<PlantNetSpecies>>($"v2/projects/{ProjectSegment(project)}/species",
            SpeciesQuery(options), cancellationToken);

    public Task<PlantNetQuotaResponse> GetQuotaAsync(CancellationToken cancellationToken = default) =>
        GetAsync<PlantNetQuotaResponse>("v2/quota", [], cancellationToken);

    public Task<PlantNetDailyQuota> GetDailyQuotaAsync(DateOnly? day = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<PlantNetDailyQuota>("v2/quota/daily",
            [("day", day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))], cancellationToken);

    public Task<List<PlantNetDailyQuota>> GetQuotaHistoryAsync(int? year = null,
        CancellationToken cancellationToken = default)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        return GetAsync<List<PlantNetDailyQuota>>("v2/quota/history",
            [("year", year?.ToString("D4", CultureInfo.InvariantCulture))], cancellationToken);
    }

    public Task<PlantNetSubscription> GetSubscriptionAsync(CancellationToken cancellationToken = default) =>
        GetAsync<PlantNetSubscription>("v2/subscription", [], cancellationToken);

    public Task<PlantNetIdentificationResult> IdentifyAsync(IReadOnlyList<PlantNetImageUpload> images,
        string project = "all", PlantNetPlantIdentificationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        UploadAsync<PlantNetIdentificationResult>($"v2/identify/{ProjectSegment(project)}", images,
            options, cancellationToken);

    /// <summary>The URL-based GET operation is deprecated by Pl@ntNet. Prefer IdentifyAsync.</summary>
    [Obsolete("Pl@ntNet deprecates GET identification. Prefer IdentifyAsync with image uploads.")]
    public Task<PlantNetIdentificationResult> IdentifyUrlsAsync(IReadOnlyList<string> images,
        string project = "all", PlantNetPlantIdentificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ValidateImages(images.Count, options?.Organs);
        var query = IdentificationQuery(options);
        foreach (var image in images)
        {
            if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("Images must be absolute HTTP or HTTPS URLs.", nameof(images));
            query.Add(("images", image));
        }
        foreach (var organ in options?.Organs ?? []) query.Add(("organs", organ));
        return GetAsync<PlantNetIdentificationResult>($"v2/identify/{ProjectSegment(project)}", query, cancellationToken);
    }

    public Task<PlantNetDiseaseIdentificationResult> IdentifyDiseasesAsync(IReadOnlyList<PlantNetImageUpload> images,
        PlantNetIdentificationOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadAsync<PlantNetDiseaseIdentificationResult>("v2/diseases/identify", images, options, cancellationToken);

    public Task<PlantNetVarietyIdentificationResult> IdentifyVarietiesAsync(IReadOnlyList<PlantNetImageUpload> images,
        PlantNetIdentificationOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadAsync<PlantNetVarietyIdentificationResult>("v2/varieties/identify", images, options, cancellationToken);

    public async Task<PlantNetEmbeddingsResult> GetEmbeddingsAsync(PlantNetImageUpload image,
        CancellationToken cancellationToken = default)
    {
        ValidateUpload(image);
        using var content = new MultipartFormDataContent();
        AddImage(content, image, "image");
        return await SendAsync<PlantNetEmbeddingsResult>(HttpMethod.Post, "v2/embeddings", [], cancellationToken, content);
    }

    private async Task<T> UploadAsync<T>(string path, IReadOnlyList<PlantNetImageUpload> images,
        PlantNetIdentificationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ValidateImages(images.Count, options?.Organs);
        foreach (var image in images) ValidateUpload(image);
        var query = IdentificationQuery(options, plant: path.StartsWith("v2/identify/", StringComparison.Ordinal));
        using var content = new MultipartFormDataContent();
        foreach (var image in images) AddImage(content, image, "images");
        foreach (var organ in options?.Organs ?? []) content.Add(new StringContent(organ), "organs");
        return await SendAsync<T>(HttpMethod.Post, path, query, cancellationToken, content);
    }

    private static void AddImage(MultipartFormDataContent content, PlantNetImageUpload image, string field)
    {
        var file = new ByteArrayContent(image.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        content.Add(file, field, image.FileName);
    }

    private static void ValidateUpload(PlantNetImageUpload image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(image.Content);
        ArgumentException.ThrowIfNullOrWhiteSpace(image.FileName);
        if (image.Content.Length == 0) throw new ArgumentException("Image content must not be empty.", nameof(image));
        if (image.ContentType is not ("image/jpeg" or "image/png"))
            throw new ArgumentException("Image content type must be image/jpeg or image/png.", nameof(image));
    }

    private static void ValidateImages(int count, IReadOnlyList<string>? organs)
    {
        if (count is < 1 or > 5) throw new ArgumentException("Supply between one and five images.");
        if (organs is not null && (organs.Count != count || organs.Any(organ => !ValidOrgans.Contains(organ))))
            throw new ArgumentException("Supply one valid organ per image, or omit organs to use auto.", nameof(organs));
    }

    private static string ProjectSegment(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (project is "." or "..") throw new ArgumentException("Invalid project ID.", nameof(project));
        return Uri.EscapeDataString(project);
    }

    private static void ValidateType(string? type)
    {
        if (type is not (null or "kt" or "legacy"))
            throw new ArgumentException("Type must be kt or legacy.", nameof(type));
    }

    private static List<(string Name, object? Value)> SpeciesQuery(PlantNetSpeciesOptions? options)
    {
        if (options is null) return [];
        if ((options.Page is null) != (options.PageSize is null) ||
            (options.Page == "") != (options.PageSize == ""))
            throw new ArgumentException("Set Page and PageSize together; use two empty strings to disable pagination.", nameof(options));
        return [("lang", options.Language), ("page", options.Page), ("pageSize", options.PageSize),
            ("prefix", options.Prefix), ("images", options.Images)];
    }

    private static List<(string Name, object? Value)> IdentificationQuery(PlantNetIdentificationOptions? options,
        bool plant = true)
    {
        if (options is null) return [];
        if (options.NumberOfResults is < 1) throw new ArgumentOutOfRangeException(nameof(options.NumberOfResults));
        List<(string Name, object? Value)> query = [("lang", options.Language),
            ("include-related-images", options.IncludeRelatedImages), ("no-reject", options.NoReject),
            ("nb-results", options.NumberOfResults)];
        if (plant && options is PlantNetPlantIdentificationOptions plantOptions)
        {
            ValidateType(plantOptions.Type);
            query.Add(("type", plantOptions.Type));
            query.Add(("detailed", plantOptions.Detailed));
        }
        return query;
    }

    private Task<T> GetAsync<T>(string path, List<(string Name, object? Value)> query,
        CancellationToken cancellationToken) => SendAsync<T>(HttpMethod.Get, path, query, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, List<(string Name, object? Value)> query,
        CancellationToken cancellationToken, HttpContent? content = null, bool authenticated = true)
    {
        if (authenticated)
        {
            var key = options.Value.ApiKey;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Configure PlantNet:ApiKey before calling the Pl@ntNet API.");
            query.Add(("api-key", key));
        }
        var encoded = query.Where(pair => pair.Value is not null).Select(pair =>
            $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(FormatValue(pair.Value!))}");
        var queryString = string.Join("&", encoded);
        using var request = new HttpRequestMessage(method, queryString.Length == 0 ? path : $"{path}?{queryString}")
        {
            Content = content
        };
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // Do not include the request URI or upstream response body: either could expose the key.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Pl@ntNet returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new JsonException("Pl@ntNet returned an empty JSON response.");
    }

    private static string FormatValue(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!
    };
}
