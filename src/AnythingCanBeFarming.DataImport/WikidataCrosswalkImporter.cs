using System.Diagnostics;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record SourceImportResult<TReport>(long ImportId, TReport Report);
public sealed record PartitionTiming(string Partition, int Attempt, double Seconds, long Rows, string Outcome);
public sealed record MultiValueExample(string Key, string[] Values);
public sealed record MultiValueSummary(long Count, List<MultiValueExample> Examples);

public sealed class CrosswalkReport
{
    public List<PartitionTiming> Partitions { get; } = [];
    public long RowsRead { get; set; }
    public long DeprecatedStatementsSkipped { get; set; }
    public long DuplicateStatementsCollapsed { get; set; }
    public long Links { get; set; }
    public long DistinctQids { get; set; }
    public long DistinctWfoIds { get; set; }
    public MultiValueSummary? ItemsWithMultipleWfoIds { get; set; }
    public MultiValueSummary? WfoIdsOnMultipleItems { get; set; }
    public long CurrentLinksBefore { get; set; }
    public long LinksInserted { get; set; }
    public long LinksUpdated { get; set; }
    public long LinksRetired { get; set; }
    public long ItemsInserted { get; set; }
    public long ItemsReactivated { get; set; }
    public long ItemsRetired { get; set; }
    public long WarningCount { get; set; }
    public List<string> Warnings { get; } = [];
}

// Runs the partitioned SPARQL harvest. A partition that times out or errors is split by two-character
// suffix; each sub-partition gets one retry. A partition's rows are handed on only once it completed.
public sealed class WikidataCrosswalkHarvester(WikidataSparqlClient sparql, TextWriter output)
{
    public async Task HarvestAsync(Func<IReadOnlyList<CrosswalkRow>, CancellationToken, Task> accept,
        CrosswalkReport report, SparqlParseStats stats, CancellationToken cancellationToken)
    {
        foreach (var partition in CrosswalkPartition.All)
        {
            if (await TryAsync(partition, partition.CanSubdivide ? 1 : 2)) continue;
            if (!partition.CanSubdivide)
                throw new InvalidDataException($"Crosswalk partition {partition.Label} failed twice; nothing was published.");
            output.WriteLine($"Partition {partition.Label} failed; subdividing by two-character suffix.");
            foreach (var subpartition in partition.Subdivide())
                if (!await TryAsync(subpartition, 2))
                    throw new InvalidDataException($"Crosswalk partition {subpartition.Label} failed twice after subdivision; nothing was published.");
        }

        async Task<bool> TryAsync(CrosswalkPartition partition, int attempts)
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var partitionStats = new SparqlParseStats();
                var timer = Stopwatch.StartNew();
                List<CrosswalkRow> rows;
                try
                {
                    rows = await sparql.QueryAsync(partition, partitionStats, cancellationToken);
                }
                catch (Exception exception) when (IsPartitionFailure(exception))
                {
                    var reason = exception is WikidataHttpException ? exception.Message : "Malformed or truncated response.";
                    report.Partitions.Add(new(partition.Label, attempt, Seconds(timer), 0, "Failed: " + reason));
                    output.WriteLine($"Partition {partition.Label} attempt {attempt} failed after {Seconds(timer):N1} s: {reason}");
                    continue;
                }
                await accept(rows, cancellationToken);
                stats.Add(partitionStats);
                report.Partitions.Add(new(partition.Label, attempt, Seconds(timer), rows.Count, "Succeeded"));
                output.WriteLine($"Partition {partition.Label}: {rows.Count:N0} links in {Seconds(timer):N1} s");
                return true;
            }
            return false;
        }
    }

    // Timeouts, server errors, dropped connections, and damaged bodies are worth splitting;
    // client errors (including exhausted 429s) and cancellation are not.
    private static bool IsPartitionFailure(Exception exception) => exception switch
    {
        WikidataHttpException http => http.IsTimeout || http.StatusCode is null || (int)http.StatusCode >= 500,
        JsonException or InvalidDataException => true,
        _ => false
    };

    private static double Seconds(Stopwatch timer) => Math.Round(timer.Elapsed.TotalSeconds, 3);
}

public sealed class WikidataCrosswalkImporter(string connectionString, WikidataSparqlClient sparql, WikidataApiClient api, TextWriter output)
{
    public const string Source = "Wikidata";
    private const int MaximumExamples = 20;

    public async Task<SourceImportResult<CrosswalkReport>> ImportAsync(bool allowShrink, CancellationToken cancellationToken = default)
    {
        await using var session = await SourceImportSession.OpenAsync(connectionString, Source, cancellationToken);
        var import = await session.StartAsync("Crosswalk", new { command = "crosswalk", allowShrink }, cancellationToken);
        output.WriteLine($"Import {import.Id}: Wikidata crosswalk");
        var report = new CrosswalkReport();
        var stats = new SparqlParseStats();
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            await api.VerifyPropertiesAsync(cancellationToken);
            await session.ExecuteAsync("""
                DROP TABLE IF EXISTS wikidata_crosswalk_stage;
                CREATE TEMP TABLE wikidata_crosswalk_stage (qid text NOT NULL, wfo_id text NOT NULL, statement_rank text NOT NULL);
                """, cancellationToken);
            await new WikidataCrosswalkHarvester(sparql, output).HarvestAsync(
                (rows, token) => StageAsync(session.Connection, rows, token), report, stats, cancellationToken);
            report.RowsRead = stats.RowsRead;
            report.DeprecatedStatementsSkipped = stats.DeprecatedSkipped;
            report.WarningCount = stats.WarningCount;
            report.Warnings.AddRange(stats.Warnings);

            transaction = await session.Db.Database.BeginTransactionAsync(cancellationToken);
            await PublishAsync(session, import.Id, allowShrink, report, cancellationToken);
            Apply(import, report);
            await session.CompleteAsync(import, "Succeeded", report, null);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            var (status, message) = SourceImportSession.Describe(exception, cancellationToken);
            report.LinksInserted = report.LinksUpdated = report.LinksRetired = 0;
            report.ItemsInserted = report.ItemsReactivated = report.ItemsRetired = 0;
            Apply(import, report);
            await session.CompleteAsync(import, status, report, message);
            output.WriteLine(JsonSerializer.Serialize(report, Indented));
            throw new InvalidDataException($"Import {import.Id}: {message}", exception);
        }
        finally
        {
            if (transaction != null) await transaction.DisposeAsync();
        }
        output.WriteLine(JsonSerializer.Serialize(report, Indented));
        return new(import.Id, report);
    }

    private static async Task StageAsync(NpgsqlConnection connection, IReadOnlyList<CrosswalkRow> rows, CancellationToken cancellationToken)
    {
        await using var copy = await connection.BeginBinaryImportAsync(
            "COPY wikidata_crosswalk_stage (qid, wfo_id, statement_rank) FROM STDIN (FORMAT BINARY)", cancellationToken);
        copy.Timeout = TimeSpan.Zero;
        foreach (var row in rows)
        {
            await copy.StartRowAsync(cancellationToken);
            await copy.WriteAsync(row.Qid, NpgsqlDbType.Text, cancellationToken);
            await copy.WriteAsync(row.WfoId, NpgsqlDbType.Text, cancellationToken);
            await copy.WriteAsync(row.StatementRank, NpgsqlDbType.Text, cancellationToken);
        }
        await copy.CompleteAsync(cancellationToken);
    }

    private async Task PublishAsync(SourceImportSession session, long importId, bool allowShrink, CrosswalkReport report,
        CancellationToken cancellationToken)
    {
        var import = ("import", (object)importId);
        // A QID can state the same WFO ID more than once; keep one link, preferring the preferred-rank statement.
        await session.ExecuteAsync("""
            CREATE TEMP TABLE wikidata_crosswalk ON COMMIT DROP AS
            SELECT DISTINCT ON (qid, wfo_id) qid, wfo_id, statement_rank FROM wikidata_crosswalk_stage
            ORDER BY qid, wfo_id, statement_rank = 'preferred' DESC;
            CREATE UNIQUE INDEX ON wikidata_crosswalk (qid, wfo_id);
            CREATE INDEX ON wikidata_crosswalk (wfo_id);
            ANALYZE wikidata_crosswalk;
            """, cancellationToken);
        var counts = await session.ScalarsAsync("""
            SELECT (SELECT count(*) FROM wikidata_crosswalk_stage), count(*), count(DISTINCT qid), count(DISTINCT wfo_id),
                (SELECT count(*) FROM reference.wikidata_wfo_link WHERE "IsCurrent")
            FROM wikidata_crosswalk
            """, cancellationToken);
        report.DuplicateStatementsCollapsed = counts[0] - counts[1];
        (report.Links, report.DistinctQids, report.DistinctWfoIds, report.CurrentLinksBefore) = (counts[1], counts[2], counts[3], counts[4]);
        report.ItemsWithMultipleWfoIds = await MultiValueAsync(session, "qid", "wfo_id", cancellationToken);
        report.WfoIdsOnMultipleItems = await MultiValueAsync(session, "wfo_id", "qid", cancellationToken);

        // An empty or sharply smaller result almost always means an upstream outage, not real deletions.
        if (report.Links == 0)
            throw new InvalidDataException("Crosswalk returned no links; nothing was published.");
        if (!allowShrink && report.Links * 2 < report.CurrentLinksBefore)
            throw new InvalidDataException($"Crosswalk returned {report.Links:N0} links, fewer than 50% of the {report.CurrentLinksBefore:N0} current links; " +
                "nothing was published. Use --allow-shrink if the upstream change is real.");

        output.WriteLine("Harvest complete; publishing the crosswalk…");
        // Rows are only rewritten when something changed, so an unchanged rerun touches nothing.
        (report.ItemsInserted, report.ItemsReactivated) = Pair(await session.ScalarsAsync("""
            WITH upserted AS (
                INSERT INTO reference.wikidata_item AS item ("Qid", "IsCurrent", "CrosswalkImportId")
                SELECT DISTINCT qid, true, @import FROM wikidata_crosswalk
                ON CONFLICT ("Qid") DO UPDATE SET "IsCurrent" = true, "CrosswalkImportId" = EXCLUDED."CrosswalkImportId"
                WHERE NOT item."IsCurrent"
                RETURNING xmax = 0 AS inserted)
            SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM upserted
            """, cancellationToken, import));
        (report.LinksInserted, report.LinksUpdated) = Pair(await session.ScalarsAsync("""
            WITH upserted AS (
                INSERT INTO reference.wikidata_wfo_link AS link
                    ("ItemId", "Qid", "WfoId", "StatementRank", "ResolutionStatus", "IsCurrent", "ImportId")
                SELECT i."Id", c.qid, c.wfo_id, c.statement_rank, 'NotFound', true, @import
                FROM wikidata_crosswalk c JOIN reference.wikidata_item i ON i."Qid" = c.qid
                ON CONFLICT ("Qid", "WfoId") DO UPDATE SET "StatementRank" = EXCLUDED."StatementRank",
                    "IsCurrent" = true, "ImportId" = EXCLUDED."ImportId"
                WHERE link."StatementRank" <> EXCLUDED."StatementRank" OR NOT link."IsCurrent"
                RETURNING xmax = 0 AS inserted)
            SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM upserted
            """, cancellationToken, import));
        report.LinksRetired = (await session.ScalarsAsync("""
            WITH retired AS (
                UPDATE reference.wikidata_wfo_link AS link SET "IsCurrent" = false, "ImportId" = @import
                WHERE link."IsCurrent" AND NOT EXISTS
                    (SELECT 1 FROM wikidata_crosswalk c WHERE c.qid = link."Qid" AND c.wfo_id = link."WfoId")
                RETURNING 1)
            SELECT count(*) FROM retired
            """, cancellationToken, import))[0];
        report.ItemsRetired = (await session.ScalarsAsync("""
            WITH retired AS (
                UPDATE reference.wikidata_item AS item SET "IsCurrent" = false, "CrosswalkImportId" = @import
                WHERE item."IsCurrent" AND NOT EXISTS (SELECT 1 FROM wikidata_crosswalk c WHERE c.qid = item."Qid")
                RETURNING 1)
            SELECT count(*) FROM retired
            """, cancellationToken, import))[0];
        await session.ExecuteAsync("ANALYZE reference.wikidata_item; ANALYZE reference.wikidata_wfo_link;", cancellationToken);
    }

    private static async Task<MultiValueSummary> MultiValueAsync(SourceImportSession session, string key, string value,
        CancellationToken cancellationToken)
    {
        var count = (await session.ScalarsAsync(
            $"SELECT count(*) FROM (SELECT 1 FROM wikidata_crosswalk GROUP BY {key} HAVING count(*) > 1) multiple", cancellationToken))[0];
        var examples = new List<MultiValueExample>();
        await using var command = session.Command($"""
            SELECT {key}, array_agg({value} ORDER BY length({value}), {value}) FROM wikidata_crosswalk
            GROUP BY {key} HAVING count(*) > 1 ORDER BY length({key}), {key} LIMIT {MaximumExamples}
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            examples.Add(new(reader.GetString(0), reader.GetFieldValue<string[]>(1)));
        return new(count, examples);
    }

    private static void Apply(SourceImport import, CrosswalkReport report)
    {
        import.RowsRead = report.RowsRead;
        import.RowsInserted = report.LinksInserted;
        import.RowsUpdated = report.LinksUpdated;
        import.RowsRetired = report.LinksRetired;
        import.WarningCount = report.WarningCount;
    }

    private static (long, long) Pair(long[] values) => (values[0], values[1]);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
