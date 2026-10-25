using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
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

    public static async Task ApplyMigrationsAsync(string connString, string migrationsDir, CancellationToken ct,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        using var startup = BackendDiagnostics.Start("startup.migrations", logger);
        await using var conn = new NpgsqlConnection(connString);
        var locked = false;
        try
        {
            await conn.OpenAsync(ct);
            // A session lock surrounds discovery/check/application; a per-migration transaction alone
            // does not stop two instances from observing the same unapplied version.
            using (var wait = BackendDiagnostics.Start("startup.migration_lock", logger))
            {
                try
                {
                    await using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(6000016801545177415)", conn) { CommandTimeout = 1800 };
                    await acquire.ExecuteNonQueryAsync(ct);
                    locked = true;
                    wait.Complete();
                }
                catch (Exception ex) { wait.Fail(ex); throw; }
            }

            await using (var cmd = new NpgsqlCommand(@"
CREATE TABLE IF NOT EXISTS schema_migrations (
  version text PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now(),
  checksum_sha256 text NULL,
  checksum_status text NOT NULL DEFAULT 'verified'
);
ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum_sha256 text NULL;
ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum_status text NOT NULL DEFAULT 'legacy_unverified';", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var versions = GetOrderedMigrationVersions(migrationsDir);
            startup.Set("discovered_migrations", versions.Count);
            var applied = 0;
            var baselined = 0;
            foreach (var version in versions)
            {
                using var migration = BackendDiagnostics.Start("startup.migration", logger);
                migration.Set("migration", version);
                try
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(migrationsDir, version), ct);
                    var checksum = Convert.ToHexString(SHA256.HashData(bytes));
                    migration.Set("checksum_sha256", checksum);
                    // Identity remains the complete filename, including legacy duplicated numeric prefixes.
                    await using var check = new NpgsqlCommand("SELECT checksum_sha256 FROM schema_migrations WHERE version=@v", conn);
                    check.Parameters.AddWithValue("v", version);
                    var previous = await check.ExecuteScalarAsync(ct);
                    if (previous is string recorded)
                    {
                        if (!string.Equals(recorded, checksum, StringComparison.Ordinal))
                            throw new InvalidOperationException($"migration_checksum_mismatch:{version}");
                        migration.Complete("skipped", "already_applied_checksum_verified");
                        continue;
                    }
                    if (previous is DBNull)
                    {
                        // We cannot reconstruct what an old installation executed. Declare the first
                        // observed baseline honestly, then detect all subsequent source modifications.
                        await using var baseline = new NpgsqlCommand("UPDATE schema_migrations SET checksum_sha256=@hash, checksum_status='legacy_baseline' WHERE version=@v", conn);
                        baseline.Parameters.AddWithValue("hash", checksum);
                        baseline.Parameters.AddWithValue("v", version);
                        await baseline.ExecuteNonQueryAsync(ct);
                        baselined++;
                        logger.LogWarning("Migration {Migration} checksum baseline established; historical applied content cannot be verified", version);
                        migration.Complete("degraded", "legacy_checksum_baselined");
                        continue;
                    }

                    var sql = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await using (var guard = new NpgsqlCommand("SET LOCAL lock_timeout = '15s'; SET LOCAL statement_timeout = '30min';", conn, tx))
                        await guard.ExecuteNonQueryAsync(ct);
                    await using (var run = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 1800 })
                        await run.ExecuteNonQueryAsync(ct);
                    await using (var insert = new NpgsqlCommand("INSERT INTO schema_migrations(version,checksum_sha256,checksum_status) VALUES(@v,@hash,'verified')", conn, tx))
                    {
                        insert.Parameters.AddWithValue("v", version);
                        insert.Parameters.AddWithValue("hash", checksum);
                        await insert.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                    applied++;
                    migration.Complete();
                }
                catch (Exception ex) { migration.Fail(ex); throw; }
            }
            startup.Set("applied_migrations", applied);
            startup.Set("legacy_baselines", baselined);
            await DatabaseTextSearchDiagnostics.ObserveAsync(conn, logger, ct);
            startup.Complete();
        }
        catch (Exception ex) { startup.Fail(ex); throw; }
        finally
        {
            if (locked && conn.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(6000016801545177415)", conn) { CommandTimeout = 5 };
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception ex) { logger.LogWarning("Migration lock release incomplete error_type={ErrorType}; connection disposal releases the session lock", ex.GetType().Name); }
            }
        }
    }
}
