using System.Reflection;
using System.Net.Http;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SAAIA.Backend.Bootstrap;
using SAAIA.Backend.Db;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DynamicRootCategoryRegressionTests
{
    [Fact]
    public void Derive_returns_empty_category_for_root_document()
    {
        var options = new IngestionOptions
        {
            CategoryFromFirstFolder = true,
            DefaultCategory = "fallback-category"
        };

        var category = IngestionCategoryResolver.Derive("root-document.pdf", options);

        Assert.Equal(string.Empty, category);
    }

    [Theory]
    [InlineData("TopFolder/document.pdf")]
    [InlineData(@"TopFolder\Nested\document.pdf")]
    public void Derive_returns_first_folder_for_nested_document(string docPath)
    {
        var options = new IngestionOptions
        {
            CategoryFromFirstFolder = true,
            DefaultCategory = string.Empty
        };

        var category = IngestionCategoryResolver.Derive(docPath, options);

        Assert.Equal("topfolder", category);
    }

    [Fact]
    public async Task Scanner_enqueues_upsert_when_only_root_category_changes()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"saaia-category-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            const string docPath = "root-document.pdf";
            var filePath = Path.Combine(root, docPath);
            await File.WriteAllBytesAsync(filePath, "%PDF-1.4\n"u8.ToArray());

            var fixedMtimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, fixedMtimeUtc);

            var file = new FileInfo(filePath);
            var tenantId = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await SeedExistingDocumentAsync(ds, tenantId, docPath, "legacy-root", file.Length, fixedMtimeUtc);

            await RunScannerOnceAsync(ds, root, tenantId);

            await using var conn = await ds.OpenConnectionAsync();
            var document = await conn.QuerySingleAsync<DocumentState>(
                """
SELECT
  category AS "Category",
  file_size AS "FileSize",
  file_mtime AS "FileMtime",
  ingestion_version AS "IngestionVersion"
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = docPath });

            Assert.Equal(string.Empty, document.Category);
            Assert.Equal(file.Length, document.FileSize);
            Assert.True(SameMtime(fixedMtimeUtc, document.FileMtime));
            Assert.Equal(2, document.IngestionVersion);

            var job = await conn.QuerySingleAsync<QueuedJobState>(
                """
SELECT
  category AS "Category",
  action AS "Action",
  status AS "Status"
FROM ingestion_jobs
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = docPath });

            Assert.Equal(string.Empty, job.Category);
            Assert.Equal("upsert", job.Action);
            Assert.Equal("queued", job.Status);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp files created by this test.
            }
        }
    }

    [Fact]
    public async Task Scanner_requeues_stale_running_job_without_marking_failed()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"saaia-stale-running-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            const string docPath = "stale-document.pdf";
            var filePath = Path.Combine(root, docPath);
            await File.WriteAllBytesAsync(filePath, "%PDF-1.4\n"u8.ToArray());

            var fixedMtimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, fixedMtimeUtc);

            var file = new FileInfo(filePath);
            var tenantId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await SeedExistingDocumentAsync(ds, tenantId, docPath, string.Empty, file.Length, fixedMtimeUtc);
            await SeedStaleRunningJobAsync(ds, tenantId, jobId, docPath, TimeSpan.FromMinutes(30));

            await RunScannerOnceAsync(ds, root, tenantId);

            await using var conn = await ds.OpenConnectionAsync();
            var job = await conn.QuerySingleAsync<IngestionJobState>(
                """
SELECT
  status AS "Status",
  last_error AS "LastError",
  locked_by AS "LockedBy",
  locked_at AS "LockedAt",
  finished_at AS "FinishedAt"
FROM ingestion_jobs
WHERE tenant_id=@tenant_id AND job_id=@job_id;
""",
                new { tenant_id = tenantId, job_id = jobId });

            Assert.Equal("queued", job.Status);
            Assert.Equal("requeued_stale_running", job.LastError);
            Assert.Null(job.LockedBy);
            Assert.Null(job.LockedAt);
            Assert.Null(job.FinishedAt);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp files created by this test.
            }
        }
    }

    [Fact]
    public async Task Scanner_suppresses_duplicate_pdf_content_and_queues_delete_for_known_duplicate()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"saaia-duplicate-scan-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Alpha", "PDF"));

        try
        {
            const string canonicalDocPath = "Alpha/guide.pdf";
            const string duplicateDocPath = "Alpha/PDF/guide.pdf";
            var canonicalFilePath = Path.Combine(root, "Alpha", "guide.pdf");
            var duplicateFilePath = Path.Combine(root, "Alpha", "PDF", "guide.pdf");
            var bytes = "%PDF-1.4\nsame-content\n"u8.ToArray();
            await File.WriteAllBytesAsync(canonicalFilePath, bytes);
            await File.WriteAllBytesAsync(duplicateFilePath, bytes);

            var fixedMtimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(canonicalFilePath, fixedMtimeUtc);
            File.SetLastWriteTimeUtc(duplicateFilePath, fixedMtimeUtc);

            var tenantId = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await SeedExistingDocumentAsync(ds, tenantId, canonicalDocPath, "alpha", bytes.Length, fixedMtimeUtc);
            await SeedExistingDocumentAsync(ds, tenantId, duplicateDocPath, "alpha", bytes.Length, fixedMtimeUtc);

            await RunScannerOnceAsync(ds, root, tenantId);

            await using var conn = await ds.OpenConnectionAsync();
            var canonicalStatus = await conn.ExecuteScalarAsync<string>(
                """
SELECT status
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = canonicalDocPath });
            var duplicateStatus = await conn.ExecuteScalarAsync<string>(
                """
SELECT status
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = duplicateDocPath });

            Assert.Equal("indexed", canonicalStatus);
            Assert.Equal("missing", duplicateStatus);

            var deleteJob = await conn.QuerySingleAsync<QueuedJobState>(
                """
SELECT
  category AS "Category",
  action AS "Action",
  status AS "Status"
FROM ingestion_jobs
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = duplicateDocPath });

            Assert.Null(deleteJob.Category);
            Assert.Equal("delete", deleteJob.Action);
            Assert.Equal("queued", deleteJob.Status);

            var canonicalJobCount = await conn.ExecuteScalarAsync<int>(
                """
SELECT count(*)::int
FROM ingestion_jobs
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = canonicalDocPath });
            Assert.Equal(0, canonicalJobCount);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp files created by this test.
            }
        }
    }

    [Fact]
    public async Task Scanner_suppresses_duplicate_pdf_content_without_delete_when_duplicate_is_already_clean()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"saaia-duplicate-clean-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Alpha", "PDF"));

        try
        {
            const string canonicalDocPath = "Alpha/guide.pdf";
            const string duplicateDocPath = "Alpha/PDF/guide.pdf";
            var canonicalFilePath = Path.Combine(root, "Alpha", "guide.pdf");
            var duplicateFilePath = Path.Combine(root, "Alpha", "PDF", "guide.pdf");
            var bytes = "%PDF-1.4\nsame-content\n"u8.ToArray();
            await File.WriteAllBytesAsync(canonicalFilePath, bytes);
            await File.WriteAllBytesAsync(duplicateFilePath, bytes);

            var fixedMtimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(canonicalFilePath, fixedMtimeUtc);
            File.SetLastWriteTimeUtc(duplicateFilePath, fixedMtimeUtc);

            var tenantId = Guid.NewGuid();
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await SeedExistingDocumentAsync(ds, tenantId, canonicalDocPath, "alpha", bytes.Length, fixedMtimeUtc);
            await SeedExistingDocumentAsync(
                ds,
                tenantId,
                duplicateDocPath,
                "alpha",
                bytes.Length,
                fixedMtimeUtc,
                status: "missing",
                indexedVersion: 0,
                missingSinceUtc: fixedMtimeUtc);

            await RunScannerOnceAsync(ds, root, tenantId);

            await using var conn = await ds.OpenConnectionAsync();
            var duplicateState = await conn.QuerySingleAsync<(string Status, int IndexedVersion)>(
                """
SELECT status AS "Status", COALESCE(indexed_version, 0) AS "IndexedVersion"
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = duplicateDocPath });

            Assert.Equal("missing", duplicateState.Status);
            Assert.Equal(0, duplicateState.IndexedVersion);

            var duplicateJobCount = await conn.ExecuteScalarAsync<int>(
                """
SELECT count(*)::int
FROM ingestion_jobs
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""",
                new { tenant_id = tenantId, doc_path = duplicateDocPath });
            Assert.Equal(0, duplicateJobCount);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp files created by this test.
            }
        }
    }

    private static async Task RunScannerOnceAsync(NpgsqlDataSource ds, string root, Guid tenantId)
    {
        var scanner = new IngestionScanner(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<IngestionScanner>.Instance);

        var scanOnce = typeof(IngestionScanner).GetMethod(
            "ScanOnceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(scanOnce);

        var task = (Task?)scanOnce!.Invoke(scanner, new object[]
        {
            ds,
            new StubHttpClientFactory(),
            new RagOptions(),
            new IngestionOptions
            {
                DocumentsRoot = root,
                CategoryFromFirstFolder = true,
                DefaultCategory = "fallback-category",
                MinFileAgeSeconds = 0,
                ReindexIfQdrantEmpty = false
            },
            new BootstrapOptions { TenantId = tenantId },
            CancellationToken.None
        });

        Assert.NotNull(task);
        await task!;
    }

    private static async Task SeedExistingDocumentAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string docPath,
        string category,
        long fileSize,
        DateTime fileMtimeUtc,
        string status = "indexed",
        int ingestionVersion = 1,
        int indexedVersion = 1,
        DateTime? missingSinceUtc = null)
    {
        await using var conn = await ds.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name, is_active)
VALUES(@tenant_id, 'category-regression', true)
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category,
  status, created_at, updated_at, file_size, file_mtime,
  last_seen_at, missing_since, ingestion_version, indexed_version,
  auto_ingest_paused, auto_ingest_paused_at, auto_ingest_pause_reason
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, @category,
  @status, now(), now(), @file_size, @file_mtime,
  now(), @missing_since, @ingestion_version, @indexed_version,
  false, NULL, NULL
);
""",
            new
            {
                tenant_id = tenantId,
                doc_id = Guid.NewGuid(),
                doc_path = docPath,
                doc_name = Path.GetFileName(docPath),
                category,
                file_size = fileSize,
                file_mtime = DateTime.SpecifyKind(fileMtimeUtc, DateTimeKind.Utc),
                status,
                missing_since = missingSinceUtc.HasValue
                    ? DateTime.SpecifyKind(missingSinceUtc.Value, DateTimeKind.Utc)
                    : (DateTime?)null,
                ingestion_version = ingestionVersion,
                indexed_version = indexedVersion
            });
    }

    private static async Task SeedStaleRunningJobAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid jobId,
        string docPath,
        TimeSpan lockedAgo)
    {
        await using var conn = await ds.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
INSERT INTO ingestion_jobs(
  job_id, tenant_id, action, doc_path, category, status,
  attempts, locked_by, locked_at, available_at, created_at, started_at, payload
)
VALUES(
  @job_id, @tenant_id, 'upsert', @doc_path, '', 'running',
  1, 'stale-worker', now() - (@locked_ago_seconds * interval '1 second'), now(), now(), now(), '{}'::jsonb
);
""",
            new
            {
                job_id = jobId,
                tenant_id = tenantId,
                doc_path = docPath,
                locked_ago_seconds = (int)Math.Ceiling(lockedAgo.TotalSeconds)
            });
    }

    private static bool SameMtime(DateTime expectedUtc, DateTime actual)
    {
        var actualUtc = actual.Kind == DateTimeKind.Utc
            ? actual
            : DateTime.SpecifyKind(actual, DateTimeKind.Utc);

        return Math.Abs((expectedUtc - actualUtc).TotalSeconds) <= 2.0;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class DocumentState
    {
        public string Category { get; set; } = "";
        public long FileSize { get; set; }
        public DateTime FileMtime { get; set; }
        public int IngestionVersion { get; set; }
    }

    private sealed class QueuedJobState
    {
        public string Category { get; set; } = "";
        public string Action { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed class IngestionJobState
    {
        public string Status { get; set; } = "";
        public string? LastError { get; set; }
        public string? LockedBy { get; set; }
        public DateTimeOffset? LockedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
    }

    private sealed class PostgresIntegrationDb : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        public string ConnectionString { get; }

        private PostgresIntegrationDb(string adminConnectionString, string databaseName, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public static async Task<PostgresIntegrationDb?> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable("SAAIA_TEST_PG_CONN");
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                return null;

            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
            var adminConnectionString = adminBuilder.ConnectionString;
            var databaseName = $"saaia_category_{Guid.NewGuid():N}";

            await using (var adminConn = new NpgsqlConnection(adminConnectionString))
            {
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync($"CREATE DATABASE \"{databaseName}\";");
            }

            var dbBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName
            };

            var db = new PostgresIntegrationDb(adminConnectionString, databaseName, dbBuilder.ConnectionString);
            var migrationsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SAAIA.Backend", "Db", "Migrations"));
            await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, CancellationToken.None);
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var adminConn = new NpgsqlConnection(_adminConnectionString);
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync(
                    $@"SELECT pg_terminate_backend(pid)
                       FROM pg_stat_activity
                       WHERE datname = '{_databaseName}'
                         AND pid <> pg_backend_pid();");
                await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\";");
            }
            catch
            {
                // Best-effort cleanup for optional integration tests.
            }
        }
    }
}
