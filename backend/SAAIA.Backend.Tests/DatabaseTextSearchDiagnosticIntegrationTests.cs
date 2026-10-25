using Dapper;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class DatabaseTextSearchDiagnosticIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Unicode_fixture_reports_behavior_without_creating_user_tables_or_changing_locale()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var before = await connection.QuerySingleAsync<Metadata>(MetadataSql);
        var logger = new DiagnosticRecordingLogger<DatabaseTextSearchDiagnosticIntegrationTests>();
        var result = await DatabaseTextSearchDiagnostics.ObserveAsync(connection, logger, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result.Compatible); // IntegrationPg's Unicode fixture is a prerequisite, not inferred from its locale name.
        var after = await connection.QuerySingleAsync<Metadata>(MetadataSql);
        Assert.Equal(before, after);
        var terminal = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var value)
            && Equals(value, "startup.database_text_search"));
        Assert.Equal("success", terminal.Values["Outcome"]);
    }

    [ProfileFact("IntegrationPg")]
    public async Task PostgreSQL16_C_parser_limitation_is_observed_and_reported_without_failing_startup()
    {
        await using var owner = await LocalPostgresDiagnosticFixture.CreateAsync();
        await using var admin = await owner.DataSource.OpenConnectionAsync();
        var name = "saaia_diag_unicode_" + Guid.NewGuid().ToString("N");
        var created = false;
        NpgsqlDataSource? source = null;
        try
        {
            await admin.ExecuteAsync($"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER libc LC_COLLATE 'C' LC_CTYPE 'C'");
            created = true;
            var builder = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Database = name };
            source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await source.OpenConnectionAsync();
            var before = await connection.QuerySingleAsync<Metadata>(MetadataSql);
            var logger = new DiagnosticRecordingLogger<DatabaseTextSearchDiagnosticIntegrationTests>();
            var result = await DatabaseTextSearchDiagnostics.ObserveAsync(connection, logger, CancellationToken.None);
            Assert.NotNull(result);
            Assert.False(result.PunctuationSeparated);
            Assert.False(result.Compatible);
            Assert.True(result.ConjunctionPreserved);
            Assert.True(result.AccentPreserved);
            Assert.Equal(before, await connection.QuerySingleAsync<Metadata>(MetadataSql));
            var terminal = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var value)
                && Equals(value, "startup.database_text_search"));
            Assert.Equal("degraded", terminal.Values["Outcome"]);
            Assert.Equal(DatabaseTextSearchDiagnostics.IncompatibleReason, terminal.Values["ReasonCode"]);
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
            if (created)
            {
                await admin.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=@name AND pid<>pg_backend_pid()", new { name });
                await admin.ExecuteAsync($"DROP DATABASE \"{name}\"");
            }
        }
    }

    private const string MetadataSql = """
        SELECT d.datctype AS CType, d.datcollate AS Collation,
          (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='public') AS UserTableCount
        FROM pg_catalog.pg_database d WHERE d.datname=current_database();
        """;
    private sealed record Metadata(string CType, string Collation, long UserTableCount);
}
