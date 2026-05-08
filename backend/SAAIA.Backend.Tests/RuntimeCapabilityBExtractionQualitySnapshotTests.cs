using Dapper;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Db;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RuntimeCapabilityBExtractionQualitySnapshotTests
{
    [Fact]
    public async Task LoadCapabilityBDocumentAsync_loads_revision_extraction_quality_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var payload = """
        {
          "extractionSource": "pdf_text",
          "ocrAttempted": true,
          "ocrApplied": false,
          "ocrDiagnostics": {
            "failureReason": "ocr_failed",
            "imagePageDiagnostics": [
              { "pageNumber": 1, "status": "ocr_failed" }
            ]
          },
          "extractionQuality": {
            "pageCount": 3,
            "textPageCount": 1,
            "emptyPageCount": 1,
            "sparsePageCount": 2,
            "totalWordCount": 12,
            "totalCharCount": 80,
            "textPageRatio": 0.3333,
            "textStatus": "low_text",
            "ocrRecommended": true,
            "signals": ["low_average_words_per_page", "ocr_recommended"]
          }
        }
        """;

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Capability B extraction quality tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, page_count,
  auto_ingest_paused, auto_ingest_pause_reason
)
VALUES(
  @tenant, @docId, 'OCR/low-text-ocr-failed.pdf', 'low-text-ocr-failed.pdf', 'ocr', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('ab', 32), 'hex'), 4096, now(), 3,
  true, 'manual_review'
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, 'OCR/low-text-ocr-failed.pdf',
  decode(repeat('ab', 32), 'hex'), 4096, now(), 1, 1, now()
);

INSERT INTO document_processing_runs(
  processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id,
  action, status, ingestion_version, indexed_version_before,
  indexed_version_after, source_hash, started_at, finished_at, payload
)
VALUES(
  @processingRunId, @tenant, @jobId, @docId, 'OCR/low-text-ocr-failed.pdf', @revisionId,
  'upsert', 'done', 1, 0,
  1, decode(repeat('ab', 32), 'hex'), now(), now(), @payload::jsonb
);
""",
            new
            {
                tenant = tenantId,
                docId,
                revisionId,
                processingRunId = Guid.NewGuid(),
                jobId = Guid.NewGuid(),
                payload
            });

        var doc = await RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);

        Assert.NotNull(doc);
        Assert.NotNull(doc!.ExtractionQuality);
        Assert.Equal("ocr_failed_or_insufficient", doc.ExtractionQuality!.Status);
        Assert.Equal("low_text", doc.ExtractionQuality.TextStatus);
        Assert.True(doc.ExtractionQuality.ManualReviewRecommended);
        Assert.True(doc.ExtractionQuality.RequiresCaution);
        Assert.True(doc.ExtractionQuality.OcrAttempted);
        Assert.False(doc.ExtractionQuality.OcrApplied);
        Assert.True(doc.ExtractionQuality.OcrRecommended);
        Assert.Equal("ocr_failed", doc.ExtractionQuality.OcrFailureReason);
        Assert.Equal(3, doc.ExtractionQuality.PageCount);
        Assert.Equal(1, doc.ExtractionQuality.TextPageCount);
        Assert.Equal(1, doc.ExtractionQuality.EmptyPageCount);
        Assert.Equal(2, doc.ExtractionQuality.SparsePageCount);
        Assert.Contains("low_average_words_per_page", doc.ExtractionQuality.Signals);
        Assert.Contains("ocr_failed", doc.ExtractionQuality.Signals);
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
            var databaseName = $"saaia_rtq_{Guid.NewGuid():N}";

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
            }
        }
    }
}
