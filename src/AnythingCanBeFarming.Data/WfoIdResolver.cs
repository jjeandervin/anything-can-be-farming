using Microsoft.EntityFrameworkCore;

namespace AnythingCanBeFarming.Data;

public sealed record WfoIdResolution(string RequestedWfoId, string ResolvedWfoId, string Status)
{
    public bool WasRedirected => RequestedWfoId != ResolvedWfoId;
}

public sealed class WfoIdResolver(AcbfDbContext db)
{
    public const int MaximumDepth = 64;

    public Task<WfoIdResolution> ResolveAsync(string wfoId, CancellationToken cancellationToken = default) =>
        TraverseAsync(wfoId,
            id => db.WfoDeduplicatedIds.AsNoTracking().Where(x => x.DeprecatedWfoId == id)
                .Select(x => x.ReplacementWfoId).SingleOrDefaultAsync(cancellationToken),
            id => db.WfoTaxa.AnyAsync(x => x.TaxonId == id && x.IsCurrent, cancellationToken), cancellationToken);

    // Explicit replacements take precedence even when the old ID remains in the backbone.
    public static async Task<WfoIdResolution> TraverseAsync(string wfoId,
        Func<string, Task<string?>> replacement, Func<string, Task<bool>> isCurrent,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = wfoId;
        for (var depth = 0; ; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current)) return new(wfoId, current, "Cycle");
            var next = await replacement(current);
            if (next == null)
                return new(wfoId, current, await isCurrent(current) ? "Resolved" : "NotFound");
            if (depth == MaximumDepth) return new(wfoId, current, "DepthLimit");
            current = next;
        }
    }
}
