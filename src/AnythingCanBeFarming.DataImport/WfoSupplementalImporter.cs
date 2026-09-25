using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed record SupplementalResult(long ImportId, bool AlreadyImported, SupplementalReport Report);

public sealed class WfoSupplementalImporter(string connectionString, TextWriter output)
{
    // Shared with the backbone importer: relationship refreshes and publication cannot overlap.
    private const long LockKey = 0x4143424657464F;

    public async Task<SupplementalResult> ImportAsync(string path, WfoSupplementalKind kind, string? version,
        bool force = false, CancellationToken cancellationToken = default)
    {
        var report = new SupplementalReport();
        var (table, columns, keys) = Definition(kind);
        var columnList = string.Join(", ", columns.Select(Quote));
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        source.Position = 0;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>()
            .UseNpgsql(connection, options => options.CommandTimeout(0)).Options);
        await using var locking = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", connection);
        if (!(bool)(await locking.ExecuteScalarAsync(cancellationToken))!)
            throw new InvalidOperationException("Another WFO import is running. Try again after it finishes.");
        try
        {
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                throw new InvalidOperationException("Apply EF Core migrations before importing WFO.");
            var datasetKind = kind.ToString();
            var prior = await db.WfoImports.AsNoTracking().Where(x => x.DatasetKind == datasetKind &&
                x.SourceFileHash == hash && x.Status == "Succeeded").OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (prior != null && !force)
            {
                output.WriteLine($"Already imported {kind}: import {prior.Id}, SHA-256 {hash}. Use --force to reimport.");
                output.WriteLine(prior.ValidationJson);
                return new(prior.Id, true, report);
            }
            await db.WfoImports.Where(x => x.Status == "Running").ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.Status, "Failed").SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.ErrorMessage, "Previous process ended before completing the import."), cancellationToken);
            var import = new WfoImport
            {
                DatasetKind = datasetKind, SourceFileName = Path.GetFileName(path), SourceFileHash = hash,
                DatasetVersion = version, StartedAt = DateTimeOffset.UtcNow, Status = "Running"
            };
            db.WfoImports.Add(import);
            await db.SaveChangesAsync(cancellationToken);
            output.WriteLine($"Import {import.Id}: {import.SourceFileName}, release {version ?? "(unknown)"}, SHA-256 {hash}");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await ExecuteAsync(connection, $"CREATE TEMP TABLE supplemental_stage ON COMMIT DROP AS SELECT {columnList} FROM reference.{table} WITH NO DATA", cancellationToken);
                using (var reader = new StreamReader(source, new UTF8Encoding(false, true), false, 65536, leaveOpen: true))
                using (var copy = connection.BeginBinaryImport($"COPY supplemental_stage ({columnList}) FROM STDIN (FORMAT BINARY)"))
                {
                    copy.Timeout = TimeSpan.Zero;
                    foreach (var row in WfoSupplementalSource.Read(reader, kind, report))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        copy.StartRow();
                        foreach (var value in row)
                            if (value == null) copy.WriteNull(); else copy.Write(value, NpgsqlDbType.Text);
                        if (report.RowsRead % 250000 == 0) output.WriteLine($"Read {report.RowsRead:N0} {kind} rows...");
                    }
                    copy.Complete();
                }
                source.Position = 0;
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken)) != hash)
                    throw new InvalidDataException("Source changed during import; changes rolled back.");
                if (report.RowsRead == 0) throw new InvalidDataException("Empty supplemental file; changes rolled back.");
                var keyList = string.Join(", ", keys.Select(Quote));
                var duplicates = await ScalarAsync(connection, $"SELECT coalesce(sum(n), 0)::bigint FROM (SELECT count(*) n FROM supplemental_stage GROUP BY {keyList} HAVING count(*) > 1) d", cancellationToken);
                report.RowsRejected += duplicates;
                if (duplicates > 0) throw new InvalidDataException($"{duplicates} rows have duplicate {keyList} keys; changes rolled back.");
                var updates = string.Join(", ", columns.Except(keys).Select(x => $"{Quote(x)} = EXCLUDED.{Quote(x)}"));
                await ExecuteAsync(connection, $"""
                    INSERT INTO reference.{table} ({columnList}, "ImportId")
                    SELECT {columnList}, {import.Id} FROM supplemental_stage
                    ON CONFLICT ({keyList}) DO UPDATE SET {updates}, "ImportId" = EXCLUDED."ImportId";
                    ANALYZE reference.{table};
                    """, cancellationToken);
                await RefreshRelationshipsAsync(connection, cancellationToken);
                await ValidateAsync(connection, kind, report, cancellationToken);
                report.RowsImported = report.RowsRead;
                Complete(import, report, "Succeeded", null);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                report.RowsImported = 0;
                var message = exception is InvalidDataException ? exception.Message :
                    $"Supplemental import failed ({exception.GetType().Name}); changes rolled back.";
                Complete(import, report, "Failed", message);
                await db.SaveChangesAsync(CancellationToken.None);
                output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidDataException($"Import {import.Id}: {message}", exception);
            }
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return new(import.Id, false, report);
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
                await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    public static async Task RefreshRelationshipsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var (table, raw, fk) in new[]
        {
            ("wfo_ipni_mapping", "WfoId", "WfoTaxonId"),
            ("wfo_deprecated_name", "WfoId", "WfoTaxonId"),
            ("wfo_deduplicated_id", "DeprecatedWfoId", "DeprecatedTaxonId"),
            ("wfo_deduplicated_id", "ReplacementWfoId", "ReplacementTaxonId")
        })
            await ExecuteAsync(connection, $"""
                UPDATE reference.{table} AS target SET {Quote(fk)} = t."Id"
                FROM reference.{table} AS s LEFT JOIN reference.wfo_taxon t
                    ON t."TaxonId" = s.{Quote(raw)} AND t."IsCurrent"
                WHERE target."Id" = s."Id" AND target.{Quote(fk)} IS DISTINCT FROM t."Id";
                """, cancellationToken);
    }

    private static async Task ValidateAsync(NpgsqlConnection connection, WfoSupplementalKind kind,
        SupplementalReport report, CancellationToken cancellationToken)
    {
        var (table, _, _) = Definition(kind);
        var expressions = kind switch
        {
            WfoSupplementalKind.Ipni => new Dictionary<string, string>
            {
                ["TotalIpniMappings"] = "count(*)", ["DistinctIpniIds"] = "count(DISTINCT \"IpniId\")",
                ["DistinctWfoIds"] = "count(DISTINCT \"WfoId\")",
                ["WfoIdsResolved"] = "count(DISTINCT \"WfoId\") FILTER (WHERE \"WfoTaxonId\" IS NOT NULL)",
                ["WfoIdsNotFound"] = "count(DISTINCT \"WfoId\") FILTER (WHERE \"WfoTaxonId\" IS NULL)"
            },
            WfoSupplementalKind.Deprecated => new Dictionary<string, string>
            {
                ["TotalDeprecatedNames"] = "count(*)", ["DistinctDeprecatedWfoIds"] = "count(DISTINCT \"WfoId\")",
                ["DeprecatedIdsInBackbone"] = "count(\"WfoTaxonId\")",
                ["DeprecatedIdsAbsent"] = "count(*) FILTER (WHERE \"WfoTaxonId\" IS NULL)"
            },
            _ => new Dictionary<string, string>
            {
                ["TotalDeduplicationMappings"] = "count(*)", ["ReplacementIdsResolved"] = "count(\"ReplacementTaxonId\")",
                ["ReplacementIdsUnresolved"] = "count(*) FILTER (WHERE \"ReplacementTaxonId\" IS NULL)",
                ["DeprecatedIdsInBackbone"] = "count(\"DeprecatedTaxonId\")",
                ["SelfReferencingMappings"] = "count(*) FILTER (WHERE \"DeprecatedWfoId\" = \"ReplacementWfoId\")"
            }
        };
        await using (var command = Command(connection, $"SELECT {string.Join(", ", expressions.Values)} FROM reference.{table}"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            var index = 0;
            foreach (var key in expressions.Keys) report.Statistics[key] = reader.GetInt64(index++);
        }
        if (kind != WfoSupplementalKind.Deduplicated) return;
        // Check the merged graph, including previously imported edges. Traversal is bounded.
        await using var graph = Command(connection, $"""
            WITH RECURSIVE walk AS (
                SELECT "DeprecatedWfoId" AS start, "ReplacementWfoId" AS node,
                    ARRAY["DeprecatedWfoId"] AS visited, 1 AS depth,
                    "DeprecatedWfoId" = "ReplacementWfoId" AS cycle
                FROM reference.wfo_deduplicated_id
                UNION ALL
                SELECT w.start, d."ReplacementWfoId", w.visited || w.node, w.depth + 1,
                    d."ReplacementWfoId" = ANY(w.visited || w.node)
                FROM walk w JOIN reference.wfo_deduplicated_id d ON d."DeprecatedWfoId" = w.node
                WHERE NOT w.cycle AND w.depth < {WfoIdResolver.MaximumDepth}
            )
            SELECT count(DISTINCT start) FILTER (WHERE cycle),
                count(DISTINCT start) FILTER (WHERE NOT cycle AND depth = {WfoIdResolver.MaximumDepth}
                    AND EXISTS (SELECT 1 FROM reference.wfo_deduplicated_id d WHERE d."DeprecatedWfoId" = node))
            FROM walk
            """);
        await using var graphReader = await graph.ExecuteReaderAsync(cancellationToken);
        await graphReader.ReadAsync(cancellationToken);
        report.Statistics["MappingsReachingCycles"] = graphReader.GetInt64(0);
        report.Statistics["ChainsExceedingDepthLimit"] = graphReader.GetInt64(1);
        if (graphReader.GetInt64(0) > 0 || graphReader.GetInt64(1) > 0)
        {
            report.RowsRejected = report.RowsRead;
            report.Warnings.Add("Invalid replacement graph: cycles, self references, or excessively deep chains. Publication rejected.");
            throw new InvalidDataException("Deduplication graph contains cycles/self references or chains exceeding 64 replacements; changes rolled back.");
        }
    }

    private static void Complete(WfoImport import, SupplementalReport report, string status, string? error)
    {
        import.Status = status;
        import.CompletedAt = DateTimeOffset.UtcNow;
        import.RowsRead = report.RowsRead;
        import.RowsImported = report.RowsImported;
        import.RowsRejected = report.RowsRejected;
        import.WarningCount = report.Warnings.Count;
        import.ErrorMessage = error;
        import.ValidationJson = JsonSerializer.Serialize(report);
    }

    private static (string Table, string[] Columns, string[] Keys) Definition(WfoSupplementalKind kind) => kind switch
    {
        WfoSupplementalKind.Ipni => ("wfo_ipni_mapping", ["IpniId", "WfoId", "NormalizedIpniId"], ["IpniId", "WfoId"]),
        WfoSupplementalKind.Deprecated => ("wfo_deprecated_name", ["WfoId", "CanonicalName", "AuthorsString", "Rank", "NomenclaturalStatus"], ["WfoId"]),
        WfoSupplementalKind.Deduplicated => ("wfo_deduplicated_id", ["DeprecatedWfoId", "ReplacementWfoId", "CanonicalName", "AuthorsString", "Rank", "NomenclaturalStatus"], ["DeprecatedWfoId"]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static string Quote(string name) => $"\"{name}\"";
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = 0 };
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
