using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AnythingCanBeFarming.DataImport;

public sealed class WfoImporter(string connectionString, TextWriter output)
{
    private const long LockKey = 0x4143424657464F;
    private static readonly string ColumnList = string.Join(", ", WfoTsvReader.Columns.Select(Quote));

    public async Task<ImportResult> ImportAsync(string sourcePath, string? version, bool force,
        TextWriter diagnosticWriter, CancellationToken cancellationToken = default)
    {
        var report = new ImportReport();
        var diagnostics = new ImportDiagnostics(diagnosticWriter, report);
        // Keep this handle open through publication; Windows FileShare.Read prevents source mutation.
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        source.Position = 0;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>()
            .UseNpgsql(connection, options => options.CommandTimeout(0)).Options);
        await using var lockCommand = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", connection);
        if (!(bool)(await lockCommand.ExecuteScalarAsync(cancellationToken))!)
            throw new InvalidOperationException("Another WFO import is running. Try again after it finishes.");
        try
        {
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                throw new InvalidOperationException("Apply EF Core migrations before importing WFO; the importer does not migrate the database.");
            var prior = await db.WfoImports.AsNoTracking()
                .Where(x => x.DatasetKind == "Backbone" && x.SourceFileHash == hash && x.Status == "Succeeded")
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (prior != null && !force)
            {
                output.WriteLine($"Already imported: import {prior.Id}, version {prior.DatasetVersion ?? "(unknown)"}, SHA-256 {hash}. Use --force to reimport.");
                return new ImportResult(prior.Id, true, report);
            }
            // With the session lock held, any remaining Running entries were interrupted.
            await db.WfoImports.Where(x => x.Status == "Running").ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.Status, "Failed")
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.ErrorMessage, "Previous process ended before completing the import."), cancellationToken);
            var import = new WfoImport
            {
                SourceFileName = Path.GetFileName(sourcePath), SourceFileHash = hash,
                DatasetVersion = version, StartedAt = DateTimeOffset.UtcNow, Status = "Running"
            };
            db.WfoImports.Add(import);
            await db.SaveChangesAsync(cancellationToken);
            output.WriteLine($"Import {import.Id}: {import.SourceFileName}, version {version ?? "(unknown)"}, SHA-256 {hash}");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await ExecuteAsync(connection, $"""
                    CREATE TEMP TABLE wfo_stage ON COMMIT DROP AS
                    SELECT {ColumnList}, 0::bigint AS "SourceRow" FROM reference.wfo_taxon WITH NO DATA
                    """, cancellationToken);
                using (var reader = new StreamReader(source, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, 65536, leaveOpen: true))
                {
                    // Strip only a UTF-8 BOM; do not reinterpret UTF-16 input.
                    if (reader.Peek() == '\uFEFF') reader.Read();
                    using var copy = connection.BeginBinaryImport($"COPY wfo_stage ({ColumnList}, \"SourceRow\") FROM STDIN (FORMAT BINARY)");
                    copy.Timeout = TimeSpan.Zero;
                    foreach (var row in WfoTsvReader.Read(reader, report, diagnostics))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        copy.StartRow();
                        foreach (var value in row.Values)
                        {
                            if (value == null) copy.WriteNull();
                            else if (value is DateOnly date) copy.Write(date, NpgsqlDbType.Date);
                            else copy.Write((string)value, NpgsqlDbType.Text);
                        }
                        copy.Write(row.SourceRow, NpgsqlDbType.Bigint);
                        if (report.RowsRead % 100000 == 0) output.WriteLine($"Read {report.RowsRead:N0} records…");
                    }
                    copy.Complete();
                }
                // Detect concurrent edits on systems where file sharing is advisory.
                source.Position = 0;
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken)) != hash)
                    throw new InvalidDataException("Source file changed during import; snapshot was not published.");
                await ExecuteAsync(connection, "CREATE INDEX ON wfo_stage (\"TaxonId\"); ANALYZE wfo_stage;", cancellationToken);
                await ValidateAsync(connection, report, diagnostics, cancellationToken);
                if (report.RowsRejected > 0 || report.DuplicateTaxonIds > 0 || report.RowsStaged == 0)
                    throw new InvalidDataException("Snapshot was not published: rejected/duplicate records or empty input. See the diagnostic report.");

                output.WriteLine("Validation passed; publishing the snapshot and resolving relationships…");
                var updates = string.Join(", ", WfoTsvReader.Columns.Where(x => x != "TaxonId").Select(x => $"{Quote(x)} = EXCLUDED.{Quote(x)}"));
                await ExecuteAsync(connection, $"""
                    INSERT INTO reference.wfo_taxon ({ColumnList}, "ImportId", "IsCurrent")
                    SELECT {ColumnList}, {import.Id}, true FROM wfo_stage
                    ON CONFLICT ("TaxonId") DO UPDATE SET {updates},
                        "ImportId" = EXCLUDED."ImportId", "IsCurrent" = true;
                    UPDATE reference.wfo_taxon SET "IsCurrent" = false,
                        "ParentId" = NULL, "AcceptedTaxonId" = NULL, "OriginalTaxonId" = NULL
                    WHERE "IsCurrent" AND "ImportId" <> {import.Id};
                    ANALYZE reference.wfo_taxon;
                    """, cancellationToken);
                await ExecuteAsync(connection, """
                    UPDATE reference.wfo_taxon AS t
                    SET "ParentId" = p."Id", "AcceptedTaxonId" = a."Id", "OriginalTaxonId" = o."Id"
                    FROM wfo_stage s
                    LEFT JOIN reference.wfo_taxon p ON p."TaxonId" = s."ParentNameUsageId" AND p."IsCurrent"
                    LEFT JOIN reference.wfo_taxon a ON a."TaxonId" = s."AcceptedNameUsageId" AND a."IsCurrent"
                    LEFT JOIN reference.wfo_taxon o ON o."TaxonId" = s."OriginalNameUsageId" AND o."IsCurrent"
                    WHERE t."TaxonId" = s."TaxonId"
                      AND (t."ParentId", t."AcceptedTaxonId", t."OriginalTaxonId")
                          IS DISTINCT FROM (p."Id", a."Id", o."Id");
                    ANALYZE reference.wfo_taxon;
                    """, cancellationToken);
                await WfoSupplementalImporter.RefreshRelationshipsAsync(connection, cancellationToken);
                report.RowsImported = report.RowsStaged;
                Complete(import, report, "Succeeded", null);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                report.RowsImported = 0;
                var message = exception switch
                {
                    OperationCanceledException => "Import cancelled; snapshot changes rolled back.",
                    InvalidDataException => exception.Message,
                    PostgresException pg => $"PostgreSQL error {pg.SqlState}; snapshot changes rolled back.",
                    _ => $"Import failed ({exception.GetType().Name}); snapshot changes rolled back."
                };
                Complete(import, report, "Failed", message);
                await db.SaveChangesAsync(CancellationToken.None);
                output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidDataException($"Import {import.Id}: {message}", exception);
            }
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return new ImportResult(import.Id, false, report);
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
                await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private static void Complete(WfoImport import, ImportReport report, string status, string? error)
    {
        import.Status = status;
        import.CompletedAt = DateTimeOffset.UtcNow;
        import.RowsRead = report.RowsRead;
        import.RowsImported = report.RowsImported;
        import.RowsRejected = report.RowsRejected;
        import.WarningCount = report.WarningCount;
        import.ErrorMessage = error;
        import.ValidationJson = JsonSerializer.Serialize(report);
    }

    private static async Task ValidateAsync(NpgsqlConnection connection, ImportReport report,
        ImportDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        await using (var command = Command(connection, """
            SELECT count(DISTINCT "TaxonId"), count(DISTINCT "Family"), count(DISTINCT "Genus") FROM wfo_stage
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            report.UniqueTaxonIds = reader.GetInt64(0);
            report.UniqueFamilies = reader.GetInt64(1);
            report.UniqueGenera = reader.GetInt64(2);
        }
        await using (var command = Command(connection, """
            SELECT s."SourceRow", s."TaxonId" FROM wfo_stage s
            JOIN (SELECT "TaxonId" FROM wfo_stage GROUP BY "TaxonId" HAVING count(*) > 1) d
            ON s."TaxonId" = d."TaxonId" ORDER BY s."TaxonId", s."SourceRow"
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            string? last = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(1);
                if (id != last) report.DuplicateTaxonIds++;
                else report.DuplicateRows++;
                last = id;
                report.RowsRejected++;
                diagnostics.Issue(reader.GetInt64(0), "error", "taxonID", "Duplicate identifier; all conflicting rows prevent publication.", id);
            }
        }
        foreach (var column in new[] { "TaxonomicStatus", "NomenclaturalStatus", "TaxonRank", "MajorGroup" })
        {
            var groups = new List<ValueCount>();
            await using var command = Command(connection, $"SELECT {Quote(column)}, count(*) FROM wfo_stage GROUP BY {Quote(column)} ORDER BY {Quote(column)} NULLS FIRST");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                groups.Add(new ValueCount(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1)));
            report.Groups[column] = groups;
        }
        foreach (var column in new[] { "ParentNameUsageId", "AcceptedNameUsageId", "OriginalNameUsageId" })
        {
            long resolved = 0, unresolved = 0;
            // EXISTS avoids multiplying rows when diagnosing duplicates.
            await using var command = Command(connection, $"""
                SELECT s."SourceRow", s."TaxonId", s.{Quote(column)},
                    EXISTS (SELECT 1 FROM wfo_stage target WHERE target."TaxonId" = s.{Quote(column)})
                FROM wfo_stage s WHERE s.{Quote(column)} IS NOT NULL
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetBoolean(3)) resolved++;
                else
                {
                    unresolved++;
                    diagnostics.Issue(reader.GetInt64(0), "warning", column,
                        $"Referenced WFO identifier '{reader.GetString(2)}' is absent from this snapshot.", reader.GetString(1));
                }
            }
            report.Relationships[column] = new RelationshipCount(resolved, unresolved);
        }
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = 0 };
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
