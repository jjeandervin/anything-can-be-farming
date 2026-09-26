using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WikidataPostgresTests
{
    [PostgresFact]
    public async Task Migration_creates_wikidata_tables_and_trigram_indexes()
    {
        await using var database = await WfoPostgresTests.TestDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'"));
        foreach (var table in new[] { "source_import", "wikidata_item", "wikidata_wfo_link", "wikidata_external_id", "wikidata_common_name" })
            Assert.Equal(1L, await ScalarAsync(connection,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'reference' AND table_name = '{table}'"));

        foreach (var index in new[] { "IX_wfo_taxon_ScientificName_trgm", "IX_wfo_taxon_Genus_trgm", "IX_wikidata_common_name_NormalizedName_trgm" })
        {
            await using var command = new NpgsqlCommand(
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'reference' AND indexname = @name", connection);
            command.Parameters.AddWithValue("name", index);
            var definition = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(definition);
            Assert.Contains("USING gin", definition);
            Assert.Contains("gin_trgm_ops", definition);
        }

        await using var db = database.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await db.WikidataWfoLinks.CountAsync());
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
