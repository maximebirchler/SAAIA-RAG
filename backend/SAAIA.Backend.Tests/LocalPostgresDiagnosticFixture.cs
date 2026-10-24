using Dapper;
using Npgsql;
using SAAIA.Backend.Db;

namespace SAAIA.Backend.Tests;

/// <summary>Always creates a randomly named database on an explicitly configured loopback fixture.</summary>
internal sealed class LocalPostgresDiagnosticFixture : IAsyncDisposable
{
    private readonly string _adminConnection;
    private readonly string _databaseName;
    internal string ConnectionString { get; }
    internal NpgsqlDataSource DataSource { get; }

    private LocalPostgresDiagnosticFixture(string adminConnection, string databaseName, string connectionString)
    {
        _adminConnection = adminConnection; _databaseName = databaseName; ConnectionString = connectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    internal static async Task<LocalPostgresDiagnosticFixture> CreateAsync(bool migrateProduct = false)
    {
        var configured = Environment.GetEnvironmentVariable("SAAIA_TEST_PG_CONN");
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("IntegrationPg requires SAAIA_TEST_PG_CONN; no SQL test was executed.");
        var builder = new NpgsqlConnectionStringBuilder(configured);
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Diagnostic SQL tests require a loopback PostgreSQL fixture.");
        var name = "saaia_diag_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(configured))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE \"{name}\"");
        }
        builder.Database = name;
        var fixture = new LocalPostgresDiagnosticFixture(configured, name, builder.ConnectionString);
        try
        {
            if (migrateProduct)
                await DbMigrator.ApplyMigrationsAsync(fixture.ConnectionString,
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SAAIA.Backend", "Db", "Migrations")), CancellationToken.None);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        using var pooledConnection = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(pooledConnection);
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await connection.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=@name AND pid <> pg_backend_pid()", new { name = _databaseName });
        await connection.ExecuteAsync($"DROP DATABASE \"{_databaseName}\"");
    }
}
