using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace AnythingCanBeFarming.DataImport;

public sealed record UsdaVerifyOptions(int Sample = 20, int Seed = 42, IReadOnlyList<string>? Symbols = null);

public enum UsdaFieldMode { Label, Flag, Set, Shade, Min, Max, Only, Statistic }

// How one USDA characteristic (or profile list) compares to one trait key. A field can have several mappings;
// statistic-filtered ones are tallied separately as "key [statistic]".
public sealed record UsdaFieldMapping(string UsdaName, string Key, UsdaFieldMode Mode, string? Statistic = null,
    IReadOnlyDictionary<string, string>? Translate = null)
{
    public string TallyKey => Statistic == null ? Key : $"{Key} [{Statistic[(Statistic.LastIndexOf('/') + 1)..]}]";
}

public sealed class UsdaTraitTally
{
    public long Agree { get; set; }
    public long Disagree { get; set; }
    public long MissingInArchive { get; set; }
    public long Unmapped { get; set; }
    public long Unresolved { get; set; }
}

public sealed class UsdaCodeTally
{
    public required string Code { get; init; }
    public string? TypeUri { get; init; }
    public string? Label { get; init; }
    public string? Confidence { get; init; }
    public long Agree { get; set; }
    public long Disagree { get; set; }
    public SortedSet<string> AgreeingTaxa { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> AgreeingKeys { get; } = new(StringComparer.Ordinal);
}

public sealed record UsdaDisagreement(string Symbol, string Key, string Field, string Ours, string Usda, string? Codes, string? Confidence);
public sealed record UsdaUnmappedObservation(string Key, string Code, string UsdaValue, long Count);
public sealed record UsdaShadeObservation(string Symbol, string Code, string UsdaValue, string DirectLabel, string InvertedLabel);

public sealed class UsdaShadeReport
{
    public long Compared { get; set; }
    public long DirectAgreements { get; set; }
    public long InvertedAgreements { get; set; }
    public List<UsdaShadeObservation> Observations { get; } = [];
}

public sealed class UsdaVerifyReport
{
    public List<string> Symbols { get; } = [];
    public List<string> NotFoundAtUsda { get; } = [];
    public List<string> WithoutCharacteristics { get; } = [];
    public SortedDictionary<string, UsdaTraitTally> Traits { get; } = new(StringComparer.Ordinal);
    public List<UsdaDisagreement> Disagreements { get; } = [];
    public List<UsdaDisagreement> NumericDisagreements { get; } = [];
    public List<UsdaCodeTally> Codes { get; set; } = [];
    public List<string> ConfirmedInferredCodes { get; } = [];
    public List<string> VerifiedCodesWithDisagreements { get; } = [];
    public long HighToleranceTaxa { get; set; }
    public bool HighToleranceConfirmed { get; set; }
    public UsdaShadeReport Shade { get; } = new();
    public List<UsdaUnmappedObservation> UnmappedObservations { get; set; } = [];
    public SortedSet<string> UnmappedUsdaFields { get; } = new(StringComparer.Ordinal);
    public bool Passed { get; set; }
}

// Section 5: compares the imported labels with USDA's live labeled characteristics for a deterministic sample.
// Exits nonzero only when a verified code disagrees; shade tolerance is reported under both hypotheses and never fails.
public sealed partial class UsdaVerifier(string connectionString, UsdaPlantsClient plants, TextWriter output)
{
    public static readonly string[] AlwaysIncluded = ["ACSA3", "COFL2", "RUHI2", "ECPU"];
    public const string HighToleranceCode = "PATO_0002393";
    public const int HighToleranceTaxaRequired = 3;
    // Non-shade tolerance traits where PATO_0002393 can be confirmed as High.
    public static readonly string[] ToleranceKeys =
        ["fire_tolerance", "drought_tolerance", "caco3_tolerance", "anaerobic_tolerance", "salinity_tolerance", "hedge_tolerance"];
    private const string Maximum = "http://semanticscience.org/resource/SIO_001114";
    private const string Median = "http://semanticscience.org/resource/SIO_001110";
    private static readonly Dictionary<string, string> YesEvergreen = new(StringComparer.OrdinalIgnoreCase) { ["Yes"] = "Evergreen", ["No"] = "Deciduous" };

    public static IReadOnlyList<UsdaFieldMapping> FieldMap { get; } =
    [
        new("Active Growth Period", "active_growth_period", UsdaFieldMode.Label),
        new("After Harvest Regrowth Rate", "after_harvest_regrowth_rate", UsdaFieldMode.Label),
        new("Bloat", "bloat", UsdaFieldMode.Label),
        new("C:N Ratio", "cn_ratio", UsdaFieldMode.Label),
        new("Coppice Potential", "horticulture_flags", UsdaFieldMode.Flag),
        new("Fall Conspicuous", "horticulture_flags", UsdaFieldMode.Flag),
        new("Fire Resistant", "fire_resistant", UsdaFieldMode.Flag),
        new("Flower Color", "flower_color", UsdaFieldMode.Label),
        new("Flower Conspicuous", "horticulture_flags", UsdaFieldMode.Flag),
        new("Foliage Color", "foliage_color", UsdaFieldMode.Label),
        new("Foliage Porosity Summer", "foliage_porosity_summer", UsdaFieldMode.Label),
        new("Foliage Porosity Winter", "foliage_porosity_winter", UsdaFieldMode.Label),
        new("Foliage Texture", "foliage_texture", UsdaFieldMode.Label),
        new("Fruit/Seed Color", "fruit_seed_color", UsdaFieldMode.Label),
        new("Fruit/Seed Conspicuous", "horticulture_flags", UsdaFieldMode.Flag),
        new("Growth Form", "growth_form", UsdaFieldMode.Label),
        new("Growth Rate", "growth_rate", UsdaFieldMode.Label),
        new("Height at 20 Years, Maximum (feet)", "height_ft", UsdaFieldMode.Statistic, Maximum),
        // The spec's rule (mature height is the larger value), and the rule by statistical method.
        new("Height, Mature (feet)", "height_ft", UsdaFieldMode.Max),
        new("Height, Mature (feet)", "height_ft", UsdaFieldMode.Statistic, Median),
        new("Known Allelopath", "known_allelopath", UsdaFieldMode.Flag),
        new("Leaf Retention", "leaf_retention", UsdaFieldMode.Label, Translate: YesEvergreen),
        new("Lifespan", "lifespan", UsdaFieldMode.Label),
        new("Low Growing Grass", "low_growing_grass", UsdaFieldMode.Flag),
        new("Nitrogen Fixation", "nitrogen_fixation", UsdaFieldMode.Label),
        new("Resprout Ability", "resprout_ability", UsdaFieldMode.Flag),
        new("Shape and Orientation", "shape_orientation", UsdaFieldMode.Label),
        new("Toxicity", "toxicity", UsdaFieldMode.Label),
        new("Adapted to Coarse Textured Soils", "soil_texture_flags", UsdaFieldMode.Flag),
        new("Adapted to Fine Textured Soils", "soil_texture_flags", UsdaFieldMode.Flag),
        new("Adapted to Medium Textured Soils", "soil_texture_flags", UsdaFieldMode.Flag),
        new("Anaerobic Tolerance", "anaerobic_tolerance", UsdaFieldMode.Label),
        new("CaCO3 Tolerance", "caco3_tolerance", UsdaFieldMode.Label),
        new("Cold Stratification Required", "cold_stratification_required", UsdaFieldMode.Flag),
        new("Drought Tolerance", "drought_tolerance", UsdaFieldMode.Label),
        new("Fertility Requirement", "fertility_requirement", UsdaFieldMode.Label),
        new("Fire Tolerance", "fire_tolerance", UsdaFieldMode.Label),
        new("Frost Free Days, Minimum", "frost_free_days_min", UsdaFieldMode.Only),
        new("Hedge Tolerance", "hedge_tolerance", UsdaFieldMode.Label),
        new("Moisture Use", "moisture_use", UsdaFieldMode.Label),
        new("pH, Maximum", "soil_ph", UsdaFieldMode.Max),
        new("pH, Minimum", "soil_ph", UsdaFieldMode.Min),
        new("Planting Density per Acre, Maximum", "planting_density_per_acre", UsdaFieldMode.Max),
        new("Planting Density per Acre, Minimum", "planting_density_per_acre", UsdaFieldMode.Min),
        new("Precipitation, Maximum", "precipitation_in", UsdaFieldMode.Max),
        new("Precipitation, Minimum", "precipitation_in", UsdaFieldMode.Min),
        new("Root Depth, Minimum (inches)", "root_depth_min_in", UsdaFieldMode.Only),
        new("Salinity Tolerance", "salinity_tolerance", UsdaFieldMode.Label),
        new("Shade Tolerance", "shade_tolerance", UsdaFieldMode.Shade),
        new("Temperature, Minimum (°F)", "min_temperature_f", UsdaFieldMode.Only),
        new("Bloom Period", "bloom_period", UsdaFieldMode.Label),
        new("Commercial Availability", "commercial_availability", UsdaFieldMode.Label),
        new("Fruit/Seed Abundance", "fruit_seed_abundance", UsdaFieldMode.Label),
        new("Fruit/Seed Period Begin", "fruit_seed_period_begin", UsdaFieldMode.Label),
        new("Fruit/Seed Period End", "fruit_seed_period_end", UsdaFieldMode.Label),
        new("Fruit/Seed Persistence", "fruit_seed_persistence", UsdaFieldMode.Flag),
        new("Propagated by Bare Root", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Bulb", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Container", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Corm", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Cuttings", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Seed", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Sod", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Sprigs", "propagation_flags", UsdaFieldMode.Flag),
        new("Propagated by Tubers", "propagation_flags", UsdaFieldMode.Flag),
        new("Seed per Pound", "seeds_per_pound", UsdaFieldMode.Only),
        new("Seed Spread Rate", "seed_spread_rate", UsdaFieldMode.Label),
        new("Seedling Vigor", "seedling_vigor", UsdaFieldMode.Label),
        new("Small Grain", "small_grain", UsdaFieldMode.Flag),
        new("Vegetative Spread Rate", "vegetative_spread_rate", UsdaFieldMode.Label),
        new("Fuelwood Product", "fuelwood_product", UsdaFieldMode.Label),
        new("Palatable Browse Animal", "browse_palatability", UsdaFieldMode.Label),
        new("Palatable Graze Animal", "graze_palatability", UsdaFieldMode.Label),
        new("Protein Potential", "protein_potential", UsdaFieldMode.Label)
    ];

    // From the plant profile rather than the characteristics.
    public static readonly UsdaFieldMapping Durations = new("Durations", "duration", UsdaFieldMode.Set);
    public static readonly UsdaFieldMapping GrowthHabits = new("GrowthHabits", "growth_habit", UsdaFieldMode.Set);

    public sealed record Fact(string Key, string Code, string? Label, string? Confidence, string? LabelTypeUri, decimal? Numeric, string? Statistic);

    public async Task<UsdaVerifyReport> VerifyAsync(UsdaVerifyOptions options, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var report = new UsdaVerifyReport();
        report.Symbols.AddRange(await SampleAsync(connection, options, cancellationToken));
        var facts = await FactsAsync(connection, report.Symbols, cancellationToken);
        var toleranceLabels = await ToleranceLabelsAsync(connection, cancellationToken);
        var comparison = new Comparison(report, toleranceLabels);
        output.WriteLine($"Verifying {report.Symbols.Count} symbols against USDA PLANTS (one request at a time)…");
        foreach (var symbol in report.Symbols)
        {
            var profile = await plants.GetProfileAsync(symbol, cancellationToken);
            if (profile == null)
            {
                report.NotFoundAtUsda.Add(symbol);
                continue;
            }
            var ours = facts.GetValueOrDefault(symbol) ?? [];
            comparison.Compare(symbol, Durations, string.Join('|', profile.Durations), ours, profile.Durations);
            comparison.Compare(symbol, GrowthHabits, string.Join('|', profile.GrowthHabits), ours, profile.GrowthHabits);
            if (!profile.HasCharacteristics)
            {
                report.WithoutCharacteristics.Add(symbol);
                continue;
            }
            foreach (var characteristic in await plants.GetCharacteristicsAsync(profile.Id, cancellationToken))
            {
                var mappings = FieldMap.Where(x => x.UsdaName.Equals(characteristic.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mappings.Count == 0) report.UnmappedUsdaFields.Add(characteristic.Name);
                foreach (var mapping in mappings) comparison.Compare(symbol, mapping, characteristic.Value, ours, null);
            }
        }
        comparison.Finish();
        Print(report);
        return report;
    }

    private static async Task<List<string>> SampleAsync(NpgsqlConnection connection, UsdaVerifyOptions options, CancellationToken cancellationToken)
    {
        var chosen = new List<string>(AlwaysIncluded);
        if (options.Symbols is { Count: > 0 } symbols)
            chosen.AddRange(symbols.Select(x => x.ToUpperInvariant()));
        else
        {
            var candidates = new List<string>();
            await using var command = new NpgsqlCommand("""
                SELECT DISTINCT f."Symbol" FROM reference.usda_fact f JOIN reference.usda_trait_type t ON t."TypeUri" = f."TypeUri"
                WHERE t."Key" IN ('shade_tolerance', 'min_temperature_f') ORDER BY 1
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add(reader.GetString(0));
            if (candidates.Count == 0) throw new InvalidOperationException("No USDA taxa with characteristics are imported. Run `usda` first.");
            // Fisher-Yates over the symbol-ordered list: the same seed and data give the same sample.
            var random = new Random(options.Seed);
            for (var i = candidates.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            chosen.AddRange(candidates.Take(options.Sample));
        }
        return chosen.Distinct(StringComparer.Ordinal).ToList();
    }

    private static async Task<Dictionary<string, List<Fact>>> FactsAsync(NpgsqlConnection connection, List<string> symbols, CancellationToken cancellationToken)
    {
        var facts = new Dictionary<string, List<Fact>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT symbol, trait_key, value_code, value_label, label_confidence, label_type_uri, value_numeric, statistical_method
            FROM reference.usda_fact_labeled WHERE symbol = ANY(@symbols) AND trait_key IS NOT NULL ORDER BY fact_id
            """, connection);
        command.Parameters.AddWithValue("symbols", symbols.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var symbol = reader.GetString(0);
            if (!facts.TryGetValue(symbol, out var list)) facts[symbol] = list = [];
            list.Add(new(reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6), Text(reader, 7)));
        }
        return facts;
    }

    // Generic labels of the three tolerance codes, used to score both shade hypotheses.
    private static async Task<Dictionary<string, string>> ToleranceLabelsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT "Code", "Label" FROM reference.usda_code_label
            WHERE "TypeUri" IS NULL AND "Label" IS NOT NULL AND "Code" IN ('PATO_0002394', 'PATO_0000461', 'PATO_0002393')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) labels[reader.GetString(0)] = Comparable(reader.GetString(1));
        return labels;
    }

    // Our labels may carry a trailing qualifier, e.g. "Low (tolerance)"; USDA shows "Low".
    public static string Comparable(string label) => QualifierPattern().Replace(label, "").Trim();

    public sealed class Comparison(UsdaVerifyReport report, IReadOnlyDictionary<string, string> toleranceLabels)
    {
        private readonly Dictionary<(string Code, string? TypeUri), UsdaCodeTally> codes = [];
        private readonly Dictionary<(string Key, string Code, string Value), long> unmapped = [];

        public void Compare(string symbol, UsdaFieldMapping mapping, string usdaValue, IReadOnlyList<Fact> facts, string[]? usdaSet)
        {
            var tally = Tally(mapping.TallyKey);
            var relevant = facts.Where(x => x.Key == mapping.Key).ToList();
            // USDA leaves duration and habit empty for some taxa (e.g. genera); there is nothing to compare.
            if (mapping.Mode == UsdaFieldMode.Set && usdaSet!.Length == 0) return;
            if (relevant.Count == 0)
            {
                tally.MissingInArchive++;
                return;
            }
            switch (mapping.Mode)
            {
                case UsdaFieldMode.Shade:
                    ScoreShade(symbol, relevant, usdaValue, tally);
                    return;
                case UsdaFieldMode.Min or UsdaFieldMode.Max or UsdaFieldMode.Only or UsdaFieldMode.Statistic:
                    CompareNumber(symbol, mapping, usdaValue, relevant, tally);
                    return;
            }
            if (relevant.FirstOrDefault(x => x.Label == null) is { } missing && mapping.Mode != UsdaFieldMode.Flag)
            {
                if (missing.Confidence == null)
                {
                    tally.Unmapped++;
                    var observation = (mapping.Key, missing.Code, usdaValue);
                    unmapped[observation] = unmapped.GetValueOrDefault(observation) + 1;
                }
                else tally.Unresolved++;
                return;
            }
            switch (mapping.Mode)
            {
                case UsdaFieldMode.Flag:
                {
                    var prefix = mapping.UsdaName + ":";
                    var match = relevant.Where(x => x.Label?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true).ToList();
                    if (match.Count == 0)
                    {
                        tally.MissingInArchive++;
                        return;
                    }
                    var expected = $"{mapping.UsdaName}: {usdaValue}";
                    var agree = match.Count == 1 && match[0].Label!.Equals(expected, StringComparison.OrdinalIgnoreCase);
                    Record(symbol, mapping, tally, agree, match, string.Join(" | ", match.Select(x => x.Label)), expected);
                    return;
                }
                case UsdaFieldMode.Set:
                {
                    var theirs = usdaSet!.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var ours = relevant.Select(x => Comparable(x.Label!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var agree = ours.SetEquals(theirs);
                    tally.Agree += agree ? 1 : 0;
                    tally.Disagree += agree ? 0 : 1;
                    foreach (var fact in relevant) CountCode(symbol, mapping.Key, fact, theirs.Contains(Comparable(fact.Label!)));
                    if (!agree) Disagree(symbol, mapping, relevant, string.Join(" | ", ours.Order()), string.Join(" | ", usdaSet!.Order()));
                    return;
                }
                default:
                {
                    var expected = mapping.Translate?.GetValueOrDefault(usdaValue) ?? usdaValue;
                    var ours = relevant.Select(x => Comparable(x.Label!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var agree = ours.Count == 1 && ours[0].Equals(expected, StringComparison.OrdinalIgnoreCase);
                    Record(symbol, mapping, tally, agree, relevant, string.Join(" | ", ours), usdaValue);
                    return;
                }
            }
        }

        private void Record(string symbol, UsdaFieldMapping mapping, UsdaTraitTally tally, bool agree, List<Fact> facts, string ours, string usda)
        {
            if (agree) tally.Agree++; else tally.Disagree++;
            foreach (var fact in facts) CountCode(symbol, mapping.Key, fact, agree);
            if (!agree) Disagree(symbol, mapping, facts, ours, usda);
        }

        private void Disagree(string symbol, UsdaFieldMapping mapping, List<Fact> facts, string ours, string usda) =>
            report.Disagreements.Add(new(symbol, mapping.Key, mapping.UsdaName, ours, usda, string.Join(",", facts.Select(x => x.Code)),
                facts.Any(x => x.Confidence == "verified") ? "verified" : facts.Select(x => x.Confidence).FirstOrDefault()));

        private void CountCode(string symbol, string key, Fact fact, bool agree)
        {
            var id = (fact.Code, fact.LabelTypeUri);
            if (!codes.TryGetValue(id, out var tally))
                codes[id] = tally = new() { Code = fact.Code, TypeUri = fact.LabelTypeUri, Label = fact.Label, Confidence = fact.Confidence };
            if (agree)
            {
                tally.Agree++;
                tally.AgreeingTaxa.Add(symbol);
                tally.AgreeingKeys.Add(key);
            }
            else tally.Disagree++;
        }

        private void CompareNumber(string symbol, UsdaFieldMapping mapping, string usdaValue, List<Fact> facts, UsdaTraitTally tally)
        {
            var values = facts.Where(x => x.Numeric != null && (mapping.Statistic == null || x.Statistic == mapping.Statistic))
                .Select(x => x.Numeric!.Value).ToList();
            decimal? ours = values.Count == 0 ? null : mapping.Mode switch
            {
                UsdaFieldMode.Min => values.Min(),
                UsdaFieldMode.Max => values.Max(),
                _ => values.Distinct().Count() == 1 ? values[0] : null
            };
            if (values.Count == 0)
            {
                tally.MissingInArchive++;
                return;
            }
            var theirs = UsdaValues.Number(usdaValue);
            var agree = ours != null && theirs != null && Math.Abs(ours.Value - theirs.Value) <= 0.000001m;
            if (agree) tally.Agree++;
            else
            {
                tally.Disagree++;
                report.NumericDisagreements.Add(new(symbol, mapping.TallyKey, mapping.UsdaName,
                    string.Join(" | ", values.Select(x => x.ToString(CultureInfo.InvariantCulture))), usdaValue, null, null));
            }
        }

        // Direct: a shade code means what it means in the other tolerance traits. Inverted: Low and High swap.
        private void ScoreShade(string symbol, List<Fact> facts, string usdaValue, UsdaTraitTally tally)
        {
            tally.Unresolved++;
            if (facts.Count != 1 || !toleranceLabels.TryGetValue(facts[0].Code, out var direct)) return;
            var inverted = direct.Equals("Low", StringComparison.OrdinalIgnoreCase) ? "High"
                : direct.Equals("High", StringComparison.OrdinalIgnoreCase) ? "Low" : direct;
            var shade = report.Shade;
            shade.Compared++;
            if (direct.Equals(usdaValue, StringComparison.OrdinalIgnoreCase)) shade.DirectAgreements++;
            if (inverted.Equals(usdaValue, StringComparison.OrdinalIgnoreCase)) shade.InvertedAgreements++;
            shade.Observations.Add(new(symbol, facts[0].Code, usdaValue, direct, inverted));
        }

        private UsdaTraitTally Tally(string key)
        {
            if (!report.Traits.TryGetValue(key, out var tally)) report.Traits[key] = tally = new();
            return tally;
        }

        public void Finish()
        {
            report.Codes = codes.Values.OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.TypeUri, StringComparer.Ordinal).ToList();
            foreach (var code in report.Codes)
            {
                if (code.Confidence == "verified" && code.Disagree > 0) report.VerifiedCodesWithDisagreements.Add($"{code.Code} ({code.Label})");
                if (code.Confidence == "inferred" && code.Agree > 0 && code.Disagree == 0)
                    report.ConfirmedInferredCodes.Add($"{code.Code} ({code.Label}): {code.AgreeingTaxa.Count} {(code.AgreeingTaxa.Count == 1 ? "taxon" : "taxa")}");
            }
            var high = codes.GetValueOrDefault((HighToleranceCode, null));
            report.HighToleranceTaxa = high?.AgreeingTaxa.Count ?? 0;
            report.HighToleranceConfirmed = high != null && high.Disagree == 0 && report.HighToleranceTaxa >= HighToleranceTaxaRequired
                && high.AgreeingKeys.All(ToleranceKeys.Contains);
            report.UnmappedObservations = unmapped.Select(x => new UsdaUnmappedObservation(x.Key.Key, x.Key.Code, x.Key.Value, x.Value))
                .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ThenByDescending(x => x.Count).ToList();
            report.Passed = report.VerifiedCodesWithDisagreements.Count == 0;
        }
    }

    private void Print(UsdaVerifyReport report)
    {
        output.WriteLine();
        output.WriteLine($"{"Trait key",-30} {"Agree",6} {"Disagree",9} {"Missing",8} {"Unmapped",9} {"Unresolved",11}");
        foreach (var (key, tally) in report.Traits)
            output.WriteLine($"{key,-30} {tally.Agree,6} {tally.Disagree,9} {tally.MissingInArchive,8} {tally.Unmapped,9} {tally.Unresolved,11}");
        if (report.NotFoundAtUsda.Count > 0) output.WriteLine($"Not found at USDA: {string.Join(", ", report.NotFoundAtUsda)}");
        if (report.WithoutCharacteristics.Count > 0) output.WriteLine($"No characteristics at USDA: {string.Join(", ", report.WithoutCharacteristics)}");
        output.WriteLine();
        output.WriteLine(report.Disagreements.Count == 0 ? "Label disagreements: none." : "Label disagreements:");
        foreach (var x in report.Disagreements)
            output.WriteLine($"  {x.Symbol} {x.Key}: ours \"{x.Ours}\" vs USDA {x.Field} \"{x.Usda}\" (codes {x.Codes}, {x.Confidence ?? "unmapped"})");
        output.WriteLine(report.NumericDisagreements.Count == 0 ? "Numeric disagreements: none." : "Numeric disagreements:");
        foreach (var x in report.NumericDisagreements)
            output.WriteLine($"  {x.Symbol} {x.Key}: ours {x.Ours} vs USDA {x.Field} {x.Usda}");
        output.WriteLine(report.ConfirmedInferredCodes.Count == 0 ? "Inferred codes confirmed: none." : "Inferred codes confirmed (candidates for promotion):");
        foreach (var code in report.ConfirmedInferredCodes) output.WriteLine($"  {code}");
        output.WriteLine($"{HighToleranceCode} = High (tolerance): {(report.HighToleranceConfirmed ? "CONFIRMED" : "not confirmed")} " +
            $"on {report.HighToleranceTaxa} taxa across non-shade tolerance traits (needs {HighToleranceTaxaRequired} and no disagreements).");
        var shade = report.Shade;
        output.WriteLine($"Shade tolerance (never fails the run): {shade.Compared} compared; direct hypothesis agrees on {shade.DirectAgreements}, " +
            $"inverted on {shade.InvertedAgreements}.");
        foreach (var x in shade.Observations)
            output.WriteLine($"  {x.Symbol}: {x.Code} → direct {x.DirectLabel}, inverted {x.InvertedLabel}; USDA says {x.UsdaValue}");
        if (report.UnmappedObservations.Count > 0) output.WriteLine("Unmapped codes seen next to USDA values:");
        foreach (var x in report.UnmappedObservations) output.WriteLine($"  {x.Key} {x.Code} ↔ \"{x.UsdaValue}\" ({x.Count})");
        if (report.UnmappedUsdaFields.Count > 0) output.WriteLine($"USDA fields with no mapping: {string.Join("; ", report.UnmappedUsdaFields)}");
        output.WriteLine(report.Passed ? "PASSED: no verified code disagreed." :
            $"FAILED: verified codes disagreed: {string.Join(", ", report.VerifiedCodesWithDisagreements)}");
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    [GeneratedRegex(@"\s*\([^)]*\)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex QualifierPattern();
}
