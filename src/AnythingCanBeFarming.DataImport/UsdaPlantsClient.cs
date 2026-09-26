using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AnythingCanBeFarming.DataImport;

public sealed class UsdaPlantsOptions
{
    public string UserAgent { get; set; } = WikidataOptions.DefaultUserAgent;
    public Uri BaseAddress { get; set; } = new("https://plantsservices.sc.egov.usda.gov/");
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public static UsdaPlantsOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new UsdaPlantsOptions();
        var userAgent = configuration["Usda:UserAgent"] ?? configuration["Wikidata:UserAgent"];
        if (!string.IsNullOrWhiteSpace(userAgent)) options.UserAgent = userAgent;
        if (!string.IsNullOrWhiteSpace(configuration["Usda:PlantsServicesBaseAddress"]))
            options.BaseAddress = new Uri(configuration["Usda:PlantsServicesBaseAddress"]!);
        return options;
    }
}

public sealed class UsdaPlantsClients : IDisposable
{
    private readonly HttpClient http;

    public UsdaPlantsClients(UsdaPlantsOptions options, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        http = new HttpClient(handler ?? WikidataOptions.CreateHandler(), disposeHandler: handler == null)
            { BaseAddress = options.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        // Same politeness rules as Wikidata: one request at a time, a minimum gap, and bounded retries on 429/503.
        Plants = new UsdaPlantsClient(new WikidataHttp(http, new WikidataOptions { UserAgent = options.UserAgent },
            options.Interval, options.RequestTimeout, delay, service: "USDA PLANTS"));
    }

    public UsdaPlantsClient Plants { get; }

    public void Dispose() => http.Dispose();
}

public sealed record UsdaPlantProfile(long Id, string Symbol, bool HasCharacteristics, string[] Durations, string[] GrowthHabits);
public sealed record UsdaCharacteristic(string Name, string Value, string? Category);

// USDA's unofficial JSON backend for plants.usda.gov. A verification aid only; the import never depends on it.
// Shapes follow saved responses (tests/.../Fixtures/usda/api): PlantProfile returns Id 0 for an unknown symbol,
// and PlantCharacteristics returns an array of {PlantCharacteristicName, PlantCharacteristicValue, ...}.
public sealed partial class UsdaPlantsClient(WikidataHttp http)
{
    public async Task<UsdaPlantProfile?> GetProfileAsync(string symbol, CancellationToken cancellationToken)
    {
        if (!SymbolPattern().IsMatch(symbol)) throw new ArgumentException("USDA symbols are uppercase letters and digits.", nameof(symbol));
        using var document = await GetAsync($"api/PlantProfile?symbol={Uri.EscapeDataString(symbol)}", cancellationToken);
        return ParseProfile(document.RootElement);
    }

    public async Task<List<UsdaCharacteristic>> GetCharacteristicsAsync(long id, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"api/PlantCharacteristics/{id}", cancellationToken);
        return ParseCharacteristics(document.RootElement);
    }

    public static UsdaPlantProfile? ParseProfile(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("USDA PLANTS profile response is not an object.");
        var id = root.TryGetProperty("Id", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
        var symbol = String(root, "Symbol");
        if (id == 0 || symbol == null) return null;
        return new(id, symbol, root.TryGetProperty("HasCharacteristics", out var has) && has.ValueKind == JsonValueKind.True,
            Strings(root, "Durations"), Strings(root, "GrowthHabits"));
    }

    // Cultivar-specific rows describe a cultivar rather than the taxon and are skipped.
    public static List<UsdaCharacteristic> ParseCharacteristics(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("USDA PLANTS characteristics response is not an array.");
        return root.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object && String(x, "CultivarName") == null)
            .Select(x => (Name: String(x, "PlantCharacteristicName"), Value: String(x, "PlantCharacteristicValue"), Category: String(x, "PlantCharacteristicCategory")))
            .Where(x => x.Name != null && x.Value != null)
            .Select(x => new UsdaCharacteristic(x.Name!.Trim(), x.Value!.Trim(), x.Category))
            .ToList();
    }

    private Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken) =>
        http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), async (response, token) =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token);
        }, cancellationToken);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];

    [GeneratedRegex("^[A-Z0-9]{1,16}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}
