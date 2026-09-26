using System.Data;
using System.Linq.Expressions;
using AnythingCanBeFarming.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnythingCanBeFarming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reference/plants")]
public sealed class ReferencePlantsController(AcbfDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < 2 || q.Length > 200)
            return BadRequest(new { error = "q must contain 2 to 200 characters." });
        var maximum = Math.Clamp(configuration.GetValue("ReferencePlants:MaxPageSize", 100), 1, 100);
        var size = pageSize ?? Math.Clamp(configuration.GetValue("ReferencePlants:DefaultPageSize", 20), 1, maximum);
        if (size < 1 || size > maximum)
            return BadRequest(new { error = $"pageSize must be between 1 and {maximum}." });
        var normalized = NameNormalizer.Normalize(q);
        var results = await db.Database.SqlQueryRaw<PlantSearchResult>(SearchSql,
            new NpgsqlParameter("q", q),
            new NpgsqlParameter("contains", "%" + EscapeLike(q) + "%"),
            new NpgsqlParameter("prefix", EscapeLike(q) + "%"),
            new NpgsqlParameter("wordSpace", "% " + EscapeLike(q) + "%"),
            new NpgsqlParameter("wordHyphen", "%-" + EscapeLike(q) + "%"),
            // A query that normalizes to nothing (only combining marks) must not match every common name.
            new NpgsqlParameter("searchCommon", normalized.Length > 0),
            new NpgsqlParameter("nq", normalized),
            new NpgsqlParameter("ncontains", "%" + EscapeLike(normalized) + "%"),
            new NpgsqlParameter("nprefix", EscapeLike(normalized) + "%"),
            new NpgsqlParameter("nwordSpace", "% " + EscapeLike(normalized) + "%"),
            new NpgsqlParameter("nwordHyphen", "%-" + EscapeLike(normalized) + "%"),
            new NpgsqlParameter("size", size)).ToListAsync(cancellationToken);
        return Ok(results);
    }

    // Treat SQL pattern characters in user input as literal text.
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // Genus before species before infraspecific ranks; "notho" (hybrid) ranks sort with their base rank,
    // and anything unlisted (WFO "unranked") sorts last.
    private static readonly string RankDepthSql = "CASE regexp_replace(lower(t.\"TaxonRank\"), '^notho', '') " + string.Join(" ", new[]
    {
        "kingdom", "subkingdom", "phylum", "subphylum", "class", "subclass", "superorder", "order", "suborder",
        "family", "subfamily", "supertribe", "tribe", "subtribe", "genus", "subgenus", "section", "subsection", "series", "subseries",
        "species", "subspecies", "prole", "convar", "variety", "subvariety", "form", "subform", "lusus"
    }.Select((rank, depth) => $"WHEN '{rank}' THEN {depth}")) + " ELSE 1000 END";

    // One row per accepted taxon: synonyms report against their current accepted taxon, common names against the
    // link's accepted taxon, and taxa without one (Unchecked, dangling synonyms) as themselves. Each taxon keeps its
    // best match: exact, prefix, word prefix, then substring. Within a tier, a match on the taxon's own name or common
    // name beats a synonym match (the synonym "Hosta" must not outrank the genus Hosta). The trigram indexes serve
    // the ILIKE/LIKE filters.
    private static readonly string SearchSql = $$"""
        WITH scientific AS (
            SELECT CASE WHEN t."TaxonomicStatus" = 'Synonym' AND a."Id" IS NOT NULL THEN a."Id" ELSE t."Id" END AS target_id,
                CASE WHEN t."TaxonomicStatus" = 'Synonym' AND a."Id" IS NOT NULL THEN 'synonym' ELSE 'scientificName' END AS matched_on,
                CASE WHEN t."TaxonomicStatus" = 'Synonym' AND a."Id" IS NOT NULL THEN 1 ELSE 0 END AS source_order,
                t."ScientificName" AS matched_text,
                CASE WHEN lower(t."ScientificName") = lower(@q) THEN 1
                     WHEN t."ScientificName" ILIKE @prefix ESCAPE '\' THEN 2
                     WHEN t."ScientificName" ILIKE @wordSpace ESCAPE '\' OR t."ScientificName" ILIKE @wordHyphen ESCAPE '\' THEN 3
                     ELSE 4 END AS tier
            FROM reference.wfo_taxon t
            LEFT JOIN reference.wfo_taxon a ON a."Id" = t."AcceptedTaxonId" AND a."IsCurrent"
            WHERE t."IsCurrent" AND (t."ScientificName" ILIKE @contains ESCAPE '\' OR t."Genus" ILIKE @contains ESCAPE '\')
        ), common AS (
            SELECT coalesce(l."AcceptedWfoTaxonId", l."WfoTaxonId") AS target_id, 'commonName' AS matched_on, 2 AS source_order,
                n."Name" AS matched_text,
                CASE WHEN n."NormalizedName" = @nq THEN 1
                     WHEN n."NormalizedName" LIKE @nprefix ESCAPE '\' THEN 2
                     WHEN n."NormalizedName" LIKE @nwordSpace ESCAPE '\' OR n."NormalizedName" LIKE @nwordHyphen ESCAPE '\' THEN 3
                     ELSE 4 END AS tier
            FROM reference.wikidata_common_name n
            JOIN reference.wikidata_wfo_link l ON l."ItemId" = n."ItemId" AND l."IsCurrent"
            WHERE @searchCommon AND (n."Language" = 'en' OR n."Language" LIKE 'en-%')
                AND n."NormalizedName" LIKE @ncontains ESCAPE '\'
        ), best AS (
            SELECT DISTINCT ON (m.target_id) m.target_id, m.tier, m.matched_on, m.matched_text
            FROM (SELECT * FROM scientific UNION ALL SELECT * FROM common) m
            WHERE m.target_id IS NOT NULL
            ORDER BY m.target_id, m.tier, m.source_order, length(m.matched_text), m.matched_text COLLATE "C"
        ), page AS (
            SELECT t."Id", t."TaxonId", t."ScientificName", t."ScientificNameAuthorship", t."TaxonRank", t."TaxonomicStatus",
                t."Family", t."Genus", b.tier, b.matched_on, b.matched_text,
                b.matched_on = 'synonym' AS via_synonym, t."TaxonomicStatus" IS DISTINCT FROM 'Accepted' AS not_accepted,
                {{RankDepthSql}} AS depth
            FROM best b JOIN reference.wfo_taxon t ON t."Id" = b.target_id AND t."IsCurrent"
            ORDER BY b.tier, via_synonym, not_accepted, depth, t."ScientificName" COLLATE "C", t."TaxonId" COLLATE "C"
            LIMIT @size
        )
        SELECT p."TaxonId", p."ScientificName", p."ScientificNameAuthorship", p."TaxonRank", p."TaxonomicStatus",
            p."Family", p."Genus", c."Name" AS "CommonName", p.matched_on AS "MatchedOn", p.matched_text AS "MatchedText"
        FROM page p
        LEFT JOIN LATERAL (
            SELECT n."Name" FROM reference.wikidata_common_name n
            JOIN reference.wikidata_wfo_link l ON l."ItemId" = n."ItemId" AND l."IsCurrent"
            WHERE (l."AcceptedWfoTaxonId" = p."Id" OR (l."AcceptedWfoTaxonId" IS NULL AND l."WfoTaxonId" = p."Id"))
                AND (n."Language" = 'en' OR n."Language" LIKE 'en-%')
            ORDER BY n."Language" <> 'en', NOT (@searchCommon AND n."NormalizedName" LIKE @ncontains ESCAPE '\'),
                length(n."Name"), n."Name" COLLATE "C"
            LIMIT 1
        ) c ON true
        ORDER BY p.tier, p.via_synonym, p.not_accepted, p.depth, p."ScientificName" COLLATE "C", p."TaxonId" COLLATE "C"
        """;

    // Wikidata and WFO strings are untrusted source text; clients must render them as text, never HTML.
    public sealed class PlantSearchResult
    {
        public required string TaxonId { get; init; }
        public required string ScientificName { get; init; }
        public string? ScientificNameAuthorship { get; init; }
        public string? TaxonRank { get; init; }
        public string? TaxonomicStatus { get; init; }
        public string? Family { get; init; }
        public string? Genus { get; init; }
        public string? CommonName { get; init; }
        public required string MatchedOn { get; init; }
        public required string MatchedText { get; init; }
    }

    [HttpGet("{taxonId}")]
    public async Task<IActionResult> Get(string taxonId, CancellationToken cancellationToken)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        return await GetResolved(taxonId, cancellationToken);
    }

    private async Task<IActionResult> GetResolved(string taxonId, CancellationToken cancellationToken)
    {
        var resolution = await new WfoIdResolver(db).ResolveAsync(taxonId, cancellationToken);
        if (resolution.Status is "Cycle" or "DepthLimit")
            return Conflict(new { error = "WFO replacement mapping cannot be resolved safely.", requestedWfoId = taxonId });
        if (resolution.Status != "Resolved") return NotFound();
        var record = await db.WfoTaxa.AsNoTracking().Where(x => x.TaxonId == resolution.ResolvedWfoId && x.IsCurrent).Select(x => new
        {
            x.Id,
            x.TaxonId,
            x.ScientificNameId,
            x.LocalId,
            x.ScientificName,
            x.TaxonRank,
            x.ParentNameUsageId,
            x.ScientificNameAuthorship,
            x.Family,
            x.Subfamily,
            x.Tribe,
            x.Subtribe,
            x.Genus,
            x.Subgenus,
            x.SpecificEpithet,
            x.InfraspecificEpithet,
            x.VerbatimTaxonRank,
            x.NomenclaturalStatus,
            x.NamePublishedIn,
            x.TaxonomicStatus,
            x.AcceptedNameUsageId,
            x.OriginalNameUsageId,
            x.NameAccordingToId,
            x.TaxonRemarks,
            x.WfoCreatedAt,
            x.WfoModifiedAt,
            x.References,
            x.Source,
            x.MajorGroup,
            x.TplId,
            x.IsCurrent,
            Parent = x.Parent == null ? null : new RelatedTaxon(x.Parent.TaxonId, x.Parent.ScientificName),
            AcceptedTaxon = x.AcceptedTaxon == null ? null : new RelatedTaxon(x.AcceptedTaxon.TaxonId, x.AcceptedTaxon.ScientificName),
            OriginalTaxon = x.OriginalTaxon == null ? null : new RelatedTaxon(x.OriginalTaxon.TaxonId, x.OriginalTaxon.ScientificName)
        }).SingleOrDefaultAsync(cancellationToken);
        return record == null ? NotFound() : Ok(new
        {
            resolution.RequestedWfoId, resolution.ResolvedWfoId, resolution.WasRedirected, Taxon = record
        });
    }

    [HttpGet("by-ipni/{ipniId}")]
    public async Task<IActionResult> ByIpni(string ipniId, CancellationToken cancellationToken)
    {
        var normalized = IpniIdentifier.Normalize(ipniId);
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 200)
            return BadRequest(new { error = "ipniId must contain 1 to 200 characters." });
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var mappings = await db.WfoIpniMappings.AsNoTracking().Where(x => x.NormalizedIpniId == normalized)
            .Select(x => x.WfoId).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var resolver = new WfoIdResolver(db);
        var resolved = new List<WfoIdResolution>();
        foreach (var id in mappings)
        {
            var result = await resolver.ResolveAsync(id, cancellationToken);
            if (result.Status is "Cycle" or "DepthLimit")
                return Conflict(new { error = "WFO replacement mapping cannot be resolved safely.", ipniId });
            if (result.Status == "Resolved") resolved.Add(result);
        }
        var targets = resolved.Select(x => x.ResolvedWfoId).Distinct().ToArray();
        if (targets.Length == 0) return NotFound();
        // Historical mappings are not guaranteed to be one-to-one; never choose arbitrarily.
        if (targets.Length > 1)
            return Conflict(new { error = "IPNI identifier maps to multiple current WFO taxa.", ipniId, wfoIds = targets });
        return await GetResolved(resolved[0].RequestedWfoId, cancellationToken);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken)
    {
        // All counters refer to one committed snapshot, even if an import publishes between queries.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var current = db.WfoTaxa.AsNoTracking().Where(x => x.IsCurrent);
        var totalRecords = await db.WfoTaxa.LongCountAsync(cancellationToken);
        var counts = await current.GroupBy(_ => 1).Select(g => new
        {
            CurrentRecords = g.LongCount(),
            UniqueFamilies = g.Where(x => x.Family != null).Select(x => x.Family).Distinct().LongCount(),
            UniqueGenera = g.Where(x => x.Genus != null).Select(x => x.Genus).Distinct().LongCount(),
            ResolvedParentRelationships = g.LongCount(x => x.ParentId != null),
            UnresolvedParentRelationships = g.LongCount(x => x.ParentNameUsageId != null && x.ParentId == null),
            ResolvedAcceptedNameRelationships = g.LongCount(x => x.AcceptedTaxonId != null),
            UnresolvedAcceptedNameRelationships = g.LongCount(x => x.AcceptedNameUsageId != null && x.AcceptedTaxonId == null),
            ResolvedOriginalNameRelationships = g.LongCount(x => x.OriginalTaxonId != null),
            UnresolvedOriginalNameRelationships = g.LongCount(x => x.OriginalNameUsageId != null && x.OriginalTaxonId == null)
        }).SingleOrDefaultAsync(cancellationToken);
        var taxonomicStatus = await Groups(current, x => x.TaxonomicStatus, cancellationToken);
        var nomenclaturalStatus = await Groups(current, x => x.NomenclaturalStatus, cancellationToken);
        var taxonRank = await Groups(current, x => x.TaxonRank, cancellationToken);
        var majorGroup = await Groups(current, x => x.MajorGroup, cancellationToken);
        var lastImport = await db.WfoImports.AsNoTracking().Where(x => x.DatasetKind == "Backbone" && x.Status == "Succeeded")
            .OrderByDescending(x => x.CompletedAt).Select(x => new
            {
                x.Id, x.DatasetVersion, x.CompletedAt, x.SourceFileName, x.SourceFileHash,
                x.RowsRead, x.RowsImported, x.RowsRejected, x.WarningCount
            }).FirstOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new
        {
            TotalRecords = totalRecords,
            CurrentRecords = counts?.CurrentRecords ?? 0,
            UniqueFamilies = counts?.UniqueFamilies ?? 0,
            UniqueGenera = counts?.UniqueGenera ?? 0,
            RecordsByTaxonomicStatus = taxonomicStatus, RecordsByNomenclaturalStatus = nomenclaturalStatus,
            RecordsByTaxonRank = taxonRank, RecordsByMajorGroup = majorGroup,
            ResolvedParentRelationships = counts?.ResolvedParentRelationships ?? 0,
            UnresolvedParentRelationships = counts?.UnresolvedParentRelationships ?? 0,
            ResolvedAcceptedNameRelationships = counts?.ResolvedAcceptedNameRelationships ?? 0,
            UnresolvedAcceptedNameRelationships = counts?.UnresolvedAcceptedNameRelationships ?? 0,
            ResolvedOriginalNameRelationships = counts?.ResolvedOriginalNameRelationships ?? 0,
            UnresolvedOriginalNameRelationships = counts?.UnresolvedOriginalNameRelationships ?? 0,
            LastImportedWfoVersion = lastImport?.DatasetVersion,
            LastImportTimestamp = lastImport?.CompletedAt, LastImport = lastImport
        });
    }

    private static Task<List<ReferenceValueCount>> Groups(IQueryable<WfoTaxon> taxa,
        Expression<Func<WfoTaxon, string?>> key, CancellationToken cancellationToken) =>
        taxa.GroupBy(key).OrderBy(g => g.Key).Select(g => new ReferenceValueCount(g.Key, g.LongCount()))
            .ToListAsync(cancellationToken);

    public sealed record RelatedTaxon(string TaxonId, string ScientificName);
    public sealed record ReferenceValueCount(string? Value, long Count);
}
