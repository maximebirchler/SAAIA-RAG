using System.Text.RegularExpressions;
using SAAIA.Backend.Db;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DbMigratorTests
{
    [Fact]
    public void Migration_versions_are_full_filenames_and_legacy_duplicate_prefixes_are_explicit()
    {
        var migrationsDir = ResolveMigrationsDir();
        var versions = DbMigrator.GetOrderedMigrationVersions(migrationsDir);

        Assert.Contains("004_documents_indexed_version_and_jobs_refactor.sql", versions);
        Assert.Contains("004_phase1_security.sql", versions);
        Assert.Contains("008_chat_messages_tracking.sql", versions);
        Assert.Contains("008_documents_catalog_index.sql", versions);

        Assert.DoesNotContain("004", versions);
        Assert.DoesNotContain("008", versions);
        var orderedVersions = versions.ToList();
        Assert.True(
            orderedVersions.IndexOf("004_documents_indexed_version_and_jobs_refactor.sql") <
            orderedVersions.IndexOf("004_phase1_security.sql"));
        Assert.True(
            orderedVersions.IndexOf("008_chat_messages_tracking.sql") <
            orderedVersions.IndexOf("008_documents_catalog_index.sql"));
    }

    [Fact]
    public void Migration_numeric_prefixes_allow_only_documented_legacy_duplicates()
    {
        var migrationsDir = ResolveMigrationsDir();
        var versions = DbMigrator.GetOrderedMigrationVersions(migrationsDir);
        var duplicatePrefixes = versions
            .Select(static version => Regex.Match(version, @"^\d+").Value)
            .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
            .GroupBy(static prefix => prefix, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static prefix => prefix, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["004", "008"], duplicatePrefixes);
    }

    private static string ResolveMigrationsDir()
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "SAAIA.Backend",
            "Db",
            "Migrations"));
}
