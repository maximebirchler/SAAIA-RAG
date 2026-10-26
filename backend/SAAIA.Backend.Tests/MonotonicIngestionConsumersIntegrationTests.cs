using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class MonotonicIngestionConsumersIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Abandoned_generation_is_not_pending_backlog_or_automatic_reindex()
    {
        await using var scenario = await Scenario.CreateAsync("canceled", observedChangedSource: false);
        var (candidates, summary) = await scenario.ReadAsync();
        var qualityGapCount = await scenario.ReadQualityGapCountAsync();
        var failures = new List<string>();
        if (summary.CandidateCount != 0 || summary.ReadyToEnqueueCount != 0)
            failures.Add("An abandoned generation 5 / published 3 is not pending work.");
        if (candidates.Any(candidate => candidate.RecommendedAction == "enqueue_reindex"))
            failures.Add("Cancellation alone cannot recommend automatic reindex.");
        if (qualityGapCount != 0)
            failures.Add("The quality checker must not reject a settled canceled generation as an orphan pending job.");
        if (summary.ReasonCounts is null || !summary.ReasonCounts.ContainsKey("ingestion_generation_abandoned")
            || !summary.ReasonCounts.ContainsKey("source_freshness_unverified"))
            failures.Add("The abandoned generation and unverified present source must remain visible in diagnostics.");
        // Equality of documents.content_hash and revision.source_hash is NOT a
        // claim that the present file has been rehashed. No such claim is made.
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Active_generation_gap_remains_signaled_and_blocked_by_actual_job(string status)
    {
        await using var scenario = await Scenario.CreateAsync(status, observedChangedSource: false);
        var (candidates, summary) = await scenario.ReadAsync();
        var candidate = Assert.Single(candidates);
        Assert.Contains("indexed_version_outdated", candidate.Reasons);
        Assert.Equal(1, summary.CandidateCount);
        Assert.Equal(1, summary.BlockedByActiveJobCount);
        Assert.Equal(0, summary.ReadyToEnqueueCount);
        Assert.Equal(0, await scenario.ReadQualityGapCountAsync());
    }

    [ProfileFact("IntegrationPg")]
    public async Task Failed_generation_has_an_explicit_failure_diagnostic_instead_of_implicit_pending()
    {
        await using var scenario = await Scenario.CreateAsync("failed", observedChangedSource: false);
        var (candidates, summary) = await scenario.ReadAsync();
        Assert.Contains("ingestion_attempt_failed", candidates.SelectMany(candidate => candidate.Reasons)
            .Concat(summary.ReasonCounts?.Keys ?? []));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Changed_source_observed_by_abandoned_attempt_keeps_published_data_freshness_warning()
    {
        await using var scenario = await Scenario.CreateAsync("canceled", observedChangedSource: true);
        var (candidates, summary) = await scenario.ReadAsync();
        // Enqueue preserves documents.content_hash. The actual evidence here is
        // the abandoned worker's different resume.sourceHash plus changed size.
        Assert.Contains("indexed_version_outdated", candidates.SelectMany(candidate => candidate.Reasons)
            .Concat(summary.ReasonCounts?.Keys ?? []));
        Assert.Equal(1, await scenario.ReadQualityGapCountAsync());
        Assert.Equal(0, summary.ReadyToEnqueueCount);
        Assert.DoesNotContain(candidates, candidate => candidate.RecommendedAction == "enqueue_reindex");
    }

    [ProfileFact("IntegrationPg")]
    public async Task Unknown_generation_without_a_matching_job_stays_explicitly_unresolved()
    {
        await using var scenario = await Scenario.CreateAsync("none", observedChangedSource: false);
        var (candidates, summary) = await scenario.ReadAsync();
        Assert.Contains("ingestion_generation_unresolved", Assert.Single(candidates).Reasons);
        Assert.True(summary.ReasonCounts?.ContainsKey("ingestion_generation_unresolved") == true);
        Assert.Equal(1, await scenario.ReadQualityGapCountAsync());
    }

    [ProfileFact("IntegrationPg")]
    public async Task Failure_without_a_valid_publication_remains_visible_and_policy_blocked()
    {
        await using var scenario = await Scenario.CreateAsync("failed", observedChangedSource: false);
        await scenario.MarkNeverPublishedAsync();
        var (candidates, summary) = await scenario.ReadAsync();
        var candidate = Assert.Single(candidates);
        Assert.Contains("never_indexed", candidate.Reasons);
        Assert.Contains("ingestion_attempt_failed", candidate.Reasons);
        Assert.Contains("auto_ingest_paused", candidate.Reasons);
        Assert.Equal(1, summary.CandidateCount);
        Assert.Equal(0, summary.ReadyToEnqueueCount);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly LocalPostgresDiagnosticFixture _database;
        private readonly string _sourceRoot;
        private readonly Guid _tenant;

        private Scenario(LocalPostgresDiagnosticFixture database, string sourceRoot, Guid tenant)
        {
            _database = database; _sourceRoot = sourceRoot; _tenant = tenant;
        }

        internal static async Task<Scenario> CreateAsync(string jobStatus, bool observedChangedSource)
        {
            var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
            var sourceRoot = Path.Combine(Path.GetTempPath(), "saaia-monotonic-consumers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            var tenant = Guid.NewGuid();
            var scenario = new Scenario(database, sourceRoot, tenant);
            try
            {
                // This is a metadata-consumer fixture, not a PDF extraction test.
                await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "control.pdf"), [1, 2, 3, 4]);
                var doc = Guid.NewGuid();
                var payload = JsonSerializer.Serialize(new
                {
                    docId = doc, version = 5, indexedVersionBefore = 3,
                    resume = observedChangedSource ? new { sourceHash = new string('b', 64), fileSize = 5 } : null
                });
                await using var connection = await database.DataSource.OpenConnectionAsync();
                await connection.ExecuteAsync("""
                    INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic monotonic generation consumer');
                    INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,content_hash,
                        file_size,file_mtime,page_count,ingestion_version,indexed_version,auto_ingest_paused)
                    VALUES(@tenant,@doc,'control.pdf','control.pdf','synthetic','indexed',decode(repeat('aa',32),'hex'),
                        @source_size,'2026-10-25T00:00:00Z',1,5,3,false);
                    INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,source_size,
                        source_mtime,ingestion_version,indexed_version)
                    VALUES(@revision,@tenant,@doc,'control.pdf',decode(repeat('aa',32),'hex'),4,
                        '2026-10-25T00:00:00Z',3,3);
                    INSERT INTO document_sections(section_id,tenant_id,revision_id,ordinal,title,section_level,page_start,page_end)
                    VALUES(@section,@tenant,@revision,0,'Control',1,1,1);
                    INSERT INTO document_page_index(tenant_id,revision_id,page_number,char_count,checksum)
                    VALUES(@tenant,@revision,1,length(@text),sha256(convert_to(@text,'UTF8')));
                    INSERT INTO document_units(unit_id,tenant_id,revision_id,section_id,ordinal,page_start,page_end,
                        text_content,char_count,token_count,checksum,metadata)
                    VALUES(@unit,@tenant,@revision,@section,0,1,1,@text,length(@text),4,sha256(convert_to(@text,'UTF8')),'{}');
                    INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,section_id,unit_id,chunk_index,
                        page_start,page_end,text_content,token_count,checksum,metadata)
                    VALUES(@chunk,@tenant,@revision,@section,@unit,0,1,1,@text,4,sha256(convert_to(@text,'UTF8')),
                        jsonb_build_object('offsetStart',0,'offsetEnd',length(@text)));
                    INSERT INTO exact_match_entries(exact_match_entry_id,tenant_id,revision_id,section_id,unit_id,entry_index,
                        page_start,page_end,text_content,normalized_text,char_count,token_count,checksum,metadata)
                    VALUES(@exact,@tenant,@revision,@section,@unit,0,1,1,@text,
                        @normalized,length(@text),4,sha256(convert_to(@normalized,'UTF8')),
                        jsonb_build_object('offsetStart',0,'offsetEnd',length(@text)));
                    INSERT INTO contextual_text_entries(contextual_text_entry_id,tenant_id,revision_id,section_id,unit_id,
                        retrieval_chunk_id,entry_index,page_start,page_end,text_content,char_count,token_count,checksum)
                    VALUES(@context,@tenant,@revision,@section,@unit,@chunk,0,1,1,@text,length(@text),4,sha256(convert_to(@text,'UTF8')));
                    INSERT INTO ingestion_jobs(job_id,tenant_id,action,doc_path,category,status,attempts,payload,last_error,finished_at)
                    SELECT @job,@tenant,'upsert','control.pdf','synthetic',@status,1,CAST(@payload AS jsonb),
                        CASE WHEN @status='failed' THEN 'synthetic_failure' ELSE NULL END,
                        CASE WHEN @status IN ('failed','canceled') THEN now() ELSE NULL END
                    WHERE @status<>'none';
                    """, new
                {
                    tenant, doc, revision = Guid.NewGuid(), section = Guid.NewGuid(), unit = Guid.NewGuid(),
                    chunk = Guid.NewGuid(), exact = Guid.NewGuid(), context = Guid.NewGuid(), job = Guid.NewGuid(),
                    status = jobStatus, payload, source_size = observedChangedSource ? 5 : 4,
                    text = "Synthetic publication remains available.", normalized = "synthetic publication remains available"
                });
                Assert.True(await connection.ExecuteScalarAsync<bool>("""
                    SELECT (SELECT count(*) FROM document_units)=1
                      AND (SELECT count(*) FROM retrieval_chunks)=1
                      AND (SELECT count(*) FROM exact_match_entries)=1
                      AND (SELECT count(*) FROM contextual_text_entries)=1
                      AND NOT EXISTS (
                        SELECT 1 FROM document_units WHERE checksum IS NULL OR checksum<>sha256(convert_to(text_content,'UTF8'))
                          OR char_count<>length(text_content) OR token_count<>4 OR page_start<>1 OR page_end<>1
                        UNION ALL SELECT 1 FROM retrieval_chunks WHERE checksum IS NULL OR checksum<>sha256(convert_to(text_content,'UTF8'))
                          OR token_count<>4 OR page_start<>1 OR page_end<>1 OR (metadata->>'offsetStart')::int<>0
                          OR (metadata->>'offsetEnd')::int<>length(text_content)
                        UNION ALL SELECT 1 FROM exact_match_entries WHERE checksum<>sha256(convert_to(normalized_text,'UTF8'))
                          OR char_count<>length(text_content) OR token_count<>4 OR page_start<>1 OR page_end<>1
                          OR (metadata->>'offsetStart')::int<>0 OR (metadata->>'offsetEnd')::int<>length(text_content)
                        UNION ALL SELECT 1 FROM contextual_text_entries WHERE checksum<>sha256(convert_to(text_content,'UTF8'))
                          OR char_count<>length(text_content) OR token_count<>4 OR page_start<>1 OR page_end<>1
                      )
                    """), "The metadata-consumer fixture must have complete, exact text checksums and offsets before assertions.");
                return scenario;
            }
            catch
            {
                await scenario.DisposeAsync();
                throw;
            }
        }

        internal async Task<(AdminRuntimeCapabilityAEnrichmentCandidateDto[] Candidates,
            AdminRuntimeCapabilityOperationalSummaryDto Summary)> ReadAsync()
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync();
            var candidates = await RuntimeCapabilityAEnrichmentStore.LoadCandidatesAsync(connection, _tenant,
                new IngestionOptions { DocumentsRoot = _sourceRoot }, null, null, 10, null, CancellationToken.None);
            var summary = await RuntimeCapabilityDiagnosticsBuilder.LoadCapabilityAOperationalSummaryAsync(connection,
                _tenant, "capability_a.corpus_enrichment", CancellationToken.None);
            return (candidates, summary);
        }

        internal async Task MarkNeverPublishedAsync()
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync("""
                UPDATE documents SET indexed_version=0,status='error',auto_ingest_paused=true,
                    auto_ingest_pause_reason='synthetic_failure' WHERE tenant_id=@tenant
                """, new { tenant = _tenant });
        }

        internal async Task<int> ReadQualityGapCountAsync()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "run-ingestion-quality-checks.ps1")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            var source = await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "tools", "run-ingestion-quality-checks.ps1"));
            // Execute the real quality-check SELECT; do not restate its SQL in a fixture.
            var match = Regex.Match(source,
                @"SELECT 'version_gap_no_active'.*?GROUP BY d\.category(?:,\s*3)?", RegexOptions.Singleline);
            Assert.True(match.Success, "The quality script's generation-gap query must remain explicitly testable.");
            var query = "SELECT check_name AS \"Check\", category AS \"Category\", metric AS \"Metric\", value AS \"Value\" FROM ("
                + match.Value.Replace("$categoryFilterDocuments", "") + ") AS q(check_name,category,metric,value)";
            await using var connection = await _database.DataSource.OpenConnectionAsync();
            var counts = await connection.QueryAsync<QualityCount>(query);
            return counts.Where(row => row.Metric is not ("abandoned_source_unverified" or "failed_attempt"))
                .Sum(row => int.Parse(row.Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            File.Delete(Path.Combine(_sourceRoot, "control.pdf"));
            if (Directory.Exists(_sourceRoot)) Directory.Delete(_sourceRoot);
        }
    }

    private sealed record QualityCount(string Check, string Category, string Metric, string Value);
}
