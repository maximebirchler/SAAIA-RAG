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

    [Fact]
    public void Document_profile_search_projection_migration_materializes_indexable_search_text()
    {
        var migration = File.ReadAllText(Path.Combine(
            ResolveMigrationsDir(),
            "057_document_profile_search_entries.sql"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS document_profile_search_entries", migration, StringComparison.Ordinal);
        Assert.Contains("search_tsv tsvector", migration, StringComparison.Ordinal);
        Assert.Contains("GENERATED ALWAYS AS", migration, StringComparison.Ordinal);
        Assert.Contains("ix_document_profile_search_entries_tsv", migration, StringComparison.Ordinal);
        Assert.Contains("saaia_refresh_document_profile_search_entry", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_match_lookup_index_migration_replaces_large_text_btree_with_hash_and_trgm()
    {
        var migration = File.ReadAllText(Path.Combine(
            ResolveMigrationsDir(),
            "058_exact_match_lookup_index_hardening.sql"));

        Assert.Contains("DROP INDEX IF EXISTS ix_exact_match_entries_revision_normalized", migration, StringComparison.Ordinal);
        Assert.Contains("ix_exact_match_entries_revision_normalized_hash", migration, StringComparison.Ordinal);
        Assert.Contains("md5(normalized_text)", migration, StringComparison.Ordinal);
        Assert.Contains("ix_exact_match_entries_normalized_trgm", migration, StringComparison.Ordinal);
        Assert.Contains("gin_trgm_ops", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_profile_search_projection_recall_migration_removes_card_limit()
    {
        var baseMigration = File.ReadAllText(Path.Combine(
            ResolveMigrationsDir(),
            "057_document_profile_search_entries.sql"));
        var migration = File.ReadAllText(Path.Combine(
            ResolveMigrationsDir(),
            "059_document_profile_search_entries_full_card_recall.sql"));

        Assert.DoesNotContain("LIMIT 80", baseMigration, StringComparison.Ordinal);
        Assert.Contains("pg_get_functiondef", migration, StringComparison.Ordinal);
        Assert.Contains("saaia_refresh_document_profile_search_entry", migration, StringComparison.Ordinal);
        Assert.Contains("REPLACE", migration, StringComparison.Ordinal);
        Assert.Contains("LIMIT 80", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_profile_content_cards_migration_filters_unsafe_legacy_cards()
    {
        var migration = File.ReadAllText(Path.Combine(
            ResolveMigrationsDir(),
            "060_safe_profile_content_cards.sql"));

        Assert.Contains("saaia_is_safe_profile_content_card", migration, StringComparison.Ordinal);
        Assert.Contains("saaia_profile_content_card_has_grounded_evidence", migration, StringComparison.Ordinal);
        Assert.Contains("saaia_profile_content_card_has_technical_identifier", migration, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM document_profile_content_cards", migration, StringComparison.Ordinal);
        Assert.Contains("COALESCE(p.metadata, '{}'::jsonb) - 'contentCards' - 'contentCardCount'", migration, StringComparison.Ordinal);
        Assert.Contains("safe_profile_search_text", migration, StringComparison.Ordinal);
        Assert.Contains("SET search_text = safe_profile_search_text.search_text", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(cards.metadata_json, p.metadata)", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("profile_terms.search_text", migration, StringComparison.Ordinal);
        Assert.Contains("SELECT saaia_refresh_document_profile_search_entry", migration, StringComparison.Ordinal);
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
