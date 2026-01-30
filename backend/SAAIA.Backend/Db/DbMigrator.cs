using System.Text;
using Npgsql;

namespace SAAIA.Backend.Db;

public static class DbMigrator
{
    public static async Task ApplyMigrationsAsync(string connString, string migrationsDir, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);

        // Ensure schema_migrations exists
        await using (var cmd = new NpgsqlCommand(@"
CREATE TABLE IF NOT EXISTS schema_migrations (
  version text PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now()
);", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!Directory.Exists(migrationsDir))
            throw new DirectoryNotFoundException($"Migrations dir not found: {migrationsDir}");

        var files = Directory.GetFiles(migrationsDir, "*.sql")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in files)
        {
            var version = Path.GetFileName(file);

            // already applied?
            await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE version = @v", conn))
            {
                check.Parameters.AddWithValue("v", version);
                var exists = await check.ExecuteScalarAsync(ct);
                if (exists is not null) continue;
            }

            var sql = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var run = new NpgsqlCommand(sql, conn, tx))
                await run.ExecuteNonQueryAsync(ct);

            await using (var ins = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES(@v)", conn, tx))
            {
                ins.Parameters.AddWithValue("v", version);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }
}
