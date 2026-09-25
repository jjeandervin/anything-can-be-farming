using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnythingCanBeFarming.DataImport;

public static partial class WfoSource
{
    public static string FindRepositoryRoot(string? start = null)
    {
        for (var directory = new DirectoryInfo(start ?? Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AnythingCanBeFarming.sln"))) return directory.FullName;
        throw new InvalidOperationException("Run from the ACBF repository or supply --file and configure ConnectionStrings:acbf.");
    }

    public static string Discover(string directory)
    {
        if (!Directory.Exists(directory)) throw new FileNotFoundException($"Stage the WFO TSV under {directory} or use --file.");
        var candidates = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".tsv", ".txt", ".csv" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                using var reader = new StreamReader(path, Encoding.UTF8);
                return reader.ReadLine()?.TrimStart('\uFEFF').StartsWith("taxonID\t", StringComparison.Ordinal) == true;
            }).Take(2).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("No WFO TSV found (including .csv files with tab-delimited headers). Use --file."),
            _ => throw new InvalidOperationException("Multiple WFO snapshots found. Select one with --file.")
        };
    }

    public static string? Version(string path, string? versionOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(versionOverride)) return versionOverride;
        var metadata = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "fileInfo.json");
        if (File.Exists(metadata))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            if (document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                return version.GetString();
        }
        var match = ReleasePattern().Match(Path.GetFileNameWithoutExtension(path));
        return match.Success ? match.Value.Replace('_', '-') : null;
    }

    [GeneratedRegex(@"(?<!\d)20\d{2}[-_](?:0[1-9]|1[0-2])(?!\d)")]
    private static partial Regex ReleasePattern();
}
