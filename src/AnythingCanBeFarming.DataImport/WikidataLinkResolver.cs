using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record CoverageSummary(long AcceptedSpecies, long LinkedAcceptedSpecies, double Percent);

public sealed class ResolutionReport
{
    public long CurrentLinks { get; set; }
    public long LinksChanged { get; set; }
    public Dictionary<string, long> ResolutionStatus { get; } = [];
    public long LinksResolvedToCurrentTaxon { get; set; }
    public long LinksWithAcceptedTaxon { get; set; }
    public long LinksRedirectedThroughDeduplication { get; set; }
    public long MalformedWfoIds { get; set; }
    public List<string> MalformedWfoIdExamples { get; } = [];
    public CoverageSummary? Coverage { get; set; }
}

// Section 3.3: follow WFO deduplication replacements, then pick the current and accepted taxon.
// Links are never inferred from names. Only rows whose outcome changed are rewritten.
public static class WikidataLinkResolver
{
    private const int MaximumExamples = 20;
    // Same rule as WfoIdentifier.IsWellFormed; PostgreSQL's $ only matches at the end of the string.
    private const string WellFormedWfoId = "'^wfo-[0-9]{10}$'";

    // Serializes resolution against crosswalk publication without holding the Wikidata source lock,
    // so a long details run never blocks a WFO import's resolution step.
    public static long LockKey { get; } = SourceImportSession.LockKey("Wikidata:resolution");

    public static async Task<ResolutionReport> RunAsync(string connectionString, TextWriter output, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>().UseNpgsql(connection).Options);
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("Apply EF Core migrations before resolving Wikidata links; the importer does not migrate the database.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var report = await ResolveAsync(connection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    // Runs inside the caller's transaction.
    public static async Task<ResolutionReport> ResolveAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, $"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = Command(connection, """SELECT "DeprecatedWfoId", "ReplacementWfoId" FROM reference.wfo_deduplicated_id"""))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) replacements[reader.GetString(0)] = reader.GetString(1);

        var replaced = new List<string>();
        await using (var command = Command(connection, """
            SELECT DISTINCT l."WfoId" FROM reference.wikidata_wfo_link l
            JOIN reference.wfo_deduplicated_id d ON d."DeprecatedWfoId" = l."WfoId"
            WHERE l."IsCurrent"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) replaced.Add(reader.GetString(0));

        await ExecuteAsync(connection, """
            DROP TABLE IF EXISTS wikidata_redirect;
            CREATE TEMP TABLE wikidata_redirect (wfo_id text PRIMARY KEY, resolved_wfo_id text NOT NULL, forced_status text) ON COMMIT DROP;
            """, cancellationToken);
        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY wikidata_redirect (wfo_id, resolved_wfo_id, forced_status) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var wfoId in replaced)
            {
                // Currency of the final ID is decided in SQL below; the traversal only follows the chain.
                var resolution = await WfoIdResolver.TraverseAsync(wfoId,
                    id => Task.FromResult(replacements.GetValueOrDefault(id)), _ => Task.FromResult(true), cancellationToken);
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(wfoId, NpgsqlDbType.Text, cancellationToken);
                await copy.WriteAsync(resolution.ResolvedWfoId, NpgsqlDbType.Text, cancellationToken);
                if (resolution.Status is "Cycle" or "DepthLimit") await copy.WriteAsync(resolution.Status, NpgsqlDbType.Text, cancellationToken);
                else await copy.WriteNullAsync(cancellationToken);
            }
            await copy.CompleteAsync(cancellationToken);
        }

        var report = new ResolutionReport();
        await using (var command = Command(connection, $"""
            WITH target AS (
                SELECT l."Id", l."WfoId" ~ {WellFormedWfoId} AS well_formed,
                    coalesce(r.resolved_wfo_id, l."WfoId") AS resolved, r.forced_status
                FROM reference.wikidata_wfo_link l
                LEFT JOIN wikidata_redirect r ON r.wfo_id = l."WfoId"
                WHERE l."IsCurrent"
            ), computed AS (
                SELECT t."Id",
                    CASE WHEN t.well_formed THEN t.resolved END AS resolved,
                    CASE WHEN NOT t.well_formed THEN 'NotFound'
                         WHEN t.forced_status IS NOT NULL THEN t.forced_status
                         WHEN taxon."Id" IS NOT NULL THEN 'Resolved'
                         ELSE 'NotFound' END AS status,
                    CASE WHEN t.well_formed AND t.forced_status IS NULL THEN taxon."Id" END AS taxon_id,
                    CASE WHEN NOT t.well_formed OR t.forced_status IS NOT NULL THEN NULL
                         WHEN taxon."TaxonomicStatus" = 'Accepted' THEN taxon."Id"
                         WHEN taxon."TaxonomicStatus" = 'Synonym' THEN accepted."Id" END AS accepted_id
                FROM target t
                LEFT JOIN reference.wfo_taxon taxon ON taxon."TaxonId" = t.resolved AND taxon."IsCurrent"
                LEFT JOIN reference.wfo_taxon accepted ON accepted."Id" = taxon."AcceptedTaxonId" AND accepted."IsCurrent"
            ), changed AS (
                UPDATE reference.wikidata_wfo_link l
                SET "ResolvedWfoId" = c.resolved, "ResolutionStatus" = c.status,
                    "WfoTaxonId" = c.taxon_id, "AcceptedWfoTaxonId" = c.accepted_id
                FROM computed c
                WHERE l."Id" = c."Id" AND (l."ResolvedWfoId", l."ResolutionStatus", l."WfoTaxonId", l."AcceptedWfoTaxonId")
                    IS DISTINCT FROM (c.resolved, c.status, c.taxon_id, c.accepted_id)
                RETURNING 1
            )
            SELECT count(*) FROM changed
            """))
            report.LinksChanged = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        await using (var command = Command(connection, """
            SELECT "ResolutionStatus", count(*) FROM reference.wikidata_wfo_link WHERE "IsCurrent"
            GROUP BY "ResolutionStatus" ORDER BY "ResolutionStatus"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) report.ResolutionStatus[reader.GetString(0)] = reader.GetInt64(1);
        await using (var command = Command(connection, $"""
            SELECT count(*), count("WfoTaxonId"), count("AcceptedWfoTaxonId"),
                count(*) FILTER (WHERE "ResolvedWfoId" <> "WfoId"),
                count(*) FILTER (WHERE NOT "WfoId" ~ {WellFormedWfoId})
            FROM reference.wikidata_wfo_link WHERE "IsCurrent"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            report.CurrentLinks = reader.GetInt64(0);
            report.LinksResolvedToCurrentTaxon = reader.GetInt64(1);
            report.LinksWithAcceptedTaxon = reader.GetInt64(2);
            report.LinksRedirectedThroughDeduplication = reader.GetInt64(3);
            report.MalformedWfoIds = reader.GetInt64(4);
        }
        await using (var command = Command(connection, $"""
            SELECT DISTINCT "WfoId" FROM reference.wikidata_wfo_link
            WHERE "IsCurrent" AND NOT "WfoId" ~ {WellFormedWfoId} ORDER BY "WfoId" LIMIT {MaximumExamples}
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) report.MalformedWfoIdExamples.Add(reader.GetString(0));
        await using (var command = Command(connection, """
            SELECT count(*), count(*) FILTER (WHERE EXISTS (SELECT 1 FROM reference.wikidata_wfo_link l
                WHERE l."IsCurrent" AND l."AcceptedWfoTaxonId" = t."Id"))
            FROM reference.wfo_taxon t
            WHERE t."IsCurrent" AND t."TaxonomicStatus" = 'Accepted' AND t."TaxonRank" = 'species'
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            var (species, linked) = (reader.GetInt64(0), reader.GetInt64(1));
            report.Coverage = new(species, linked, species == 0 ? 0 : Math.Round(100.0 * linked / species, 2));
        }
        return report;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = 0 };

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
