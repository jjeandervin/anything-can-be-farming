using System.Text.RegularExpressions;

namespace AnythingCanBeFarming.Data;

public static partial class UsdaCode
{
    // A value URI's last path or fragment segment, otherwise the raw literal. PostgreSQL generates
    // usda_fact.ValueCode with this expression; FromValue must stay equivalent.
    public const string ValueCodeSql = """
        CASE WHEN "ValueRaw" ~ '^https?://' THEN regexp_replace("ValueRaw", '^.*[/#]', '') ELSE "ValueRaw" END
        """;

    public static string FromValue(string raw) =>
        UriPattern().IsMatch(raw) ? raw[(raw.LastIndexOfAny(['/', '#']) + 1)..] : raw;

    [GeneratedRegex("^https?://", RegexOptions.CultureInvariant)]
    private static partial Regex UriPattern();
}
