using Dapper;
using Npgsql;
using SAAIA.Backend.Db;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class DbMigratorDiagnosticIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Concurrent_startups_apply_each_migration_once_under_a_global_lock()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync();
        using var migrations = new MigrationDirectory("CREATE TABLE concurrent_startup(value integer); INSERT INTO concurrent_startup VALUES(1); SELECT pg_sleep(0.1);");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => DbMigrator.ApplyMigrationsAsync(database.ConnectionString, migrations.Path, CancellationToken.None)));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM concurrent_startup"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal("verified", await connection.ExecuteScalarAsync<string>("SELECT checksum_status FROM schema_migrations"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Applied_migration_modification_is_rejected_before_the_changed_sql_runs()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync();
        using var migrations = new MigrationDirectory("CREATE TABLE integrity_guard(value integer);");
        await DbMigrator.ApplyMigrationsAsync(database.ConnectionString, migrations.Path, CancellationToken.None);
        await File.AppendAllTextAsync(migrations.MigrationPath, " INSERT INTO integrity_guard VALUES(99);");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DbMigrator.ApplyMigrationsAsync(database.ConnectionString, migrations.Path, CancellationToken.None));
        Assert.StartsWith("migration_checksum_mismatch:", error.Message);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM integrity_guard"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Legacy_migration_hash_is_explicitly_baselined_instead_of_claiming_historical_verification()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync();
        using var migrations = new MigrationDirectory("CREATE TABLE must_not_run(value integer);");
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("CREATE TABLE schema_migrations(version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now()); INSERT INTO schema_migrations(version) VALUES('001_fixture.sql')");
        await DbMigrator.ApplyMigrationsAsync(database.ConnectionString, migrations.Path, CancellationToken.None);
        Assert.Equal("legacy_baseline", await connection.ExecuteScalarAsync<string>("SELECT checksum_status FROM schema_migrations"));
        var checksum = await connection.ExecuteScalarAsync<string>("SELECT checksum_sha256 FROM schema_migrations");
        Assert.NotNull(checksum);
        Assert.Equal(64, checksum.Length);
        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('must_not_run')::text"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Failed_migration_rolls_back_ddl_and_never_records_a_successful_version()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync();
        using var migrations = new MigrationDirectory("CREATE TABLE rollback_guard(value integer); SELECT missing_diagnostic_test_function();");
        await Assert.ThrowsAsync<PostgresException>(() => DbMigrator.ApplyMigrationsAsync(database.ConnectionString, migrations.Path, CancellationToken.None));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('rollback_guard')::text"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }

    private sealed class MigrationDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "saaia-migration-diagnostic-" + Guid.NewGuid().ToString("N"));
        internal string MigrationPath => System.IO.Path.Combine(Path, "001_fixture.sql");
        internal MigrationDirectory(string sql) { Directory.CreateDirectory(Path); File.WriteAllText(MigrationPath, sql); }
        public void Dispose() { File.Delete(MigrationPath); Directory.Delete(Path); }
    }
}
