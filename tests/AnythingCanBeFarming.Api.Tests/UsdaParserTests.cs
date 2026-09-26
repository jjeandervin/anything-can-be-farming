using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.DataImport;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class UsdaParserTests
{
    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "usda");
    public static string SeedDirectory => UsdaSeeds.DefaultDirectory(WfoSource.FindRepositoryRoot(AppContext.BaseDirectory));

    [Fact]
    public void Meta_xml_of_the_fixture_archive_validates_and_lists_ignored_template_tables()
    {
        var archive = UsdaArchive.Open(FixtureDirectory);
        Assert.Equal(["taxon.tab", "occurrence_specific.tab", "measurement_or_fact_specific.tab"], archive.Files.Select(x => Path.GetFileName(x.Path)));
        Assert.Equal(3, archive.Ignored.Count);
        Assert.Contains(archive.Ignored, x => x.StartsWith("agent.tab") && x.Contains("EOL template rows"));
        Assert.Equal(["taxonID", "source", "scientificName", "family", "taxonRank", "taxonomicStatus"], UsdaArchive.Taxon.Headers);
    }

    [Fact]
    public void Meta_xml_with_reordered_columns_or_a_missing_file_fails_clearly()
    {
        using var directory = CopyFixture();
        var meta = Path.Combine(directory.Path, "meta.xml");
        var original = File.ReadAllText(meta);
        File.WriteAllText(meta, original
            .Replace("index=\"2\" term=\"http://rs.tdwg.org/dwc/terms/scientificName\"", "index=\"2\" term=\"PLACEHOLDER\"")
            .Replace("index=\"3\" term=\"http://rs.tdwg.org/dwc/terms/family\"", "index=\"2\" term=\"http://rs.tdwg.org/dwc/terms/family\"")
            .Replace("index=\"2\" term=\"PLACEHOLDER\"", "index=\"3\" term=\"http://rs.tdwg.org/dwc/terms/scientificName\""));
        Assert.Contains("columns differ", Assert.Throws<InvalidDataException>(() => UsdaArchive.Open(directory.Path)).Message);

        File.WriteAllText(meta, original);
        File.Delete(Path.Combine(directory.Path, "occurrence_specific.tab"));
        Assert.Contains("occurrence_specific.tab", Assert.Throws<FileNotFoundException>(() => UsdaArchive.Open(directory.Path)).Message);

        File.Delete(meta);
        Assert.Throws<FileNotFoundException>(() => UsdaArchive.Open(directory.Path));
        Assert.Throws<FileNotFoundException>(() => UsdaArchive.Discover(directory.Path));
    }

    [Fact]
    public void Discovery_finds_meta_xml_in_the_directory_or_its_only_subdirectory()
    {
        using var directory = CopyFixture(Path.Combine("nested", "usda_plant_traits"));
        var root = Path.GetDirectoryName(Path.GetDirectoryName(directory.Path))!;
        Assert.Equal(directory.Path, UsdaArchive.Discover(root));
        Assert.Equal(directory.Path, UsdaArchive.Discover(directory.Path));
    }

    [Fact]
    public void Tsv_rows_keep_literal_backslash_n_and_quotes_and_report_bad_column_counts()
    {
        var header = string.Join('\t', UsdaArchive.Fact.Headers);
        var good = new string[18];
        Array.Fill(good, "");
        good[1] = "O1";
        good[5] = "http://eol.org/schema/terms/BloomPeriod";
        good[6] = "http://eol.org/schema/terms/midSpring";
        good[13] = "Source term: Bloom Period. Values are\\napproximations, \"quoted\" as written.";
        var malformed = new List<(long, string)>();
        var rows = UsdaTsvReader.Read(new StringReader($"{header}\n{string.Join('\t', good)}\ntoo\tfew\n"), UsdaArchive.Fact,
            (row, message) => malformed.Add((row, message))).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(2, row.SourceRow);
        Assert.Equal(good[13], row.Fields[13]);
        Assert.Contains("\\n", row.Fields[13]);
        Assert.Equal((3L, "Expected 18 columns, found 2."), Assert.Single(malformed));
        Assert.Equal("Bloom Period", UsdaValues.SourceTerm(row.Fields[13]));

        Assert.Throws<InvalidDataException>(() => UsdaTsvReader.Read(new StringReader("occurrenceID\ttaxonID\n"), UsdaArchive.Fact, (_, _) => { }).ToList());
    }

    [Theory]
    [InlineData("Source term: Growth Habit. Some plants…", "Growth Habit")]
    [InlineData("Source term: Shape and Orientation.", "Shape and Orientation")]
    [InlineData("Some plants have different Durations.", null)]
    [InlineData(null, null)]
    public void Source_term_is_parsed_from_the_start_of_remarks(string? remarks, string? expected) =>
        Assert.Equal(expected, UsdaValues.SourceTerm(remarks));

    [Theory]
    [InlineData("100", "100")]
    [InlineData("-47", "-47")]
    [InlineData("8.199999999999999", "8.199999999999999")]
    [InlineData("1.5e3", "1500")]
    [InlineData("http://eol.org/schema/terms/midSpring", null)]
    [InlineData("1,5", null)]
    [InlineData("", null)]
    public void Numbers_parse_with_the_invariant_culture(string value, string? expected) =>
        Assert.Equal(expected == null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), UsdaValues.Number(value));

    [Theory]
    [InlineData("http://www.geonames.org/5165418", "geonames", "5165418")]
    [InlineData("5165418", "geonames", "5165418")]
    [InlineData("http://www.wikidata.org/entity/Q578170", "wikidata", "Q578170")]
    [InlineData("https://www.wikidata.org/entity/Q797", "wikidata", "Q797")]
    [InlineData("Q578170", "wikidata", "Q578170")]
    [InlineData("Pacific Basin excluding Hawaii", "literal", null)]
    [InlineData("http://example.org/places/Q0", "literal", null)]
    public void Places_are_classified_by_scheme(string raw, string scheme, string? id) =>
        Assert.Equal(new PlaceReference(scheme, id), UsdaValues.ClassifyPlace(raw));

    [Theory]
    [InlineData("http://purl.obolibrary.org/obo/PATO_0000320", "PATO_0000320")]
    [InlineData("http://ncicb.nci.nih.gov/xml/owl/EVS/Thesaurus.owl#C94731", "C94731")]
    [InlineData("http://eol.org/schema/terms/lateSpring", "lateSpring")]
    [InlineData("Puerto Rico", "Puerto Rico")]
    [InlineData("100", "100")]
    public void Value_codes_are_the_last_uri_segment_or_the_literal(string raw, string code) => Assert.Equal(code, UsdaCode.FromValue(raw));

    [Theory]
    [InlineData("Acer saccharum Marshall", "species", "Acer saccharum")]
    [InlineData("Abies grandis (Douglas ex D. Don) Lindl.", "species", "Abies grandis")]
    [InlineData("Acer saccharum Marshall var. nigrum (F. Michx.) Britton", "variety", "Acer saccharum var. nigrum")]
    [InlineData("Quercus ×bebbiana C.K. Schneid.", "species", "Quercus × bebbiana")]
    [InlineData("Quercus x bebbiana C.K. Schneid.", "species", "Quercus × bebbiana")]
    [InlineData("Acer L.", "genus", "Acer")]
    [InlineData("×Agropogon Fourn.", "genus", "× Agropogon")]
    [InlineData("×Agropogon littoralis (Sm.) C.E. Hubbard [Agrostis stolonifera × Polypogon monospeliensis]", "species", "× Agropogon littoralis")]
    [InlineData("Aconitum uncinatum L. ssp. uncinatum", "subspecies", "Aconitum uncinatum subsp. uncinatum")]
    [InlineData("Senna artemisioides (Gaudich. ex DC.) Randell ssp. ×coriacea (Benth.) Randell [? × cardiosperma]", "subspecies", "Senna artemisioides subsp. coriacea")]
    [InlineData("Claytonia virginica L. var. virginica f. lutea R.J. Davis", "form", "Claytonia virginica f. lutea")]
    [InlineData("Commelina diffusa Burm. f. var. diffusa", "variety", "Commelina diffusa var. diffusa")]
    [InlineData("Cyanea st.-johnii (Hosaka) Lammers, Givnish & Systma", "species", "Cyanea st-johnii")]
    [InlineData("Echinocereus ×roetteri (Englem.) Rumpler ×roetteri", "variety", null)]
    [InlineData("Acer saccharum Marshall", "section", null)]
    [InlineData("acer saccharum", "species", null)]
    [InlineData("Acer", "species", null)]
    public void Canonical_names_drop_authorship_and_follow_wfo_spelling(string name, string rank, string? expected) =>
        Assert.Equal(expected, UsdaValues.CanonicalName(name, rank));

    [Fact]
    public void Committed_seed_files_load_and_are_consistent()
    {
        var seeds = UsdaSeeds.Load(SeedDirectory);
        Assert.Equal(61, seeds.TraitTypes.Count);
        foreach (var key in new[] { "duration", "growth_habit", "shade_tolerance", "min_temperature_f", "height_ft", "flower_color", "soil_ph" })
            Assert.Contains(seeds.TraitTypes, x => x.Key == key);
        // Every verified (and every other) code is unique per (code, type_uri); the loader also enforces this.
        Assert.Equal(seeds.CodeLabels.Count, seeds.CodeLabels.Select(x => (x.Code, x.TypeUri)).Distinct().Count());
        Assert.All(seeds.CodeLabels.Where(x => x.Confidence == "verified"), x => Assert.False(string.IsNullOrWhiteSpace(x.Evidence)));
        // Shade tolerance stays unresolved until a human decides its direction (spec 2.2.1).
        var shade = seeds.CodeLabels.Where(x => x.TypeUri == "http://eol.org/schema/terms/ShadeTolerance").ToList();
        Assert.Equal(["PATO_0000461", "PATO_0002393", "PATO_0002394"], shade.Select(x => x.Code).Order());
        Assert.All(shade, x => Assert.Equal((null, "unresolved"), (x.Label, x.Confidence)));
        Assert.Contains(seeds.CodeLabels, x => x is { Code: "PATO_0002394", TypeUri: null, Label: "Low (tolerance)", Confidence: "verified" });
        Assert.Contains(seeds.PlaceLabels, x => x is { Scheme: "geonames", PlaceId: "5165418", Name: "Ohio", CountryCode: "US", AdminCode: "OH" });
        Assert.Contains(seeds.PlaceLabels, x => x is { Scheme: "wikidata", PlaceId: "Q578170", Name: "Contiguous United States" });
        Assert.Equal(51, seeds.PlaceLabels.Count(x => x is { Kind: "state", CountryCode: "US", Scheme: "geonames" }));
    }

    [Fact]
    public void Committed_seeds_cover_every_measurement_type_in_the_fixture_archive()
    {
        var seeds = UsdaSeeds.Load(SeedDirectory);
        var types = File.ReadLines(Path.Combine(FixtureDirectory, "measurement_or_fact_specific.tab")).Skip(1)
            .Select(x => x.Split('\t')[5]).Where(x => UsdaValues.DistributionKind(x) == null).ToHashSet();
        Assert.Empty(types.Except(seeds.TraitTypes.Select(x => x.TypeUri)));
    }

    [Theory]
    [InlineData("usda-code-labels.csv", "C54722,,Low,1,verified,x", "duplicate code C54722")]
    [InlineData("usda-code-labels.csv", "newCode,,,,inferred,x", "a label is required")]
    [InlineData("usda-code-labels.csv", "newCode,,Label,,guessed,x", "confidence must be one of")]
    [InlineData("usda-code-labels.csv", "newCode,http://example.org/NotATrait,Label,,inferred,x", "is not a trait type")]
    [InlineData("usda-code-labels.csv", "newCode,,Label,first,inferred,x", "ordinal must be a whole number")]
    [InlineData("usda-trait-types.csv", "http://example.org/Other,duration,Other,coded,", "duplicate key duration")]
    [InlineData("usda-trait-types.csv", "http://example.org/Other,other,Other,fuzzy,", "value_kind must be one of")]
    [InlineData("usda-trait-types.csv", "http://eol.org/schema/terms/Present,present,Present,literal,", "distribution types are not traits")]
    [InlineData("place-labels.csv", "geonames,5165418,Ohio again,state,US,OH", "duplicate place geonames:5165418")]
    public void Invalid_seed_rows_are_rejected_with_file_and_line(string file, string row, string message)
    {
        using var directory = new TemporaryDirectory();
        foreach (var name in new[] { UsdaSeeds.TraitTypesFile, UsdaSeeds.CodeLabelsFile, UsdaSeeds.PlaceLabelsFile })
            File.Copy(Path.Combine(SeedDirectory, name), Path.Combine(directory.Path, name));
        File.AppendAllText(Path.Combine(directory.Path, file), row + "\n");
        var error = Assert.Throws<InvalidDataException>(() => UsdaSeeds.Load(directory.Path));
        Assert.Contains(message, error.Message);
        Assert.Contains(file + ":", error.Message);
    }

    internal static TemporaryDirectory CopyFixture(string? subdirectory = null)
    {
        var directory = new TemporaryDirectory(subdirectory);
        foreach (var file in Directory.EnumerateFiles(FixtureDirectory))
            File.Copy(file, Path.Combine(directory.Path, Path.GetFileName(file)));
        return directory;
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "acbf-usda-test-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory(string? subdirectory = null)
        {
            Path = subdirectory == null ? root : System.IO.Path.Combine(root, subdirectory);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
