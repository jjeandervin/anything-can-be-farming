using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace AnythingCanBeFarming.DataImport;

public sealed record UsdaRow(long SourceRow, string[] Fields);

// EOL archives are plain tab-separated text without quoting, so quote characters are data. Remarks contain
// literal backslash-n sequences, not line breaks; they are kept exactly as written.
public static class UsdaTsvReader
{
    public static IEnumerable<UsdaRow> Read(TextReader reader, UsdaTable table, Action<long, string> malformed)
    {
        using var parser = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = "\t", Mode = CsvMode.NoEscape, IgnoreBlankLines = false, TrimOptions = TrimOptions.None,
            MaxFieldSize = 16 * 1024 * 1024, ExceptionMessagesContainRawData = false
        });
        if (!parser.Read()) throw new InvalidDataException($"The {table.Role} file is empty.");
        if (!parser.Record!.SequenceEqual(table.Headers))
            throw new InvalidDataException($"The {table.Role} file's header does not match meta.xml ({string.Join(", ", table.Headers)}).");
        while (true)
        {
            var sourceRow = (long)parser.RawRow + 1;
            string[] fields;
            try
            {
                if (!parser.Read()) yield break;
                fields = parser.Record!;
            }
            catch (CsvHelperException)
            {
                throw new InvalidDataException($"Malformed {table.Role} TSV at source row {sourceRow}.");
            }
            if (fields.Length == table.Headers.Length) yield return new(sourceRow, fields);
            else if (!(fields.Length == 1 && fields[0].Length == 0))
                malformed(sourceRow, $"Expected {table.Headers.Length} columns, found {fields.Length}.");
        }
    }
}
