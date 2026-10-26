using System.Reflection;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SAAIA.Backend.Bootstrap;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DynamicRootCategoryRegressionTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(1, "indexed")]
    [InlineData(1, "missing")]
    [InlineData(3, "indexed")]
    [InlineData(3, "missing")]
    public async Task Scanner_bounded_inventory_never_infers_absence_of_a_present_pdf(int maximumFiles, string initialStatus)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var root = Path.Combine(Path.GetTempPath(), $"saaia-scanner-presence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var tenant = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
            foreach (var name in new[] { "first.pdf", "second.pdf" })
            {
                var path = Path.Combine(root, name);
                // Scanner admission examines bytes/hash, not a rendering engine.
                await File.WriteAllTextAsync(path, $"%PDF-1.4\nDistinct synthetic scanner source {name}\n");
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                var file = new FileInfo(path);
                await SeedExistingDocumentAsync(ds, tenant, name, string.Empty, file.Length, file.LastWriteTimeUtc,
                    status: initialStatus, missingSinceUtc: initialStatus == "missing" ? DateTime.UtcNow.AddMinutes(-10) : null);
            }

            await RunScannerWithInventoryLimitAsync(ds, root, tenant, maximumFiles);

            await using var connection = await ds.OpenConnectionAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM ingestion_jobs WHERE tenant_id=@tenant AND action='delete';", new { tenant }));
            var documents = (await connection.QueryAsync<(string Path, string Status, int IndexedVersion)>(
                "SELECT doc_path, status, indexed_version FROM documents WHERE tenant_id=@tenant ORDER BY doc_path;", new { tenant })).ToArray();
            Assert.Equal(2, documents.Length);
            Assert.All(documents, document =>
            {
                Assert.True(File.Exists(Path.Combine(root, document.Path)), "The omitted PDF still exists physically.");
                Assert.Equal("indexed", document.Status);
                Assert.Equal(1, document.IndexedVersion);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Scanner_complete_inventory_still_admits_a_proven_absent_pdf_after_grace()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var root = Path.Combine(Path.GetTempPath(), $"saaia-scanner-absent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var tenant = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
            var path = Path.Combine(root, "present.pdf");
            await File.WriteAllTextAsync(path, "%PDF-1.4\nDistinct present scanner control\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var file = new FileInfo(path);
            await SeedExistingDocumentAsync(ds, tenant, "present.pdf", string.Empty, file.Length, file.LastWriteTimeUtc);
            await SeedExistingDocumentAsync(ds, tenant, "truly-absent.pdf", string.Empty, 20, file.LastWriteTimeUtc,
                status: "missing", missingSinceUtc: DateTime.UtcNow.AddMinutes(-10));

            await RunScannerWithInventoryLimitAsync(ds, root, tenant, 3);

            await using var connection = await ds.OpenConnectionAsync();
            var paths = (await connection.QueryAsync<string>(
                "SELECT doc_path FROM ingestion_jobs WHERE tenant_id=@tenant AND action='delete';", new { tenant })).ToArray();
            Assert.Equal(new[] { "truly-absent.pdf" }, paths);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Scanner_rotating_batches_eventually_admit_both_distinct_sources()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var root = Path.Combine(Path.GetTempPath(), $"saaia-scanner-fairness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var tenant = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db!.ConnectionString);
            foreach (var name in new[] { "first.pdf", "second.pdf" })
            {
                var absolute = Path.Combine(root, name);
                await File.WriteAllTextAsync(absolute, $"%PDF-1.4\nDistinct content for scanner fairness {name}\n");
                var file = new FileInfo(absolute);
                await SeedExistingDocumentAsync(ds, tenant, name, string.Empty, file.Length - 1, file.LastWriteTimeUtc);
            }
            using var services = new ServiceCollection().BuildServiceProvider();
            using var scanner = new IngestionScanner(services, NullLogger<IngestionScanner>.Instance);
            await RunScannerWithInventoryLimitAsync(ds, root, tenant, 1, scanner);
            await RunScannerWithInventoryLimitAsync(ds, root, tenant, 1, scanner);
            await using var connection = await ds.OpenConnectionAsync();
            var admitted = (await connection.QueryAsync<string>(
                "SELECT doc_path FROM ingestion_jobs WHERE tenant_id=@tenant AND action='upsert' ORDER BY doc_path;", new { tenant })).ToArray();
            Assert.Equal(new[] { "first.pdf", "second.pdf" }, admitted);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM ingestion_jobs WHERE tenant_id=@tenant AND action='delete';", new { tenant }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RunScannerWithInventoryLimitAsync(NpgsqlDataSource source, string root, Guid tenant, int maximumFiles,
        IngestionScanner? suppliedScanner = null)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var ownedScanner = suppliedScanner is null ? new IngestionScanner(services, NullLogger<IngestionScanner>.Instance) : null;
        var scanner = suppliedScanner ?? ownedScanner!;
        var scan = typeof(IngestionScanner).GetMethod("ScanOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(scan);
        var task = (Task?)scan!.Invoke(scanner, new object[]
        {
            source, new StubHttpClientFactory(), new RagOptions(),
            new IngestionOptions { DocumentsRoot = root, MinFileAgeSeconds = 0, MaxFilesPerScan = maximumFiles,
                MissingGraceSeconds = 5, ReindexIfQdrantEmpty = false, DefaultCategory = "fallback-category" },
            new BootstrapOptions { TenantId = tenant }, new DocumentIntelligenceOptions(), CancellationToken.None
        });
        Assert.NotNull(task);
        await task!;
    }
}
