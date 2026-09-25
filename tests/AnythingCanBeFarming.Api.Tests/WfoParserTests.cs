using System.Globalization;
using AnythingCanBeFarming.DataImport;
using CsvHelper;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WfoParserTests
{
    [Fact]
    public void Parses_all_columns_and_preserves_quotes_tabs_newlines_unicode_and_long_text()
    {
        var row = Row("wfo-00001", "Schoenoxiphium ecklonii var. ecklonii");
        row[1] = "urn:lsid:ipni.org:names:310980-1";
        row[6] = "Nées";
        row[22] = "<a href=\"https://example.org/?a=1&b=2\">quoted \"text\"\twith tabs\nand lines</a>" + new string('x', 200000);
        row[23] = "2022-04-16";
        row[24] = "2025-06-17";
        var (rows, report, diagnostics) = Parse(Tsv(row));
        var result = Assert.Single(rows);
        Assert.Equal(2, result.SourceRow);
        Assert.Equal(29, result.Values.Length);
        for (var i = 0; i < 29; i++)
        {
            if (i is 23 or 24) Assert.IsType<DateOnly>(result.Values[i]);
            else Assert.Equal(row[i].Length == 0 ? null : row[i], result.Values[i]);
        }
        Assert.Equal(0, report.RowsRejected);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Invalid_dates_are_reported_with_source_row_and_do_not_reject_the_taxon()
    {
        var row = Row("wfo-1", "Acer");
        row[23] = "2025-02-30";
        row[24] = "unknown";
        var (rows, report, diagnostics) = Parse(Tsv(row));
        Assert.Null(Assert.Single(rows).Values[23]);
        Assert.Equal(2, report.WarningCount);
        Assert.Contains("\"row\":2", diagnostics);
        Assert.Contains("2025-02-30", diagnostics);
    }

    [Fact]
    public void Rejects_missing_required_values_and_incorrect_column_counts_without_shifting_columns()
    {
        var row = Row("", "");
        var (rows, report, diagnostics) = Parse(Tsv(row) + "too\tfew\tcolumns\n");
        Assert.Empty(rows);
        Assert.Equal(2, report.RowsRead);
        Assert.Equal(2, report.RowsRejected);
        Assert.Equal(1, report.MissingTaxonIds);
        Assert.Equal(1, report.MissingScientificNames);
        Assert.Contains("Expected 29 columns, found 3", diagnostics);
    }

    [Fact]
    public void Rejects_bad_quotes_and_reports_physical_row_after_a_multiline_record()
    {
        var good = Row("wfo-1", "Acer");
        good[22] = "line one\nline two";
        var bad = Row("wfo-2", "bad\"quote");
        var text = Tsv(good) + string.Join('\t', bad) + "\n";
        var (rows, report, diagnostics) = Parse(text);
        Assert.Single(rows);
        Assert.Equal(1, report.RowsRejected);
        Assert.Contains("\"row\":4", diagnostics);
        Assert.Contains("Malformed TSV quoting", diagnostics);
    }

    [Fact]
    public void Unclosed_quoted_field_is_rejected()
    {
        var (rows, report, diagnostics) = Parse(Tsv() + "wfo-1\t\"unterminated\n");
        Assert.Empty(rows);
        Assert.Equal(1, report.RowsRejected);
        Assert.Contains("Malformed TSV quoting", diagnostics);
    }

    [Fact]
    public void Damaged_text_warns_but_damaged_identifiers_are_rejected()
    {
        var text = Row("wfo-1", "Acer");
        text[22] = "damaged \uFFFD";
        var identifier = Row("wfo-\uFFFD", "Acer");
        var (rows, report, diagnostics) = Parse(Tsv(text, identifier));
        Assert.Single(rows);
        Assert.Equal(1, report.WarningCount);
        Assert.Equal(1, report.RowsRejected);
        Assert.Contains("Invalid UTF-8", diagnostics);
    }

    [Fact]
    public void Header_mismatch_is_fatal()
    {
        Assert.Throws<InvalidDataException>(() => Parse("taxonID\tscientificName\n"));
    }

    [Fact]
    public void Discovers_tab_delimited_csv_and_reads_actual_release_metadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "acbf-wfo-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "classification.csv");
            File.WriteAllText(path, Tsv(Row("wfo-1", "Acer")));
            File.WriteAllText(Path.Combine(directory, "fileInfo.json"), "{\"version\":\"2026-09\"}");
            Assert.Equal(path, WfoSource.Discover(directory));
            Assert.Equal("2026-09", WfoSource.Version(path));
            Assert.Equal("2026-06", WfoSource.Version(path, "2026-06"));
            File.WriteAllText(Path.Combine(directory, "second.tsv"), Tsv());
            Assert.Throws<InvalidOperationException>(() => WfoSource.Discover(directory));
        }
        finally
        {
            // Only files created by this test, in its uniquely allocated directory.
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Assert.Equal("2026-06", WfoSource.Version("wfo_2026-06.tsv"));
        Assert.Null(WfoSource.Version("classification.csv"));
    }

    internal static string[] Row(string taxonId, string name)
    {
        var fields = Enumerable.Repeat("", 29).ToArray();
        fields[0] = taxonId;
        fields[3] = name;
        fields[4] = "species";
        fields[18] = "Accepted";
        return fields;
    }

    internal static string Tsv(params string[][] rows)
    {
        using var writer = new StringWriter();
        using (var csv = new CsvWriter(writer, new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "\t" }, leaveOpen: true))
        {
            foreach (var row in new[] { WfoTsvReader.Headers }.Concat(rows))
            {
                foreach (var field in row) csv.WriteField(field);
                csv.NextRecord();
            }
        }
        return writer.ToString();
    }

    private static (List<WfoRow> Rows, ImportReport Report, string Diagnostics) Parse(string tsv)
    {
        var report = new ImportReport();
        using var diagnostics = new StringWriter();
        var rows = WfoTsvReader.Read(new StringReader(tsv), report, new ImportDiagnostics(diagnostics, report)).ToList();
        return (rows, report, diagnostics.ToString());
    }
}
