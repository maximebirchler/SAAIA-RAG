using System.Text;
using Npgsql;

namespace SAAIA.Backend.Db;

public static class DbMigrator
{
    public static IReadOnlyList<string> GetOrderedMigrationVersions(string migrationsDir)
    {
        if (!Directory.Exists(migrationsDir))
            throw new DirectoryNotFoundException($"Migrations dir not found: {migrationsDir}");

        return Directory.GetFiles(migrationsDir, "*.sql")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .Where(static version => !string.IsNullOrWhiteSpace(version))
            .Cast<string>()
            .ToArray();
    }

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

        var versions = GetOrderedMigrationVersions(migrationsDir);

        foreach (var version in versions)
        {
            var file = Path.Combine(migrationsDir, version);

            // The full filename is the migration identity. Legacy 004/008 duplicate numeric prefixes
            // are therefore safe as long as those files are not renamed after being applied.
            await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE version = @v", conn))
            {
                check.Parameters.AddWithValue("v", version);
                var exists = await check.ExecuteScalarAsync(ct);
                if (exists is not null) continue;
            }

            var sql = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var guard = new NpgsqlCommand(
                "SET LOCAL lock_timeout = '15s'; SET LOCAL statement_timeout = '30min';",
                conn,
                tx))
            {
                await guard.ExecuteNonQueryAsync(ct);
            }

            await using (var run = new NpgsqlCommand(sql, conn, tx))
            {
                // Some operational indexes are built during startup migrations and can
                // legitimately exceed the provider's default 30s command timeout.
                run.CommandTimeout = 1800;
                await run.ExecuteNonQueryAsync(ct);
            }

            await using (var ins = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES(@v)", conn, tx))
            {
                ins.Parameters.AddWithValue("v", version);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }
}
