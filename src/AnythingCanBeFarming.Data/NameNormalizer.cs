using System.Globalization;
using System.Text;

namespace AnythingCanBeFarming.Data;

// Shared by every source that stores searchable names, and by search itself for the query side.
public static class NameNormalizer
{
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsWhiteSpace(raw))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(raw is '’' or '‘' ? '\'' : char.ToLowerInvariant(raw));
        }
        return builder.ToString();
    }

    // A normalized name without spaces or hyphens, so "black-eyed susan" meets "blackeyed susan".
    // wikidata_common_name.CompactName is generated with the same rule in SQL.
    public static string Compact(string normalized) => normalized.Replace(" ", "").Replace("-", "");
}
