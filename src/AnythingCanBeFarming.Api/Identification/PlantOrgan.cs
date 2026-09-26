using System.Diagnostics.CodeAnalysis;

namespace AnythingCanBeFarming.Api.Identification;

public enum PlantOrgan { Auto, Leaf, Flower, Fruit, Bark, Habit, Scan, Branch, Sheet, Other, Drawing, Seed, Bud, Anatomy, Aerial }

public static class PlantOrgans
{
    // Explicit mapping: Enum.TryParse would also accept "Leaf", " leaf" and numeric strings such as "1".
    private static readonly Dictionary<PlantOrgan, string> Wire = new()
    {
        [PlantOrgan.Auto] = "auto", [PlantOrgan.Leaf] = "leaf", [PlantOrgan.Flower] = "flower",
        [PlantOrgan.Fruit] = "fruit", [PlantOrgan.Bark] = "bark", [PlantOrgan.Habit] = "habit",
        [PlantOrgan.Scan] = "scan", [PlantOrgan.Branch] = "branch", [PlantOrgan.Sheet] = "sheet",
        [PlantOrgan.Other] = "other", [PlantOrgan.Drawing] = "drawing", [PlantOrgan.Seed] = "seed",
        [PlantOrgan.Bud] = "bud", [PlantOrgan.Anatomy] = "anatomy", [PlantOrgan.Aerial] = "aerial"
    };

    private static readonly Dictionary<string, PlantOrgan> ByWire =
        Wire.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static IReadOnlyCollection<string> WireValues => Wire.Values;

    public static bool TryParse([NotNullWhen(true)] string? value, out PlantOrgan organ)
    {
        organ = default;
        return value is not null && ByWire.TryGetValue(value, out organ);
    }

    public static string ToWire(PlantOrgan organ) =>
        Wire.TryGetValue(organ, out var value) ? value : throw new ArgumentOutOfRangeException(nameof(organ));
}
