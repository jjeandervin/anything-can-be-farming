using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnythingCanBeFarming.DataImport;

// Source-neutral run lifecycle for reference.source_import: one advisory lock per source,
// abandoned-run detection, and sanitized failure metadata.
public sealed class SourceImportSession : IAsyncDisposable
{
    private readonly string source;
    private readonly long lockKey;
    private bool locked;

    private SourceImportSession(string source, NpgsqlConnection connection, AcbfDbContext db)
    {
        this.source = source;
        lockKey = LockKey(source);
        Connection = connection;
        Db = db;
    }

    public NpgsqlConnection Connection { get; }
    public AcbfDbContext Db { get; }

    public static long LockKey(string source) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("acbf:source_import:" + source)));

    public static async Task<SourceImportSession> OpenAsync(string connectionString, string source, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        var db = new AcbfDbContext(new DbContextOptionsBuilder<AcbfDbContext>()
            .UseNpgsql(connection, options => options.CommandTimeout(0)).Options);
        var session = new SourceImportSession(source, connection, db);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using (var command = session.Command($"SELECT pg_try_advisory_lock({session.lockKey})"))
                session.locked = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (!session.locked)
                throw new InvalidOperationException($"Another {source} import is running. Try again after it finishes.");
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                throw new InvalidOperationException($"Apply EF Core migrations before importing {source}; the importer does not migrate the database.");
            // With the lock held, any Running row for this source belongs to a process that died.
            await db.SourceImports.Where(x => x.Source == source && x.Status == "Running").ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.Status, "Abandoned")
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.ErrorMessage, "Previous process ended before completing the import."), cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<SourceImport> StartAsync(string kind, object parameters, CancellationToken cancellationToken)
    {
        var import = new SourceImport
        {
            Source = source, Kind = kind, Status = "Running", StartedAt = DateTimeOffset.UtcNow,
            ParametersJson = JsonSerializer.Serialize(parameters)
        };
        Db.SourceImports.Add(import);
        await Db.SaveChangesAsync(cancellationToken);
        return import;
    }

    public async Task CompleteAsync(SourceImport import, string status, object report, string? error)
    {
        import.Status = status;
        import.CompletedAt = DateTimeOffset.UtcNow;
        import.ValidationJson = JsonSerializer.Serialize(report);
        import.ErrorMessage = error;
        // Write every column: after a rolled-back publication, EF's snapshot no longer matches the row.
        Db.Entry(import).State = EntityState.Modified;
        await Db.SaveChangesAsync(CancellationToken.None);
    }

    public static (string Status, string Message) Describe(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => ("Cancelled", "Import cancelled; unpublished changes rolled back."),
        InvalidDataException or InvalidOperationException or WikidataHttpException => ("Failed", exception.Message),
        PostgresException pg => ("Failed", $"PostgreSQL error {pg.SqlState}; unpublished changes rolled back."),
        _ => ("Failed", $"Import failed ({exception.GetType().Name}); unpublished changes rolled back.")
    };

    public NpgsqlCommand Command(string sql) => new(sql, Connection) { CommandTimeout = 0 };

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long[]> ScalarsAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return [];
        var values = new long[reader.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i));
        return values;
    }

    public async ValueTask DisposeAsync()
    {
        if (locked && Connection.State == System.Data.ConnectionState.Open)
        {
            await using var command = Command($"SELECT pg_advisory_unlock({lockKey})");
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        await Db.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
