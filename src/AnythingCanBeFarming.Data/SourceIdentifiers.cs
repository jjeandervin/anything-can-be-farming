using System.Text.RegularExpressions;

namespace AnythingCanBeFarming.Data;

// \z rather than $: .NET's $ also matches before a trailing newline.
public static partial class WikidataIdentifier
{
    public static bool IsQid(string? value) => value != null && QidPattern().IsMatch(value);

    [GeneratedRegex(@"^Q[1-9][0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex QidPattern();
}

public static partial class WfoIdentifier
{
    public static bool IsWellFormed(string? value) => value != null && WfoIdPattern().IsMatch(value);

    [GeneratedRegex(@"^wfo-[0-9]{10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WfoIdPattern();
}
