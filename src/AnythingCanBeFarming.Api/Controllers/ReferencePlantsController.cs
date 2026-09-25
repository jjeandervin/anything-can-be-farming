using System.Data;
using System.Linq.Expressions;
using AnythingCanBeFarming.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        if (string.IsNullOrEmpty(q) || q.Length > 200)
            return BadRequest(new { error = "q must contain 1 to 200 characters." });
        var maximum = Math.Clamp(configuration.GetValue("ReferencePlants:MaxPageSize", 100), 1, 100);
        var size = pageSize ?? Math.Clamp(configuration.GetValue("ReferencePlants:DefaultPageSize", 20), 1, maximum);
        if (size < 1 || size > maximum)
            return BadRequest(new { error = $"pageSize must be between 1 and {maximum}." });
        // Treat SQL pattern characters in user input as literal text.
        var pattern = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var results = await db.WfoTaxa.AsNoTracking().Where(x => x.IsCurrent &&
            (EF.Functions.ILike(x.ScientificName, pattern, "\\") ||
             (x.Genus != null && EF.Functions.ILike(x.Genus, pattern, "\\")) ||
             (x.SpecificEpithet != null && EF.Functions.ILike(x.SpecificEpithet, pattern, "\\"))))
            .OrderBy(x => x.ScientificName).ThenBy(x => x.TaxonId).Take(size)
            .Select(x => new
            {
                x.TaxonId, x.ScientificName, x.ScientificNameAuthorship, x.TaxonRank,
                x.TaxonomicStatus, x.Family, x.Genus
            }).ToListAsync(cancellationToken);
        return Ok(results);
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
