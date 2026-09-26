using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnythingCanBeFarming.DataImport;

namespace AnythingCanBeFarming.Api.Tests;

// No test contacts USDA; responses come from saved real responses under Fixtures/usda/api.
public sealed class UsdaVerifyTests
{
    private static string Api(string file) => File.ReadAllText(Path.Combine(UsdaParserTests.FixtureDirectory, "api", file));

    [Fact]
    public void Saved_responses_parse_into_profiles_and_characteristics()
    {
        using var profile = JsonDocument.Parse(Api("PlantProfile-ACSA3.json"));
        Assert.Equal(new UsdaPlantProfile(92865, "ACSA3", true, ["Perennial"], ["Shrub", "Tree"]), UsdaPlantsClient.ParseProfile(profile.RootElement),
            ProfileComparer);
        using var unknown = JsonDocument.Parse("""{"Id":0,"Symbol":null,"HasCharacteristics":false}""");
        Assert.Null(UsdaPlantsClient.ParseProfile(unknown.RootElement));

        var characteristics = JsonNode.Parse(Api("PlantCharacteristics-92865.json"))!.AsArray();
        characteristics.Add(new JsonObject { ["PlantCharacteristicName"] = "Flower Color", ["PlantCharacteristicValue"] = "Red", ["CultivarName"] = "'Red Sunset'" });
        using var document = JsonDocument.Parse(characteristics.ToJsonString());
        var parsed = UsdaPlantsClient.ParseCharacteristics(document.RootElement);
        Assert.Equal(80, parsed.Count);
        Assert.Contains(new UsdaCharacteristic("Temperature, Minimum (°F)", "-47", "Growth Requirements"), parsed);
        Assert.Single(parsed, x => x.Name == "Flower Color");
    }

    [Fact]
    public void Our_labels_compare_without_their_qualifier()
    {
        Assert.Equal("Low", UsdaVerifier.Comparable("Low (tolerance)"));
        Assert.Equal("Deciduous", UsdaVerifier.Comparable("Deciduous (leaf retention: No)"));
        Assert.Equal("Fire Resistant: No", UsdaVerifier.Comparable("Fire Resistant: No"));
    }

    [Fact]
    public async Task Symbols_are_validated_before_any_request()
    {
        var stub = new StubUsda();
        using var clients = stub.Clients();
        await Assert.ThrowsAsync<ArgumentException>(() => clients.Plants.GetProfileAsync("acsa3&x=1", default));
        Assert.Empty(stub.Requests);
    }

    [PostgresFact]
    public async Task Verify_agrees_with_the_saved_acsa3_response_and_reports_shade_under_both_hypotheses()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await UsdaPostgresTests.ImportAsync(database, UsdaParserTests.FixtureDirectory);
        var stub = new StubUsda();
        var report = await VerifyAsync(database, stub);

        Assert.True(report.Passed);
        Assert.Equal(UsdaVerifier.AlwaysIncluded, report.Symbols);
        Assert.Equal(["COFL2", "RUHI2", "ECPU"], report.NotFoundAtUsda);
        Assert.Empty(report.Disagreements);
        Assert.Empty(report.NumericDisagreements);
        foreach (var key in new[] { "flower_color", "fire_tolerance", "drought_tolerance", "leaf_retention", "bloom_period", "foliage_porosity_summer",
            "horticulture_flags", "propagation_flags", "duration", "growth_habit", "min_temperature_f", "soil_ph", "known_allelopath" })
        {
            Assert.True(report.Traits[key].Agree > 0, key);
            Assert.Equal(0, report.Traits[key].Disagree);
        }
        // Mature height: the spec's max rule, and the rule by statistical method; the 20-year height by method.
        Assert.Equal(1, report.Traits["height_ft"].Agree);
        Assert.Equal(1, report.Traits["height_ft [SIO_001110]"].Agree);
        Assert.Equal(1, report.Traits["height_ft [SIO_001114]"].Agree);
        Assert.Equal(1, report.Traits["nitrogen_fixation"].MissingInArchive); // USDA "None" is not exported.
        Assert.Equal((1L, 0L, 1L), (report.Shade.Compared, report.Shade.DirectAgreements, report.Shade.InvertedAgreements));
        Assert.Equal(new UsdaShadeObservation("ACSA3", "PATO_0002393", "Low", "High", "Low"), Assert.Single(report.Shade.Observations));
        Assert.Equal(1, report.Traits["shade_tolerance"].Unresolved);
        Assert.Contains("Veneer Product", report.UnmappedUsdaFields);
        Assert.Contains(report.Codes, x => x is { Code: "PATO_0000320", Confidence: "verified", Disagree: 0 } && x.Agree >= 2);

        // Politeness: every request identifies itself, one at a time, at least the configured interval apart.
        Assert.Equal(5, stub.Requests.Count);
        Assert.All(stub.Requests, x => Assert.Equal(WikidataOptions.DefaultUserAgent, x.UserAgent));
        Assert.Equal(stub.Requests.Count - 1, stub.Delays.Count);
        Assert.All(stub.Delays, x => Assert.InRange(x, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(500)));
    }

    [PostgresFact]
    public async Task A_verified_code_disagreement_fails_while_numeric_differences_are_only_reported()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await UsdaPostgresTests.ImportAsync(database, UsdaParserTests.FixtureDirectory);

        var numeric = await VerifyAsync(database, new StubUsda { Change = ("Temperature, Minimum (°F)", "-40") });
        Assert.True(numeric.Passed);
        Assert.Equal(new UsdaDisagreement("ACSA3", "min_temperature_f", "Temperature, Minimum (°F)", "-47", "-40", null, null),
            Assert.Single(numeric.NumericDisagreements));

        var mature = await VerifyAsync(database, new StubUsda { Change = ("Height, Mature (feet)", "90") });
        Assert.Equal(["height_ft", "height_ft [SIO_001110]"], mature.NumericDisagreements.Select(x => x.Key));
        Assert.True(mature.Passed);

        var color = await VerifyAsync(database, new StubUsda { Change = ("Flower Color", "Red") });
        Assert.False(color.Passed);
        Assert.Equal(["PATO_0000320 (Green)"], color.VerifiedCodesWithDisagreements);
        var disagreement = Assert.Single(color.Disagreements);
        Assert.Equal(("ACSA3", "flower_color", "Green", "Red", "verified"), (disagreement.Symbol, disagreement.Key, disagreement.Ours, disagreement.Usda, disagreement.Confidence));

        var flag = await VerifyAsync(database, new StubUsda { Change = ("Fall Conspicuous", "No") });
        Assert.Equal(["fallConspicuousYes (Fall Conspicuous: Yes)"], flag.VerifiedCodesWithDisagreements);
        Assert.Equal("Fall Conspicuous: No", Assert.Single(flag.Disagreements).Usda);
    }

    [PostgresFact]
    public async Task The_default_sample_is_deterministic_and_always_includes_the_reference_taxa()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await UsdaPostgresTests.ImportAsync(database, UsdaParserTests.FixtureDirectory);
        var first = await VerifyAsync(database, new StubUsda(), new UsdaVerifyOptions(Sample: 2, Seed: 5));
        var second = await VerifyAsync(database, new StubUsda(), new UsdaVerifyOptions(Sample: 2, Seed: 5));
        Assert.Equal(first.Symbols, second.Symbols);
        Assert.Equal(UsdaVerifier.AlwaysIncluded, first.Symbols); // The fixture's only taxa with characteristics are the four.
        var chosen = await VerifyAsync(database, new StubUsda(), new UsdaVerifyOptions(Symbols: ["acsas2", "ACSA3"]));
        Assert.Equal([.. UsdaVerifier.AlwaysIncluded, "ACSAS2"], chosen.Symbols);
    }

    private static async Task<UsdaVerifyReport> VerifyAsync(WfoPostgresTests.TestDatabase database, StubUsda stub, UsdaVerifyOptions? options = null)
    {
        using var clients = stub.Clients();
        return await new UsdaVerifier(database.ConnectionString, clients.Plants, TextWriter.Null).VerifyAsync(options ?? new UsdaVerifyOptions(Sample: 0), default);
    }

    private sealed class StubUsda
    {
        public List<(string Path, string? UserAgent)> Requests { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public (string Name, string Value)? Change { get; init; }

        public UsdaPlantsClients Clients() => new(new UsdaPlantsOptions(), new Handler(Send), (delay, _) =>
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        });

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((path, string.Join(" ", request.Headers.GetValues("User-Agent"))));
            var body = path switch
            {
                "/api/PlantProfile?symbol=ACSA3" => Api("PlantProfile-ACSA3.json"),
                _ when path.StartsWith("/api/PlantProfile?symbol=") => """{"Id":0,"Symbol":null,"HasCharacteristics":false}""",
                "/api/PlantCharacteristics/92865" => Characteristics(),
                _ => null
            };
            return body == null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private string Characteristics()
        {
            var array = JsonNode.Parse(Api("PlantCharacteristics-92865.json"))!.AsArray();
            if (Change is { } change)
                foreach (var item in array)
                    if ((string?)item!["PlantCharacteristicName"] == change.Name) item["PlantCharacteristicValue"] = change.Value;
            return array.ToJsonString();
        }

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(send(request));
        }
    }

    private static readonly IEqualityComparer<UsdaPlantProfile?> ProfileComparer = EqualityComparer<UsdaPlantProfile?>.Create(
        (a, b) => a!.Id == b!.Id && a.Symbol == b.Symbol && a.HasCharacteristics == b.HasCharacteristics &&
            a.Durations.SequenceEqual(b.Durations) && a.GrowthHabits.SequenceEqual(b.GrowthHabits), x => x!.Id.GetHashCode());
}
