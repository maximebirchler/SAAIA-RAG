using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdminRuntimeEndpointsTests
{
    [Fact]
    public async Task CatalogAsync_exposes_runtime_catalog_and_default_profile()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CatalogAsync(
            ctx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCatalogResponseDto>(result, ctx);

        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Contains(payload.Runtimes, item =>
            item.Key == "qdrant"
            && item.ReadinessStatus == "configured"
            && item.ConfigurationSource == "rag_options.qdrant_base_url"
            && item.UsedByProfileKeys!.Contains("default-local")
            && item.RequiredSettingKeys!.Contains("qdrant_collection"));
        Assert.Contains(payload.Runtimes, item => item.Key == "tei-embeddings");
        Assert.Contains(payload.Runtimes, item =>
            item.Key == "server-capability-b"
            && item.DependencyRuntimeKeys!.Contains("llm-backoffice-chat"));
        var llmRuntime = Assert.Single(payload.Runtimes, item => item.Key == "llm-backoffice-chat");
        Assert.Equal("llm_runtime", llmRuntime.Kind);
        Assert.Equal("environment.BACKOFFICE_LLM_ENABLED + chat_options.llm_base_url", llmRuntime.ConfigurationSource);
        if (llmRuntime.Enabled)
        {
            Assert.Equal("http://llm.test/", llmRuntime.BaseUrl);
            Assert.Equal("local", llmRuntime.Model);
            Assert.Equal("configured", llmRuntime.ReadinessStatus);
        }
        else
        {
            Assert.Equal("disabled", llmRuntime.ReadinessStatus);
            Assert.Contains("BACKOFFICE_LLM_ENABLED", llmRuntime.MissingSettingKeys!);
        }
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "default-local" && item.PassCount == 3);
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "default-local" && item.HardwareRequirements is not null);
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "default-local" && item.FreshnessPolicy is not null);
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "strict-local" && item.PerformanceBudgets is not null);
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "strict-rerank" && item.CheckPolicy is not null);
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "strict-rerank" && item.RuntimeRequirements is not null);
        Assert.Contains(payload.Capabilities, item => item.Key == "core.retrieval" && item.Implemented);
        Assert.Contains(payload.Capabilities, item => item.Key == "capability_a.corpus_enrichment" && item.Implemented);
    }

    [Fact]
    public async Task RuntimeCatalogArtifactAsync_returns_cdc_named_artifact_payload()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.RuntimeCatalogArtifactAsync(
            ctx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeRuntimeCatalogArtifactDto>(result, ctx);

        Assert.Equal("runtime_catalog.json", payload.Artifact);
        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Contains(payload.Runtimes, item => item.Key == "tei-embeddings");
        var llmRuntime = Assert.Single(payload.Runtimes, item => item.Key == "llm-backoffice-chat");
        if (llmRuntime.Enabled)
        {
            Assert.Equal("configured", llmRuntime.ReadinessStatus);
            Assert.Equal("http://llm.test/", llmRuntime.BaseUrl);
        }
        else
        {
            Assert.Equal("disabled", llmRuntime.ReadinessStatus);
            Assert.Contains("BACKOFFICE_LLM_ENABLED", llmRuntime.MissingSettingKeys!);
        }
        Assert.Contains(payload.WarmupProfiles, item => item.HardwareRequirements is not null);
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "strict-local");
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "strict-rerank");
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "strict-rerank" && item.RuntimeRequirements!.RequireRerank);
    }

    [Fact]
    public async Task ModelCatalogArtifactAsync_returns_runtime_model_entries()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.ModelCatalogArtifactAsync(
            ctx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeModelCatalogArtifactDto>(result, ctx);

        Assert.Equal("model_catalog.json", payload.Artifact);
        Assert.Contains(payload.Items, item =>
            item.Key == "tei-embeddings"
            && item.Enabled
            && item.Implemented
            && item.ReadinessStatus == "configured"
            && item.ConfigurationSource == "rag_options.embeddings_base_url"
            && item.RequiredByProfileKeys!.Contains("strict-rerank"));
        Assert.Contains(payload.Items, item =>
            item.Key == "tei-rerank"
            && !item.Enabled
            && item.ReadinessStatus == "disabled"
            && item.ConfigurationSource == "rag_options.rerank_disabled");
        Assert.Contains(payload.Items, item =>
            item.Key == "server-capability-b"
            && item.DependencyRuntimeKeys!.Contains("llm-backoffice-chat"));
        Assert.Contains(payload.Items, item =>
            item.Key == "llm-backoffice-chat"
            && !item.Enabled
            && item.ReadinessStatus == "disabled"
            && item.ConfigurationSource == "environment.BACKOFFICE_LLM_ENABLED + chat_options.llm_base_url");
    }

    [Fact]
    public async Task ModelCatalogArtifactAsync_marks_rerank_runtime_as_derived_when_it_falls_back_to_embeddings_endpoint()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.ModelCatalogArtifactAsync(
            ctx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions(enableRerank: true, rerankModel: "cross-encoder/ms-marco")),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeModelCatalogArtifactDto>(result, ctx);
        var rerank = Assert.Single(payload.Items, item => item.Key == "tei-rerank");

        Assert.True(rerank.Enabled);
        Assert.Equal("configured", rerank.ReadinessStatus);
        Assert.Equal("derived_from_rag_options.embeddings_base_url", rerank.ConfigurationSource);
        Assert.Contains("strict-rerank", rerank.RequiredByProfileKeys!);
        Assert.Empty(rerank.MissingSettingKeys!);
    }

    [Fact]
    public async Task RequalifyAsync_qualifies_core_retrieval_and_persists_state_and_warmup()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.Equal("core.retrieval", state.Key);
        Assert.True(state.Installed);
        Assert.True(state.Configured);
        Assert.True(state.Healthy);
        Assert.True(state.Qualified);
        Assert.True(state.Authorized);
        Assert.True(state.Selected);
        Assert.Equal(3, state.PassCount);
        Assert.Single(payload.WarmupResults);
        Assert.True(payload.WarmupResults[0].Passed);
        Assert.NotNull(state.Details);

        var details = JsonSerializer.SerializeToElement(state.Details);
        var checks = details.GetProperty("checks");
        Assert.Equal(JsonValueKind.Array, checks.ValueKind);
        Assert.True(checks.GetArrayLength() > 0);
        Assert.True(checks[0].GetProperty("qdrant").TryGetProperty("durationMs", out _));
        Assert.True(checks[0].GetProperty("teiEmbeddings").TryGetProperty("durationMs", out _));
        Assert.True(checks[0].GetProperty("measurements").TryGetProperty("qdrantLoadTimeMs", out _));
        Assert.True(checks[0].GetProperty("measurements").TryGetProperty("embeddingsLoadTimeMs", out _));
        Assert.True(checks[0].TryGetProperty("runtimeGates", out _));
        Assert.True(details.TryGetProperty("qualificationFingerprint", out _));
        Assert.True(details.TryGetProperty("runtimeEnvironment", out _));
        Assert.True(checks[0].TryGetProperty("performanceBudgets", out _));
        Assert.True(checks[0].GetProperty("qdrant").TryGetProperty("measurements", out var qdrantMeasurements));
        Assert.Equal("not_applicable_for_retrieval_runtime", qdrantMeasurements.GetProperty("applicability").GetProperty("ttftMs").GetString());
        Assert.True(details.TryGetProperty("measurementSemantics", out _));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var persisted = await conn.QuerySingleAsync<(bool qualified, bool selected, int pass_count)>(
            "SELECT qualified, selected, pass_count FROM runtime_capability_state WHERE capability_key='core.retrieval';");
        Assert.True(persisted.qualified);
        Assert.True(persisted.selected);
        Assert.Equal(3, persisted.pass_count);

        var warmupCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM runtime_warmup_results WHERE capability_key='core.retrieval';");
        Assert.Equal(1, warmupCount);
    }

    [Fact]
    public async Task CapabilitiesAsync_returns_persisted_core_state_and_default_optional_capabilities()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilitiesAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilitiesResponseDto>(result, ctx);

        Assert.Equal(4, payload.Items.Count);
        Assert.Contains(payload.Items, item => item.Key == "core.retrieval" && item.Qualified);
        Assert.Contains(payload.Items, item => item.Key == "capability_a.corpus_enrichment" && item.Implemented && !item.Qualified);
        Assert.Contains(payload.WarmupResults, item => item.CapabilityKey == "core.retrieval" && item.Passed);
    }

    [Fact]
    public async Task DiagnosticsAsync_returns_summary_and_recommendations_for_runtime_state()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.DiagnosticsAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(result, ctx);
        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.True(payload.Summary.TotalCapabilities >= 4);
        Assert.True(payload.Summary.SelectedCapabilities >= 1);
        Assert.True(payload.Summary.PersistedSelectedCapabilities >= 1);
        Assert.Equal(0, payload.Summary.StaleCapabilities);
        Assert.NotNull(payload.Summary.Operational);
        Assert.Equal(0, payload.Summary.Operational!.CapabilityACandidateCount);
        Assert.Equal(0, payload.Summary.Operational.CapabilityAOffsetBackfillCandidateCount);
        Assert.Equal(0, payload.Summary.Operational.CapabilityBBacklogCount);

        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.Equal("selected", retrieval.Status);
        Assert.Contains("runtime is ready for the nominal path", retrieval.Recommendations);

        var operationalCtx = BuildAdminContext();
        var operationalResult = await AdminRuntimeEndpoints.OperationalSummaryAsync(
            operationalCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        var operational = await ExecuteResultAsync<AdminRuntimeOperationalSummaryResponseDto>(operationalResult, operationalCtx);
        Assert.Equal(0, operational.Summary.CapabilityACandidateCount);
        Assert.Equal(0, operational.Summary.CapabilityAOffsetBackfillCandidateCount);
        Assert.Equal(0, operational.Summary.CapabilityBBacklogCount);
        Assert.Contains(operational.Items, item => item.Key == "capability_a.corpus_enrichment");
        Assert.Contains(operational.Items, item => item.Key == "capability_b.backoffice_generation");
    }

    [Fact]
    public async Task EventsAsync_and_artifact_return_runtime_audit_trail_for_requalification_and_selection_updates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var selectionCtx = BuildAdminContext();
        var selectionResult = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            selectionCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(DesiredEnabled: false));
        var updated = await ExecuteResultAsync<AdminRuntimeCapabilityStateDto>(selectionResult, selectionCtx);
        Assert.False(updated.DesiredEnabled);
        Assert.False(updated.Selected);

        var eventsCtx = BuildAdminContext();
        var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
            eventsCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: "core.retrieval",
            limit: 10);
        var payload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);

        Assert.True(payload.Items.Count >= 2);
        Assert.Equal("selection_updated", payload.Items[0].EventType);
        Assert.Contains(payload.Items, item => item.EventType == "requalified" && item.Reason == "qualified");

        var selectionEvent = Assert.Single(payload.Items, item => item.EventType == "selection_updated");
        Assert.NotNull(selectionEvent.Details);
        Assert.Equal(false, selectionEvent.Details!["desiredEnabled"]);
        Assert.Equal(true, selectionEvent.Details["previousSelected"]);

        var artifactCtx = BuildAdminContext();
        var artifactResult = await AdminRuntimeEndpoints.EventsArtifactAsync(
            artifactCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: "core.retrieval",
            limit: 10);
        var artifact = await ExecuteResultAsync<AdminRuntimeEventsArtifactDto>(artifactResult, artifactCtx);

        Assert.Equal("runtime_events.json", artifact.Artifact);
        Assert.Equal(payload.Items.Count, artifact.Items.Count);
    }

    [Fact]
    public async Task EventsAsync_records_rejected_selection_and_stale_reconciliation_actions()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var rejectedSelectionCtx = BuildAdminContext();
        var rejectedSelectionResult = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            rejectedSelectionCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "capability_a.corpus_enrichment",
            new AdminRuntimeCapabilitySelectionRequestDto(Authorized: true));
        var rejectionPayload = await ExecuteResultAsync<JsonElement>(rejectedSelectionResult, rejectedSelectionCtx);
        Assert.Equal("capability_a.corpus_enrichment", rejectionPayload.GetProperty("capabilityKey").GetString());

        var reconcileCtx = BuildAdminContext();
        var reconcileResult = await AdminRuntimeEndpoints.ReconcileStaleAsync(
            reconcileCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions(collection: "documents_v2")),
            new StubHostEnvironment(),
            new AdminRuntimeReconcileStaleRequestDto(CapabilityKey: "core.retrieval"));
        var reconcilePayload = await ExecuteResultAsync<AdminRuntimeReconcileStaleResponseDto>(reconcileResult, reconcileCtx);
        Assert.Equal(1, reconcilePayload.UpdatedCount);

        var retrievalEventsCtx = BuildAdminContext();
        var retrievalEventsResult = await AdminRuntimeEndpoints.EventsAsync(
            retrievalEventsCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: "core.retrieval",
            limit: 10);
        var retrievalEvents = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(retrievalEventsResult, retrievalEventsCtx);
        Assert.Contains(retrievalEvents.Items, item => item.EventType == "stale_reconciled" && item.Reason == "qualification_inputs_changed");

        var rejectedEventsCtx = BuildAdminContext();
        var rejectedEventsResult = await AdminRuntimeEndpoints.EventsAsync(
            rejectedEventsCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: "capability_a.corpus_enrichment",
            limit: 10);
        var rejectedEvents = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(rejectedEventsResult, rejectedEventsCtx);
        var rejected = Assert.Single(rejectedEvents.Items, item => item.EventType == "selection_rejected");
        Assert.Equal("capability must be qualified before it can be authorized", rejected.Reason);
        Assert.NotNull(rejected.Details);
        Assert.Equal(true, rejected.Details!["requestedAuthorized"]);
    }

    [Fact]
    public async Task RequalifyAsync_can_qualify_and_select_capability_a()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(
                CapabilityKey: "capability_a.corpus_enrichment",
                SelectWhenQualified: true));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.Equal("capability_a.corpus_enrichment", state.Key);
        Assert.True(state.Implemented);
        Assert.True(state.Installed);
        Assert.True(state.Configured);
        Assert.True(state.Healthy);
        Assert.True(state.Qualified);
        Assert.True(state.Authorized);
        Assert.True(state.Selected);
        Assert.Equal("server-capability-a", state.RuntimeKey);
        Assert.Equal(1, state.PassCount);
        Assert.NotNull(state.Details);
        Assert.Equal("corpus_enrichment_admin", state.Details!["mode"]);

        var diagnosticsCtx = BuildAdminContext();
        var diagnosticsResult = await AdminRuntimeEndpoints.DiagnosticsAsync(
            diagnosticsCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        var diagnostics = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnosticsResult, diagnosticsCtx);
        var capabilityA = Assert.Single(diagnostics.Items, item => item.Key == "capability_a.corpus_enrichment");
        Assert.Equal("selected", capabilityA.Status);
        Assert.Contains(
            capabilityA.Recommendations,
            item => item.Contains("review capability A semantic previews", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CapabilityA_candidates_and_enqueue_flow_use_runtime_governance_and_ingestion_queue()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"saaia-cap-a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var firstRel = "ATEX/never-indexed.pdf";
            var secondRel = "Programmation/missing-artifacts.pdf";
            var thirdRel = "Programmation/legacy-offsets.pdf";
            var firstAbs = Path.Combine(tempRoot, "ATEX", "never-indexed.pdf");
            var secondAbs = Path.Combine(tempRoot, "Programmation", "missing-artifacts.pdf");
            var thirdAbs = Path.Combine(tempRoot, "Programmation", "legacy-offsets.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(firstAbs)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondAbs)!);
            Directory.CreateDirectory(Path.GetDirectoryName(thirdAbs)!);
            await File.WriteAllBytesAsync(firstAbs, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(secondAbs, [5, 6, 7, 8]);
            await File.WriteAllBytesAsync(thirdAbs, [9, 10, 11, 12]);

            var firstDocId = Guid.NewGuid();
            var secondDocId = Guid.NewGuid();
            var thirdDocId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var legacyRevisionId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var unitId = Guid.NewGuid();
            var legacySectionId = Guid.NewGuid();
            var legacyUnitId = Guid.NewGuid();
            var legacyChunkId = Guid.NewGuid();
            var legacyExactId = Guid.NewGuid();
            var legacyContextualId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused, auto_ingest_pause_reason
)
VALUES
  (@tenant, @doc1, @path1, 'never-indexed.pdf', 'atex', 'pending', now(), now(), 1, 0, false, NULL),
  (@tenant, @doc2, @path2, 'missing-artifacts.pdf', 'programmation', 'indexed', now(), now(), 1, 1, true, 'manual_review'),
  (@tenant, @doc3, @path3, 'legacy-offsets.pdf', 'programmation', 'indexed', now(), now(), 1, 1, false, NULL);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
  ingestion_version, indexed_version, published_at, created_at
)
VALUES(
  @revision_id, @tenant, @doc2, @path2, decode(repeat('ab', 32), 'hex'), 4, now(), 1, 1, now(), now()
),(
  @legacy_revision_id, @tenant, @doc3, @path3, decode(repeat('cd', 32), 'hex'), 4, now(), 1, 1, now(), now()
);

INSERT INTO document_sections(
  section_id, tenant_id, revision_id, ordinal, title, section_level, page_start, page_end, metadata, created_at
)
VALUES(
  @section_id, @tenant, @revision_id, 0, 'Control Loop Overview', 1, 1, 2, '{}'::jsonb, now()
),(
  @legacy_section_id, @tenant, @legacy_revision_id, 0, 'Legacy Offsets', 1, 1, 1, '{}'::jsonb, now()
);

INSERT INTO document_units(
  unit_id, tenant_id, revision_id, section_id, ordinal, page_start, page_end, text_content,
  char_count, token_count, metadata, created_at
)
VALUES(
  @unit_id, @tenant, @revision_id, @section_id, 0, 1, 1,
  'This unit explains how the control loop is configured, supervised and recovered during runtime incidents.',
  108, 17, '{}'::jsonb, now()
),(
  @legacy_unit_id, @tenant, @legacy_revision_id, @legacy_section_id, 0, 1, 1,
  'Legacy chunk text with no stored offsets yet.',
  41, 8, '{}'::jsonb, now()
);

INSERT INTO retrieval_chunks(
  retrieval_chunk_id, tenant_id, revision_id, section_id, unit_id, chunk_index, page_start, page_end,
  text_content, token_count, checksum, metadata, created_at
)
VALUES(
  @legacy_chunk_id, @tenant, @legacy_revision_id, @legacy_section_id, @legacy_unit_id, 0, 1, 1,
  'Legacy chunk text with no stored offsets yet.',
  8, decode(repeat('11', 32), 'hex'), '{"chunkType":"unit_exact_v1"}'::jsonb, now()
);

INSERT INTO exact_match_entries(
  exact_match_entry_id, tenant_id, revision_id, section_id, unit_id, entry_index, page_start, page_end,
  text_content, normalized_text, char_count, token_count, checksum, metadata, created_at
)
VALUES(
  @legacy_exact_id, @tenant, @legacy_revision_id, @legacy_section_id, @legacy_unit_id, 0, 1, 1,
  'Legacy chunk text with no stored offsets yet.',
  'legacy chunk text with no stored offsets yet',
  41, 8, decode(repeat('22', 32), 'hex'), '{"kind":"verbatim_excerpt"}'::jsonb, now()
);

INSERT INTO contextual_text_entries(
  contextual_text_entry_id, tenant_id, revision_id, section_id, unit_id, retrieval_chunk_id, entry_index, page_start, page_end,
  text_content, char_count, token_count, checksum, metadata, created_at
)
VALUES(
  @legacy_contextual_id, @tenant, @legacy_revision_id, @legacy_section_id, @legacy_unit_id, @legacy_chunk_id, 0, 1, 1,
  'Document: legacy-offsets.pdf\nExcerpt:\nLegacy chunk text with no stored offsets yet.',
  78, 10, decode(repeat('33', 32), 'hex'), '{}'::jsonb, now()
);
""",
                    new
                    {
                        tenant = tenantId,
                        doc1 = firstDocId,
                        doc2 = secondDocId,
                        doc3 = thirdDocId,
                        path1 = firstRel.Replace('\\', '/'),
                        path2 = secondRel.Replace('\\', '/'),
                        path3 = thirdRel.Replace('\\', '/'),
                        revision_id = revisionId,
                        legacy_revision_id = legacyRevisionId,
                        section_id = sectionId,
                        unit_id = unitId,
                        legacy_section_id = legacySectionId,
                        legacy_unit_id = legacyUnitId,
                        legacy_chunk_id = legacyChunkId,
                        legacy_exact_id = legacyExactId,
                        legacy_contextual_id = legacyContextualId
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_a.corpus_enrichment",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var candidatesCtx = BuildAdminContext();
            var candidatesResult = await AdminRuntimeEndpoints.CapabilityAEnrichmentCandidatesAsync(
                candidatesCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                Options.Create(new IngestionOptions { DocumentsRoot = tempRoot }),
                CreateCapabilityAQuestionService(),
                new StubHostEnvironment(),
                category: null,
                limit: 20);
            var candidates = await ExecuteResultAsync<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>(candidatesResult, candidatesCtx);

            Assert.Equal("capability_a.corpus_enrichment", candidates.CapabilityKey);
            Assert.True(candidates.TotalCandidates >= 3);
            Assert.Contains(candidates.Items, item => item.DocPath == firstRel.Replace('\\', '/') && item.Reasons.Contains("never_indexed"));
            Assert.Contains(candidates.Items, item => item.DocPath == secondRel.Replace('\\', '/') && item.Reasons.Contains("retrieval_chunks_missing"));
            Assert.Contains(candidates.Items, item => item.DocPath == secondRel.Replace('\\', '/') && item.Reasons.Contains("auto_ingest_paused"));
            Assert.Contains(candidates.Items, item => item.DocPath == thirdRel.Replace('\\', '/') && item.Reasons.Contains("retrieval_chunk_offsets_missing"));
            Assert.Contains(candidates.Items, item => item.DocPath == thirdRel.Replace('\\', '/') && item.Reasons.Contains("exact_match_offsets_missing"));
            var semanticCandidate = Assert.Single(candidates.Items, item => item.DocPath == secondRel.Replace('\\', '/'));
            Assert.NotNull(semanticCandidate.PreviewText);
            Assert.Contains("control loop", semanticCandidate.PreviewText!, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(semanticCandidate.KeySectionTitles);
            Assert.Contains("Control Loop Overview", semanticCandidate.KeySectionTitles!);
            Assert.NotNull(semanticCandidate.SuggestedTags);
            Assert.Contains("programmation", semanticCandidate.SuggestedTags!);
            Assert.Contains("control", semanticCandidate.SuggestedTags!);
            Assert.NotNull(semanticCandidate.HypotheticalQuestions);
            Assert.Contains(semanticCandidate.HypotheticalQuestions!, item => item.Contains("Control Loop Overview", StringComparison.OrdinalIgnoreCase));

            var diagnosticsCtx = BuildAdminContext(tenantId);
            var diagnosticsResult = await AdminRuntimeEndpoints.DiagnosticsAsync(
                diagnosticsCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());
            var diagnostics = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnosticsResult, diagnosticsCtx);
            var capabilityA = Assert.Single(diagnostics.Items, item => item.Key == "capability_a.corpus_enrichment");
            Assert.NotNull(capabilityA.OperationalSummary);
            Assert.Equal(3, capabilityA.OperationalSummary!.CandidateCount);
            Assert.Equal(2, capabilityA.OperationalSummary.ReadyToEnqueueCount);
            Assert.Equal(1, capabilityA.OperationalSummary.OffsetBackfillCandidateCount);
            Assert.Equal(0, capabilityA.OperationalSummary.BlockedByActiveJobCount);
            Assert.Equal(1, capabilityA.OperationalSummary.BlockedByCooldownCount);
            Assert.NotNull(capabilityA.OperationalSummary.ReasonCounts);
            Assert.Equal(1, capabilityA.OperationalSummary.ReasonCounts!["retrieval_chunk_offsets_missing"]);
            Assert.Equal(1, capabilityA.OperationalSummary.ReasonCounts["exact_match_offsets_missing"]);
            Assert.NotNull(diagnostics.Summary.Operational);
            Assert.Equal(3, diagnostics.Summary.Operational!.CapabilityACandidateCount);
            Assert.Equal(2, diagnostics.Summary.Operational.CapabilityAReadyToEnqueueCount);
            Assert.Equal(1, diagnostics.Summary.Operational.CapabilityAOffsetBackfillCandidateCount);
            Assert.Contains(
                capabilityA.Recommendations,
                item => item.Contains("legacy evidence-pack offsets", StringComparison.OrdinalIgnoreCase));

            var operationalCtx = BuildAdminContext(tenantId);
            var operationalResult = await AdminRuntimeEndpoints.OperationalSummaryAsync(
                operationalCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());
            var operational = await ExecuteResultAsync<AdminRuntimeOperationalSummaryResponseDto>(operationalResult, operationalCtx);
            Assert.Equal(3, operational.Summary.CapabilityACandidateCount);
            Assert.Equal(2, operational.Summary.CapabilityAReadyToEnqueueCount);
            Assert.Equal(1, operational.Summary.CapabilityAOffsetBackfillCandidateCount);
            var operationalA = Assert.Single(operational.Items, item => item.Key == "capability_a.corpus_enrichment");
            Assert.Equal("selected", operationalA.Status);
            Assert.Equal(1, operationalA.Summary.OffsetBackfillCandidateCount);
            Assert.Contains(operationalA.Recommendations, item => item.Contains("legacy evidence-pack offsets", StringComparison.OrdinalIgnoreCase));

            var candidatesArtifactCtx = BuildAdminContext();
            var candidatesArtifactResult = await AdminRuntimeEndpoints.CapabilityACandidatesArtifactAsync(
                candidatesArtifactCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                Options.Create(new IngestionOptions { DocumentsRoot = tempRoot }),
                CreateCapabilityAQuestionService(),
                new StubHostEnvironment(),
                category: null,
                limit: 20);
            var candidatesArtifact = await ExecuteResultAsync<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>(candidatesArtifactResult, candidatesArtifactCtx);

            Assert.Equal("capability_a_candidates.json", candidatesArtifact.Artifact);
            Assert.Equal(candidates.CapabilityKey, candidatesArtifact.CapabilityKey);
            Assert.Equal(candidates.TotalCandidates, candidatesArtifact.TotalCandidates);
            Assert.Contains(candidatesArtifact.Items, item => item.DocPath == firstRel.Replace('\\', '/') && item.Reasons.Contains("never_indexed"));
            Assert.Contains(candidatesArtifact.Items, item => item.DocPath == secondRel.Replace('\\', '/') && item.HypotheticalQuestions is not null && item.HypotheticalQuestions.Count > 0);
            Assert.Contains(candidatesArtifact.Items, item => item.DocPath == thirdRel.Replace('\\', '/') && item.Reasons.Contains("retrieval_chunk_offsets_missing"));

            var dryRunCtx = BuildAdminContext();
            var dryRunResult = await AdminRuntimeEndpoints.CapabilityAEnqueueAsync(
                dryRunCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                Options.Create(new IngestionOptions { DocumentsRoot = tempRoot }),
                CreateCapabilityAQuestionService(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityAEnqueueRequestDto(
                    DocPaths: [firstRel.Replace('\\', '/'), secondRel.Replace('\\', '/'), thirdRel.Replace('\\', '/')],
                    DryRun: true));
            var dryRun = await ExecuteResultAsync<AdminRuntimeCapabilityAEnqueueResponseDto>(dryRunResult, dryRunCtx);

            Assert.NotEqual(Guid.Empty, dryRun.CampaignId);
            Assert.True(dryRun.DryRun);
            Assert.False(dryRun.AllowUnsafeCandidates);
            Assert.Equal(3, dryRun.CandidateCount);
            Assert.Equal(2, dryRun.PlannedCount);
            Assert.Equal(0, dryRun.QueuedCount);
            Assert.Equal(3, dryRun.SkippedCount);
            Assert.Contains(dryRun.Items, item => item.DocPath == firstRel.Replace('\\', '/') && item.Reason == "dry_run_preview");
            Assert.Contains(dryRun.Items, item => item.DocPath == secondRel.Replace('\\', '/') && item.Reason == "policy_blocked:auto_ingest_paused");
            Assert.Contains(dryRun.Items, item => item.DocPath == thirdRel.Replace('\\', '/') && item.Reason == "dry_run_preview");
            Assert.Contains(dryRun.Items, item =>
                item.DocPath == secondRel.Replace('\\', '/')
                && item.PreviewText is not null
                && item.KeySectionTitles is not null
                && item.KeySectionTitles.Contains("Control Loop Overview")
                && item.HypotheticalQuestions is not null
                && item.HypotheticalQuestions.Any(question => question.Contains("Control Loop Overview", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(1, dryRun.ReasonCounts["never_indexed"]);
            Assert.Equal(1, dryRun.ReasonCounts["retrieval_chunks_missing"]);
            Assert.Equal(1, dryRun.ReasonCounts["auto_ingest_paused"]);
            Assert.Equal(1, dryRun.ReasonCounts["retrieval_chunk_offsets_missing"]);
            Assert.Equal(1, dryRun.ReasonCounts["exact_match_offsets_missing"]);
            Assert.Equal(1, dryRun.ReasonCounts["blocked:auto_ingest_paused"]);

            var enqueueCtx = BuildAdminContext();
            var enqueueResult = await AdminRuntimeEndpoints.CapabilityAEnqueueAsync(
                enqueueCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                Options.Create(new IngestionOptions { DocumentsRoot = tempRoot }),
                CreateCapabilityAQuestionService(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityAEnqueueRequestDto(
                    DocPaths: [firstRel.Replace('\\', '/'), secondRel.Replace('\\', '/'), thirdRel.Replace('\\', '/')]));
            var enqueue = await ExecuteResultAsync<AdminRuntimeCapabilityAEnqueueResponseDto>(enqueueResult, enqueueCtx);

            Assert.NotEqual(Guid.Empty, enqueue.CampaignId);
            Assert.False(enqueue.DryRun);
            Assert.False(enqueue.AllowUnsafeCandidates);
            Assert.Equal(3, enqueue.CandidateCount);
            Assert.Equal(2, enqueue.PlannedCount);
            Assert.Equal(2, enqueue.QueuedCount);
            Assert.Equal(1, enqueue.SkippedCount);
            Assert.Contains(enqueue.Items, item => item.DocPath == firstRel.Replace('\\', '/') && item.Queued && item.JobId is not null);
            Assert.Contains(enqueue.Items, item => item.DocPath == thirdRel.Replace('\\', '/') && item.Queued && item.JobId is not null);
            Assert.Contains(enqueue.Items, item =>
                item.DocPath == secondRel.Replace('\\', '/')
                && !item.Queued
                && item.Reason == "policy_blocked:auto_ingest_paused"
                && item.PreviewText is not null
                && item.SuggestedTags is not null
                && item.SuggestedTags.Contains("control"));

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var queuedJobs = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM ingestion_jobs WHERE tenant_id=@tenant AND doc_path IN (@doc_path_1, @doc_path_2) AND status='queued';",
                    new { tenant = tenantId, doc_path_1 = firstRel.Replace('\\', '/'), doc_path_2 = thirdRel.Replace('\\', '/') });
                Assert.Equal(2, queuedJobs);
            }

            var eventsCtx = BuildAdminContext();
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_a.corpus_enrichment",
                limit: 20);
            var events = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);

            Assert.Contains(events.Items, item => item.EventType == "requalified");
            Assert.Contains(events.Items, item => item.EventType == "capability_a_enqueued");
            Assert.Contains(events.Items, item => item.EventType == "capability_a_campaign_dry_run");
            Assert.Contains(events.Items, item => item.EventType == "capability_a_campaign_executed");

            var campaignsCtx = BuildAdminContext();
            var campaignsResult = await AdminRuntimeEndpoints.CapabilityACampaignsAsync(
                campaignsCtx,
                ds,
                new StubHostEnvironment(),
                limit: 10);
            var campaigns = await ExecuteResultAsync<AdminRuntimeCapabilityACampaignsResponseDto>(campaignsResult, campaignsCtx);

            Assert.Equal("capability_a.corpus_enrichment", campaigns.CapabilityKey);
            Assert.True(campaigns.Items.Count >= 2);
            Assert.Contains(campaigns.Items, item =>
                item.DryRun
                && item.Status == "dry_run"
                && item.CandidateCount == 3
                && item.PlannedCount == 2
                && item.QueuedCount == 0
                && item.ReasonCounts["blocked:auto_ingest_paused"] == 1);
            Assert.Contains(campaigns.Items, item =>
                !item.DryRun
                && item.Status == "executed"
                && item.CandidateCount == 3
                && item.PlannedCount == 2
                && item.QueuedCount == 2
                && item.SkippedCount == 1);

            var campaignsArtifactCtx = BuildAdminContext();
            var campaignsArtifactResult = await AdminRuntimeEndpoints.CapabilityACampaignsArtifactAsync(
                campaignsArtifactCtx,
                ds,
                new StubHostEnvironment(),
                limit: 10);
            var campaignsArtifact = await ExecuteResultAsync<AdminRuntimeCapabilityACampaignsArtifactDto>(campaignsArtifactResult, campaignsArtifactCtx);

            Assert.Equal("capability_a_campaigns.json", campaignsArtifact.Artifact);
            Assert.Equal(campaigns.Items.Count, campaignsArtifact.Items.Count);

            var campaignDetailCtx = BuildAdminContext();
            var campaignDetailResult = await AdminRuntimeEndpoints.CapabilityACampaignAsync(
                campaignDetailCtx,
                ds,
                new StubHostEnvironment(),
                enqueue.CampaignId);
            var campaignDetail = await ExecuteResultAsync<AdminRuntimeCapabilityACampaignDetailResponseDto>(campaignDetailResult, campaignDetailCtx);

            Assert.Equal(enqueue.CampaignId, campaignDetail.Item.CampaignId);
            Assert.False(campaignDetail.Item.DryRun);
            Assert.Equal(3, campaignDetail.Item.Items.Count);
            Assert.Contains(campaignDetail.Item.Items, item => item.DocPath == firstRel.Replace('\\', '/') && item.Queued && item.JobId is not null);
            Assert.Contains(campaignDetail.Item.Items, item =>
                item.DocPath == thirdRel.Replace('\\', '/')
                && item.Queued
                && item.JobId is not null
                && item.PreviewText is not null);
            Assert.Contains(campaignDetail.Item.Items, item =>
                item.DocPath == secondRel.Replace('\\', '/')
                && !item.Queued
                && item.Reason == "policy_blocked:auto_ingest_paused"
                && item.KeySectionTitles is not null
                && item.KeySectionTitles.Contains("Control Loop Overview")
                && item.HypotheticalQuestions is not null
                && item.HypotheticalQuestions.Any(question => question.Contains("Control Loop Overview", StringComparison.OrdinalIgnoreCase)));

            var campaignArtifactCtx = BuildAdminContext();
            var campaignArtifactResult = await AdminRuntimeEndpoints.CapabilityACampaignArtifactAsync(
                campaignArtifactCtx,
                ds,
                new StubHostEnvironment(),
                enqueue.CampaignId);
            var campaignArtifact = await ExecuteResultAsync<AdminRuntimeCapabilityACampaignDetailArtifactDto>(campaignArtifactResult, campaignArtifactCtx);

            Assert.Equal("capability_a_campaign_detail.json", campaignArtifact.Artifact);
            Assert.Equal(campaignDetail.Item.CampaignId, campaignArtifact.Item.CampaignId);
            Assert.Equal(campaignDetail.Item.Items.Count, campaignArtifact.Item.Items.Count);
            Assert.Contains(campaignArtifact.Item.Items, item =>
                item.DocPath == secondRel.Replace('\\', '/')
                && item.PreviewText is not null
                && item.SuggestedTags is not null
                && item.SuggestedTags.Contains("control")
                && item.HypotheticalQuestions is not null
                && item.HypotheticalQuestions.Count > 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CapabilityB_candidates_and_enqueue_flow_use_runtime_governance_and_summary_jobs()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var firstDocId = Guid.NewGuid();
            var secondDocId = Guid.NewGuid();
            var thirdDocId = Guid.NewGuid();
            var existingJobId = Guid.NewGuid();
            var failedJobId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES
  (@tenant, @doc1, 'ATEX/missing-summary.pdf', 'missing-summary.pdf', 'atex', 'indexed', now(), now(), 1, 1, false),
  (@tenant, @doc2, 'Programmation/stale-summary.pdf', 'stale-summary.pdf', 'programmation', 'indexed', now(), now(), 1, 1, false),
  (@tenant, @doc3, 'PLC/recent-failure-summary.pdf', 'recent-failure-summary.pdf', 'plc', 'indexed', now(), now(), 1, 1, false);

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
)
VALUES(
  @tenant, @doc2, 'medium', 'fr', 'deadbeef', 'stale summary', '{}'::jsonb, now(), now()
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, level, payload, created_at, finished_at, last_error
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'queued', @doc2, 'medium',
  '{"executionMode":"server_backoffice","source":"test_seed"}'::jsonb, now(), NULL, NULL
),
(
  @failedJobId, @tenant, 'summary.generate', 'failed', @doc3, 'medium',
  '{"executionMode":"server_backoffice","source":"capability_b"}'::jsonb, now(), now(), 'tei timeout'
);
""",
                    new
                    {
                        tenant = tenantId,
                        doc1 = firstDocId,
                        doc2 = secondDocId,
                        doc3 = thirdDocId,
                        jobId = existingJobId,
                        failedJobId
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            var requalifyCtx = BuildAdminContext();
            var requalifyResult = await AdminRuntimeEndpoints.RequalifyAsync(
                requalifyCtx,
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true));
            var requalify = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(requalifyResult, requalifyCtx);
            var capabilityBState = Assert.Single(requalify.Items);

            Assert.Equal("capability_b.backoffice_generation", capabilityBState.Key);
            Assert.True(capabilityBState.Implemented);
            Assert.True(capabilityBState.Configured);
            Assert.True(capabilityBState.Healthy);
            Assert.True(capabilityBState.Qualified);
            Assert.True(capabilityBState.Selected);
            Assert.Equal("server-capability-b", capabilityBState.RuntimeKey);
            Assert.Equal(3, capabilityBState.PassCount);
            Assert.NotNull(capabilityBState.Details);

            var capabilityBDetails = JsonSerializer.SerializeToElement(capabilityBState.Details);
            var capabilityBChecks = capabilityBDetails.GetProperty("checks");
            Assert.Equal(JsonValueKind.Array, capabilityBChecks.ValueKind);
            Assert.True(capabilityBChecks.GetArrayLength() > 0);
            Assert.True(capabilityBChecks[0].TryGetProperty("llm", out var llmCheck));
            Assert.Equal("ok", llmCheck.GetProperty("status").GetString());
            Assert.True(capabilityBChecks[0].GetProperty("checksSummary").GetProperty("llmPassed").GetBoolean());

            var diagnosticsCtx = BuildAdminContext();
            var diagnosticsResult = await AdminRuntimeEndpoints.DiagnosticsAsync(
                diagnosticsCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());
            var diagnostics = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnosticsResult, diagnosticsCtx);
            var capabilityB = Assert.Single(diagnostics.Items, item => item.Key == "capability_b.backoffice_generation");
            Assert.Equal("selected", capabilityB.Status);
            Assert.Contains(capabilityB.Recommendations, item => item.Contains("review capability B candidates", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(capabilityB.OperationalSummary);
            Assert.Equal(3, capabilityB.OperationalSummary!.CandidateCount);
            Assert.Equal(1, capabilityB.OperationalSummary.ReadyToEnqueueCount);
            Assert.Equal(1, capabilityB.OperationalSummary.BlockedByActiveJobCount);
            Assert.Equal(1, capabilityB.OperationalSummary.BlockedByCooldownCount);
            Assert.Equal(0, capabilityB.OperationalSummary.ActiveCapabilityJobCount);
            Assert.Equal(0, capabilityB.OperationalSummary.TotalCampaignCount);
            Assert.Equal(0, capabilityB.OperationalSummary.ActiveCampaignCount);
            Assert.Equal(0, capabilityB.OperationalSummary.StoredSummaryCount);
            Assert.NotNull(diagnostics.Summary.Operational);
            Assert.Equal(3, diagnostics.Summary.Operational!.CapabilityBBacklogCount);
            Assert.Equal(1, diagnostics.Summary.Operational.CapabilityBReadyToEnqueueCount);
            Assert.Equal(0, diagnostics.Summary.Operational.CapabilityBActiveJobCount);

            var candidatesCtx = BuildAdminContext();
            var candidatesResult = await AdminRuntimeEndpoints.CapabilityBBackofficeCandidatesAsync(
                candidatesCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                category: null,
                limit: 20);
            var candidates = await ExecuteResultAsync<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>(candidatesResult, candidatesCtx);

            Assert.Equal("capability_b.backoffice_generation", candidates.CapabilityKey);
            Assert.Equal(3, candidates.TotalCandidates);
            Assert.Contains(candidates.Items, item =>
                item.DocPath == "ATEX/missing-summary.pdf"
                && item.Reasons.Contains("summary_missing")
                && item.RecommendedAction == "enqueue_summary_generation"
                && !item.PolicyBlocked);
            Assert.Contains(candidates.Items, item => item.DocPath == "Programmation/stale-summary.pdf" && item.Reasons.Contains("summary_stale") && item.HasActiveJob);
            Assert.Contains(candidates.Items, item =>
                item.DocPath == "PLC/recent-failure-summary.pdf"
                && item.Reasons.Contains("summary_missing")
                && item.Reasons.Contains("recent_summary_failure")
                && item.RecommendedAction == "inspect_recent_summary_failure"
                && item.PolicyBlocked
                && item.PolicyBlockReason == "recent_summary_job_failure"
                && item.LastJobStatus == "failed"
                && item.LastJobError == "tei timeout");
            Assert.Equal("ATEX/missing-summary.pdf", candidates.Items[0].DocPath);

            var candidatesArtifactCtx = BuildAdminContext();
            var candidatesArtifactResult = await AdminRuntimeEndpoints.CapabilityBCandidatesArtifactAsync(
                candidatesArtifactCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                category: null,
                limit: 20);
            var candidatesArtifact = await ExecuteResultAsync<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>(candidatesArtifactResult, candidatesArtifactCtx);

            Assert.Equal("capability_b_candidates.json", candidatesArtifact.Artifact);
            Assert.Equal(candidates.TotalCandidates, candidatesArtifact.TotalCandidates);

            var dryRunCtx = BuildAdminContext();
            var dryRunResult = await AdminRuntimeEndpoints.CapabilityBEnqueueAsync(
                dryRunCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(
                    DocPaths: ["ATEX/missing-summary.pdf", "Programmation/stale-summary.pdf", "PLC/recent-failure-summary.pdf"],
                    DryRun: true));
            var dryRun = await ExecuteResultAsync<AdminRuntimeCapabilityBEnqueueResponseDto>(dryRunResult, dryRunCtx);

            Assert.True(dryRun.DryRun);
            Assert.Equal(3, dryRun.CandidateCount);
            Assert.Equal(1, dryRun.PlannedCount);
            Assert.Equal(0, dryRun.QueuedCount);
            Assert.Equal(3, dryRun.SkippedCount);
            Assert.Contains(dryRun.Items, item => item.DocPath == "ATEX/missing-summary.pdf" && item.Reason == "dry_run_preview");
            Assert.Contains(dryRun.Items, item => item.DocPath == "Programmation/stale-summary.pdf" && item.Reason == "active_summary_job_exists");
            Assert.Contains(dryRun.Items, item => item.DocPath == "PLC/recent-failure-summary.pdf" && item.Reason == "recent_summary_job_failure");
            Assert.Equal(2, dryRun.ReasonCounts["summary_missing"]);
            Assert.Equal(1, dryRun.ReasonCounts["summary_stale"]);
            Assert.Equal(1, dryRun.ReasonCounts["recent_summary_failure"]);
            Assert.Equal(1, dryRun.ReasonCounts["summary_job_active"]);
            Assert.Equal(1, dryRun.ReasonCounts["blocked:active_summary_job_exists"]);
            Assert.Equal(1, dryRun.ReasonCounts["blocked:recent_summary_job_failure"]);

            var enqueueCtx = BuildAdminContext();
            var enqueueResult = await AdminRuntimeEndpoints.CapabilityBEnqueueAsync(
                enqueueCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(
                    DocPaths: ["ATEX/missing-summary.pdf", "Programmation/stale-summary.pdf", "PLC/recent-failure-summary.pdf"]));
            var enqueue = await ExecuteResultAsync<AdminRuntimeCapabilityBEnqueueResponseDto>(enqueueResult, enqueueCtx);

            Assert.False(enqueue.DryRun);
            Assert.Equal(3, enqueue.CandidateCount);
            Assert.Equal(1, enqueue.PlannedCount);
            Assert.Equal(1, enqueue.QueuedCount);
            Assert.Equal(2, enqueue.SkippedCount);
            Assert.Contains(enqueue.Items, item => item.DocPath == "ATEX/missing-summary.pdf" && item.Queued && item.JobId is not null);
            Assert.Contains(enqueue.Items, item => item.DocPath == "Programmation/stale-summary.pdf" && !item.Queued && item.Reason == "active_summary_job_exists");
            Assert.Contains(enqueue.Items, item => item.DocPath == "PLC/recent-failure-summary.pdf" && !item.Queued && item.Reason == "recent_summary_job_failure");

            var forceCtx = BuildAdminContext();
            var forceResult = await AdminRuntimeEndpoints.CapabilityBEnqueueAsync(
                forceCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(
                    DocPaths: ["PLC/recent-failure-summary.pdf"],
                    Force: true));
            var forceEnqueue = await ExecuteResultAsync<AdminRuntimeCapabilityBEnqueueResponseDto>(forceResult, forceCtx);
            Assert.True(forceEnqueue.Force);
            Assert.Equal(1, forceEnqueue.CandidateCount);
            Assert.Equal(1, forceEnqueue.PlannedCount);
            Assert.Equal(1, forceEnqueue.QueuedCount);
            Assert.Equal(0, forceEnqueue.SkippedCount);
            Assert.Contains(forceEnqueue.Items, item => item.DocPath == "PLC/recent-failure-summary.pdf" && item.Queued && item.JobId is not null);

            var postEnqueueDiagnosticsCtx = BuildAdminContext();
            var postEnqueueDiagnosticsResult = await AdminRuntimeEndpoints.DiagnosticsAsync(
                postEnqueueDiagnosticsCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());
            var postEnqueueDiagnostics = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(postEnqueueDiagnosticsResult, postEnqueueDiagnosticsCtx);
            var postEnqueueCapabilityB = Assert.Single(postEnqueueDiagnostics.Items, item => item.Key == "capability_b.backoffice_generation");
            Assert.NotNull(postEnqueueCapabilityB.OperationalSummary);
            Assert.Equal(3, postEnqueueCapabilityB.OperationalSummary!.CandidateCount);
            Assert.Equal(0, postEnqueueCapabilityB.OperationalSummary.ReadyToEnqueueCount);
            Assert.Equal(2, postEnqueueCapabilityB.OperationalSummary.BlockedByActiveJobCount);
            Assert.Equal(0, postEnqueueCapabilityB.OperationalSummary.BlockedByCooldownCount);
            Assert.Equal(3, postEnqueueCapabilityB.OperationalSummary.ActiveCapabilityJobCount);
            Assert.Equal(3, postEnqueueCapabilityB.OperationalSummary.BlockedByActiveJobCount);
            Assert.True(postEnqueueCapabilityB.OperationalSummary.TotalCampaignCount >= 2);
            Assert.True(postEnqueueCapabilityB.OperationalSummary.ActiveCampaignCount >= 1);
            Assert.Equal(0, postEnqueueCapabilityB.OperationalSummary.LatestCampaignProgressPercent);
            Assert.NotNull(postEnqueueDiagnostics.Summary.Operational);
            Assert.Equal(3, postEnqueueDiagnostics.Summary.Operational!.CapabilityBBacklogCount);
            Assert.Equal(0, postEnqueueDiagnostics.Summary.Operational.CapabilityBReadyToEnqueueCount);
            Assert.Equal(3, postEnqueueDiagnostics.Summary.Operational.CapabilityBActiveJobCount);
            Assert.Equal(0, postEnqueueDiagnostics.Summary.Operational.CapabilityBLatestCampaignProgressPercent);

            var operationalCtx = BuildAdminContext();
            var operationalResult = await AdminRuntimeEndpoints.OperationalSummaryAsync(
                operationalCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());
            var operational = await ExecuteResultAsync<AdminRuntimeOperationalSummaryResponseDto>(operationalResult, operationalCtx);
            Assert.Equal(3, operational.Summary.CapabilityBBacklogCount);
            Assert.Equal(0, operational.Summary.CapabilityBReadyToEnqueueCount);
            Assert.Equal(3, operational.Summary.CapabilityBActiveJobCount);
            Assert.Equal(0, operational.Summary.CapabilityBLatestCampaignProgressPercent);
            var operationalB = Assert.Single(operational.Items, item => item.Key == "capability_b.backoffice_generation");
            Assert.Equal("selected", operationalB.Status);
            Assert.Equal(3, operationalB.Summary.ActiveCapabilityJobCount);
            Assert.Contains(operationalB.Recommendations, item => item.Contains("review capability B candidates", StringComparison.OrdinalIgnoreCase));

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var queuedJobs = await conn.ExecuteScalarAsync<int>(
                    """
SELECT COUNT(*)
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_type='summary.generate'
  AND status='queued'
  AND payload ->> 'source' = 'capability_b';
""",
                    new { tenant = tenantId });
                Assert.Equal(2, queuedJobs);
            }

            var eventsCtx = BuildAdminContext();
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var events = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            Assert.Contains(events.Items, item => item.EventType == "requalified");
            Assert.Contains(events.Items, item => item.EventType == "capability_b_enqueued");
            Assert.Contains(events.Items, item => item.EventType == "capability_b_campaign_dry_run");
            Assert.Contains(events.Items, item => item.EventType == "capability_b_campaign_executed");

            var campaignsCtx = BuildAdminContext();
            var campaignsResult = await AdminRuntimeEndpoints.CapabilityBCampaignsAsync(
                campaignsCtx,
                ds,
                new StubHostEnvironment(),
                limit: 10);
            var campaigns = await ExecuteResultAsync<AdminRuntimeCapabilityBCampaignsResponseDto>(campaignsResult, campaignsCtx);
            Assert.Equal("capability_b.backoffice_generation", campaigns.CapabilityKey);
            Assert.True(campaigns.Items.Count >= 3);
            Assert.Contains(campaigns.Items, item => item.DryRun && item.Status == "dry_run" && item.QueuedCount == 0);
            Assert.Contains(campaigns.Items, item => !item.DryRun && item.Status == "executed" && item.QueuedCount == 1);
            Assert.Contains(campaigns.Items, item => item.Force && item.Status == "executed" && item.QueuedCount == 1);

            var campaignDetailCtx = BuildAdminContext();
            var campaignDetailResult = await AdminRuntimeEndpoints.CapabilityBCampaignAsync(
                campaignDetailCtx,
                ds,
                new StubHostEnvironment(),
                enqueue.CampaignId);
            var campaignDetail = await ExecuteResultAsync<AdminRuntimeCapabilityBCampaignDetailResponseDto>(campaignDetailResult, campaignDetailCtx);
            Assert.Equal(enqueue.CampaignId, campaignDetail.Item.CampaignId);
            Assert.Equal(3, campaignDetail.Item.Items.Count);
            Assert.Contains(campaignDetail.Item.Items, item => item.DocPath == "ATEX/missing-summary.pdf" && item.Queued && item.JobId is not null);
            Assert.Contains(campaignDetail.Item.Items, item => item.DocPath == "Programmation/stale-summary.pdf" && !item.Queued && item.Reason == "active_summary_job_exists");
            Assert.Contains(campaignDetail.Item.Items, item => item.DocPath == "PLC/recent-failure-summary.pdf" && !item.Queued && item.Reason == "recent_summary_job_failure");

            var campaignArtifactCtx = BuildAdminContext();
            var campaignArtifactResult = await AdminRuntimeEndpoints.CapabilityBCampaignArtifactAsync(
                campaignArtifactCtx,
                ds,
                new StubHostEnvironment(),
                enqueue.CampaignId);
            var campaignArtifact = await ExecuteResultAsync<AdminRuntimeCapabilityBCampaignDetailArtifactDto>(campaignArtifactResult, campaignArtifactCtx);
            Assert.Equal("capability_b_campaign_detail.json", campaignArtifact.Artifact);
            Assert.Equal(campaignDetail.Item.CampaignId, campaignArtifact.Item.CampaignId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task RequalifyAsync_marks_capability_b_not_qualified_when_llm_runtime_probe_fails()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var ctx = BuildAdminContext();
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

            var result = await AdminRuntimeEndpoints.RequalifyAsync(
                ctx,
                ds,
                new BrokenBackofficeRuntimeGovernanceHttpClientFactory(),
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true));

            var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
            var state = Assert.Single(payload.Items);

            Assert.Equal("capability_b.backoffice_generation", state.Key);
            Assert.True(state.Configured);
            Assert.False(state.Healthy);
            Assert.False(state.Qualified);
            Assert.False(state.Authorized);
            Assert.False(state.Selected);
            Assert.Equal(3, state.PassCount);
            Assert.Contains("llm chat completion check failed", state.LastError, StringComparison.OrdinalIgnoreCase);

            var details = JsonSerializer.SerializeToElement(state.Details);
            var checks = details.GetProperty("checks");
            Assert.True(checks.GetArrayLength() > 0);
            Assert.Equal("http_error", checks[0].GetProperty("llm").GetProperty("status").GetString());
            Assert.False(checks[0].GetProperty("checksSummary").GetProperty("llmPassed").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task CapabilitiesAsync_marks_core_retrieval_as_stale_when_qualification_inputs_change()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var changedRag = CreateRagOptions();
        changedRag.EmbeddingsModel = "intfloat/multilingual-e5-large";

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilitiesAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(changedRag),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilitiesResponseDto>(result, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.True(retrieval.Stale);
        Assert.False(retrieval.Authorized);
        Assert.False(retrieval.Selected);
        Assert.True(retrieval.PersistedAuthorized);
        Assert.True(retrieval.PersistedSelected);
        Assert.False(retrieval.EffectiveAuthorized);
        Assert.False(retrieval.EffectiveSelected);
        Assert.Equal("qualification_inputs_changed", retrieval.StaleReason);
        Assert.NotNull(retrieval.QualificationFingerprint);

        var details = JsonSerializer.SerializeToElement(retrieval.Details);
        Assert.Equal("stale", details.GetProperty("qualificationFreshness").GetString());
        Assert.Equal("qualification_inputs_changed", details.GetProperty("staleQualificationReason").GetString());
        Assert.True(details.GetProperty("persistedAuthorized").GetBoolean());
        Assert.True(details.GetProperty("persistedSelected").GetBoolean());
    }

    [Fact]
    public async Task CapabilitiesAsync_marks_core_retrieval_as_stale_when_qualification_has_expired()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var options = new RuntimeGovernanceOptions
        {
            MaxQualificationAgeHours = 1
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            options,
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE runtime_capability_state SET last_qualified_at = @ts, last_checked_at = @ts WHERE capability_key='core.retrieval';",
                new { ts = DateTimeOffset.UtcNow.AddHours(-5).UtcDateTime });
        }

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilitiesAsync(
            ctx,
            ds,
            Options.Create(options),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilitiesResponseDto>(result, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.True(retrieval.Stale);
        Assert.Equal("qualification_expired", retrieval.StaleReason);
        Assert.NotNull(retrieval.QualificationAgeHours);
        Assert.NotNull(retrieval.QualificationExpiresAt);

        var details = JsonSerializer.SerializeToElement(retrieval.Details);
        Assert.Equal("qualification_expired", details.GetProperty("staleQualificationReason").GetString());
        Assert.True(details.TryGetProperty("qualificationAgeHours", out _));
        Assert.True(details.TryGetProperty("qualificationExpiresAt", out _));
    }

    [Fact]
    public async Task UpdateSelectionAsync_can_deselect_qualified_core_retrieval()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(Selected: false));

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityStateDto>(result, ctx);
        Assert.False(payload.Selected);
        Assert.True(payload.Authorized);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var persistedSelected = await conn.ExecuteScalarAsync<bool>(
            "SELECT selected FROM runtime_capability_state WHERE capability_key='core.retrieval';");
        Assert.False(persistedSelected);
    }

    [Fact]
    public async Task UpdateSelectionAsync_disabling_capability_clears_authorization_and_selection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(DesiredEnabled: false));

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityStateDto>(result, ctx);
        Assert.False(payload.DesiredEnabled);
        Assert.False(payload.Authorized);
        Assert.False(payload.Selected);
        Assert.NotNull(payload.Details);

        var details = JsonSerializer.SerializeToElement(payload.Details);
        Assert.True(details.TryGetProperty("selectionPolicy", out var selectionPolicy));
        Assert.False(selectionPolicy.GetProperty("authorized").GetBoolean());
        Assert.False(selectionPolicy.GetProperty("selected").GetBoolean());
    }

    [Fact]
    public async Task UpdateSelectionAsync_rejects_selecting_unqualified_capability()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(Authorized: true, Selected: true));

        var payload = await ExecuteAnonymousAsync(result, ctx);
        Assert.Equal("capability must be qualified before it can be authorized", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateSelectionAsync_rejects_authorizing_capability_when_desired_enabled_is_false()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(DesiredEnabled: false, Authorized: true));

        var payload = await ExecuteAnonymousAsync(result, ctx);
        Assert.Equal("capability must be desired-enabled before it can be authorized", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateSelectionAsync_rejects_stale_capability_until_it_is_requalified()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var changedRag = CreateRagOptions();
        changedRag.QdrantCollection = "knowledge_base_v2";

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(changedRag),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(Authorized: true, Selected: true));

        var payload = await ExecuteAnonymousAsync(result, ctx);
        Assert.Equal("capability must be requalified because its qualification is stale", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WarmupResultsAsync_returns_recent_runtime_warmup_rows()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.WarmupResultsAsync(
            ctx,
            ds,
            new StubHostEnvironment(),
            "core.retrieval",
            10);

        var payload = await ExecuteResultAsync<AdminRuntimeWarmupResultsResponseDto>(result, ctx);
        Assert.Single(payload.Items);
        Assert.Equal("core.retrieval", payload.Items[0].CapabilityKey);
        Assert.True(payload.Items[0].Passed);
    }

    [Fact]
    public async Task CapabilityStateArtifactAsync_returns_persisted_runtime_state_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilityStateArtifactAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityStateArtifactDto>(result, ctx);
        Assert.Equal("capability_state.json", payload.Artifact);
        Assert.Contains(payload.Items, item => item.Key == "core.retrieval" && item.Qualified && item.Details is not null);
    }

    [Fact]
    public async Task WarmupResultsArtifactAsync_returns_recent_runtime_warmup_artifact_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.WarmupResultsArtifactAsync(
            ctx,
            ds,
            new StubHostEnvironment(),
            "core.retrieval",
            10);

        var payload = await ExecuteResultAsync<AdminRuntimeWarmupResultsArtifactDto>(result, ctx);
        Assert.Equal("warmup_results.json", payload.Artifact);
        Assert.Single(payload.Items);
        Assert.Equal("core.retrieval", payload.Items[0].CapabilityKey);
    }

    [Fact]
    public async Task DiagnosticsArtifactAsync_returns_runtime_diagnostic_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.DiagnosticsArtifactAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsArtifactDto>(result, ctx);
        Assert.Equal("diagnostics.json", payload.Artifact);
        Assert.True(payload.Summary.TotalCapabilities >= 4);
        Assert.Contains(payload.Items, item => item.Key == "core.retrieval");
    }

    [Fact]
    public async Task RetrievalKpisAsync_returns_cdc_thresholds_and_metric_guide()
    {
        var ctx = BuildAdminContext();
        var options = new RuntimeGovernanceOptions
        {
            RetrievalKpiObservationWindowMinutes = 20,
            RetrievalP95TargetMs = 800,
            RerankP95TargetMs = 300,
            ZeroResultRateTargetPercent = 5
        };

        var result = await AdminRuntimeEndpoints.RetrievalKpisAsync(
            ctx,
            new StubHostEnvironment(),
            Options.Create(options));

        var payload = await ExecuteResultAsync<AdminRuntimeRetrievalKpisResponseDto>(result, ctx);
        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Equal(20, payload.Policy.ObservationWindowMinutes);
        Assert.Equal(800, payload.Policy.RetrievalP95TargetMs);
        Assert.Equal(300, payload.Policy.RerankP95TargetMs);
        Assert.Equal(5, payload.Policy.ZeroResultRateTargetPercent);
        Assert.Contains("zero_results", payload.Policy.ZeroResultRateFormula, StringComparison.Ordinal);
        Assert.Contains(payload.Metrics, item => item.Key == "retrieval_p95" && item.Instrument == "saaia.retrieval.duration" && item.Aggregation == "p95");
        Assert.Contains(payload.Metrics, item => item.Key == "rerank_p95" && item.Instrument == "saaia.retrieval.rerank.duration");
        Assert.Contains(payload.Metrics, item => item.Key == "zero_result_rate" && item.Aggregation == "ratio");
        Assert.Contains(payload.Alerts, item => item.Key == "retrieval_p95_regression" && item.Condition.Contains("800", StringComparison.Ordinal));
        Assert.Contains(payload.Alerts, item => item.Key == "zero_result_rate_regression" && item.Condition.Contains("5", StringComparison.Ordinal));
        Assert.Contains(payload.DashboardPanels, item => item.Contains("Retrieval P95", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetrievalKpisArtifactAsync_returns_named_artifact_snapshot()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.RetrievalKpisArtifactAsync(
            ctx,
            new StubHostEnvironment(),
            Options.Create(new RuntimeGovernanceOptions()));

        var payload = await ExecuteResultAsync<AdminRuntimeRetrievalKpisArtifactDto>(result, ctx);
        Assert.Equal("retrieval-kpis.json", payload.Artifact);
        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Contains(payload.Metrics, item => item.Instrument == "saaia.retrieval.requests");
        Assert.Contains(payload.Alerts, item => item.Key == "rerank_p95_regression");
    }

    [Fact]
    public async Task CapabilityBKpisAsync_returns_ops_thresholds_and_metric_guide()
    {
        var ctx = BuildAdminContext();
        var options = new RuntimeGovernanceOptions
        {
            CapabilityBKpiObservationWindowMinutes = 25,
            CapabilityBGenerationP95TargetMs = 2800,
            CapabilityBLiveFallbackRateTargetPercent = 4,
            CapabilityBFailureRateTargetPercent = 6,
            CapabilityBQualityScoreTarget = 0.72
        };

        var result = await AdminRuntimeEndpoints.CapabilityBKpisAsync(
            ctx,
            new StubHostEnvironment(),
            Options.Create(options));

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityBKpisResponseDto>(result, ctx);
        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Equal(25, payload.Policy.ObservationWindowMinutes);
        Assert.Equal(2800, payload.Policy.GenerationP95TargetMs);
        Assert.Equal(4, payload.Policy.LiveFallbackRateTargetPercent);
        Assert.Equal(6, payload.Policy.FailureRateTargetPercent);
        Assert.Equal(0.72, payload.Policy.QualityScoreTarget);
        Assert.Contains("runtime_unavailable", payload.Policy.LiveFallbackRateFormula, StringComparison.Ordinal);
        Assert.Contains("failed_docs", payload.Policy.FailureRateFormula, StringComparison.Ordinal);
        Assert.Contains(payload.Metrics, item => item.Key == "capability_b_generation_p95" && item.Instrument == "saaia.runtime.capability_b.summary_generation.duration");
        Assert.Contains(payload.Metrics, item => item.Key == "capability_b_live_fallback_rate" && item.Instrument == "saaia.runtime.capability_b.execution_decisions");
        Assert.Contains(payload.Metrics, item => item.Key == "capability_b_first_response_p95" && item.Instrument == "saaia.runtime.capability_b.summary_generation.first_response");
        Assert.Contains(payload.Metrics, item => item.Key == "capability_b_output_length_p50" && item.Aggregation == "p50");
        Assert.Contains(payload.Metrics, item => item.Key == "capability_b_quality_score_p50" && item.Instrument == "saaia.runtime.capability_b.summary_generation.quality_score");
        Assert.Contains(payload.Alerts, item => item.Key == "capability_b_generation_p95_regression" && item.Condition.Contains("2800", StringComparison.Ordinal));
        Assert.Contains(payload.Alerts, item => item.Key == "capability_b_live_fallback_rate_regression" && item.Condition.Contains("4", StringComparison.Ordinal));
        Assert.Contains(payload.Alerts, item => item.Key == "capability_b_quality_score_regression" && item.Condition.Contains("quality_score", StringComparison.Ordinal));
        Assert.Contains(payload.DashboardPanels, item => item.Contains("Capability B generation P95", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CapabilityBKpisArtifactAsync_returns_named_artifact_snapshot()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilityBKpisArtifactAsync(
            ctx,
            new StubHostEnvironment(),
            Options.Create(new RuntimeGovernanceOptions()));

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityBKpisArtifactDto>(result, ctx);
        Assert.Equal("capability-b-kpis.json", payload.Artifact);
        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Contains(payload.Metrics, item => item.Instrument == "saaia.runtime.capability_b.execution_decisions");
        Assert.Contains(payload.Metrics, item => item.Instrument == "saaia.runtime.capability_b.summary_generation.first_response");
        Assert.Contains(payload.Metrics, item => item.Instrument == "saaia.runtime.capability_b.summary_generation.quality_score");
        Assert.Contains(payload.Alerts, item => item.Key == "capability_b_failure_rate_regression");
        Assert.Contains(payload.Alerts, item => item.Key == "capability_b_quality_score_regression");
    }

    [Fact]
    public async Task CapabilityBQualityReviewAsync_returns_only_low_quality_capability_b_summaries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var lowDocId = Guid.NewGuid();
        var highDocId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version, auto_ingest_paused
)
VALUES
  (@tenant, @lowDocId, 'ATEX/low-quality.pdf', 'low-quality.pdf', 'atex', 'indexed', now(), now(), 1, 1, false),
  (@tenant, @highDocId, 'ATEX/high-quality.pdf', 'high-quality.pdf', 'atex', 'indexed', now(), now(), 1, 1, false);

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
)
VALUES
(
  @tenant,
  @lowDocId,
  'medium',
  'fr',
  'hash-low',
  'Summary with thin coverage.',
  '{
    "generator":"capability_b_worker_v2",
    "strategy":"llm_document_foundation",
    "runtimeCapabilityStatus":"selected",
    "qualityScore":0.41,
    "fallbackUsed":false,
    "qualitySignals":{
      "lineCount":1,
      "lengthScore":0.35,
      "structureScore":0.4,
      "sectionCoverageScore":0.25,
      "matchedSectionCount":1,
      "expectedSectionCount":4,
      "keywordCoverageScore":0.25,
      "matchedKeywordCount":1,
      "expectedKeywordCount":4
    }
  }'::jsonb,
  now(),
  now()
),
(
  @tenant,
  @highDocId,
  'medium',
  'fr',
  'hash-high',
  'High quality summary with better coverage and structure.',
  '{
    "generator":"capability_b_worker_v2",
    "strategy":"llm_document_foundation",
    "runtimeCapabilityStatus":"selected",
    "qualityScore":0.88,
    "fallbackUsed":false,
    "qualitySignals":{
      "lineCount":3,
      "lengthScore":1.0,
      "structureScore":1.0,
      "sectionCoverageScore":0.75,
      "matchedSectionCount":3,
      "expectedSectionCount":4,
      "keywordCoverageScore":0.75,
      "matchedKeywordCount":3,
      "expectedKeywordCount":4
    }
  }'::jsonb,
  now(),
  now()
);
""",
                new { tenant = tenantId, lowDocId, highDocId });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var options = new RuntimeGovernanceOptions
        {
            CapabilityBQualityScoreTarget = 0.60
        };
        var ctx = BuildAdminContext();

        var result = await AdminRuntimeEndpoints.CapabilityBQualityReviewAsync(
            ctx,
            ds,
            Options.Create(options),
            new StubHostEnvironment(),
            limit: 20);

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityBQualityReviewResponseDto>(result, ctx);
        Assert.Equal("capability_b.backoffice_generation", payload.CapabilityKey);
        Assert.Equal(0.60, payload.QualityThreshold);
        Assert.Equal(1, payload.TotalItems);
        var item = Assert.Single(payload.Items);
        Assert.Equal(lowDocId, item.DocId);
        Assert.Equal("ATEX/low-quality.pdf", item.DocPath);
        Assert.Equal(0.41, item.QualityScore);
        Assert.Equal("high", item.Severity);
        Assert.Equal("regenerate_with_context_review", item.RecommendedAction);
        Assert.Equal("llm_document_foundation", item.Strategy);
        Assert.Contains(item.Recommendations, recommendation => recommendation.Contains("section coverage", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0.25, item.Signals.SectionCoverageScore);
    }

    [Fact]
    public async Task CapabilityBQualityReviewArtifactAsync_returns_named_artifact_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/quality-artifact.pdf', 'quality-artifact.pdf', 'atex', 'indexed', now(), now(), 1, 1, false
);

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
)
VALUES(
  @tenant,
  @docId,
  'medium',
  'fr',
  'hash-artifact',
  'Fallback summary for quality artifact review.',
  '{
    "generator":"capability_b_worker_v2",
    "strategy":"deterministic_document_foundation",
    "fallbackUsed":true,
    "fallbackReason":"llm_empty_response",
    "runtimeCapabilityStatus":"runtime_unavailable",
    "qualityScore":0.38,
    "qualitySignals":{
      "lineCount":1,
      "lengthScore":0.35,
      "structureScore":0.4,
      "sectionCoverageScore":0.2,
      "matchedSectionCount":1,
      "expectedSectionCount":5,
      "keywordCoverageScore":0.2,
      "matchedKeywordCount":1,
      "expectedKeywordCount":5
    }
  }'::jsonb,
  now(),
  now()
);
""",
                new { tenant = tenantId, docId });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilityBQualityReviewArtifactAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment(),
            limit: 20);

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityBQualityReviewArtifactDto>(result, ctx);
        Assert.Equal("capability_b_quality_review.json", payload.Artifact);
        Assert.Equal("capability_b.backoffice_generation", payload.CapabilityKey);
        Assert.Equal(1, payload.TotalItems);
        var item = Assert.Single(payload.Items);
        Assert.True(item.FallbackUsed);
        Assert.Equal("llm_empty_response", item.FallbackReason);
        Assert.Contains(item.Recommendations, recommendation => recommendation.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CapabilityBQualityReviewSummaryAsync_returns_aggregate_snapshot_for_low_quality_summaries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docA = Guid.NewGuid();
        var docB = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version, auto_ingest_paused
)
VALUES
  (@tenant, @docA, 'ATEX/quality-summary-a.pdf', 'quality-summary-a.pdf', 'atex', 'indexed', now(), now(), 1, 1, false),
  (@tenant, @docB, 'ATEX/quality-summary-b.pdf', 'quality-summary-b.pdf', 'atex', 'indexed', now(), now(), 1, 1, false);

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
)
VALUES
(
  @tenant,
  @docA,
  'medium',
  'fr',
  'hash-a',
  'Low quality LLM summary.',
  '{
    "generator":"capability_b_worker_v2",
    "strategy":"llm_document_foundation",
    "runtimeCapabilityStatus":"selected",
    "fallbackUsed":false,
    "qualityScore":0.42
  }'::jsonb,
  now() - interval ''2 minutes'',
  now() - interval ''2 minutes''
),
(
  @tenant,
  @docB,
  'medium',
  'fr',
  'hash-b',
  'Low quality fallback summary.',
  '{
    "generator":"capability_b_worker_v2",
    "strategy":"deterministic_document_foundation",
    "runtimeCapabilityStatus":"runtime_unavailable",
    "fallbackUsed":true,
    "fallbackReason":"llm_empty_response",
    "qualityScore":0.31
  }'::jsonb,
  now() - interval ''1 minutes'',
  now() - interval ''1 minutes''
);
""",
                new { tenant = tenantId, docA, docB });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var options = new RuntimeGovernanceOptions
        {
            CapabilityBQualityScoreTarget = 0.60
        };
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilityBQualityReviewSummaryAsync(
            ctx,
            ds,
            Options.Create(options),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityBQualityReviewSummaryResponseDto>(result, ctx);
        Assert.Equal("capability_b.backoffice_generation", payload.CapabilityKey);
        Assert.Equal(0.60, payload.QualityThreshold);
        Assert.Equal(2, payload.Summary.TotalLowQualitySummaries);
        Assert.Equal(1, payload.Summary.FallbackSummaryCount);
        Assert.Equal(1, payload.Summary.LiveLlmSummaryCount);
        Assert.Equal(1, payload.Summary.RuntimeUnavailableCount);
        Assert.Equal(0.31, payload.Summary.LowestQualityScore);
        Assert.NotNull(payload.Summary.LatestUpdatedAt);
        Assert.Contains(payload.Summary.StrategyCounts, item => item.Key == "deterministic_document_foundation" && item.Count == 1);
        Assert.Contains(payload.Summary.StrategyCounts, item => item.Key == "llm_document_foundation" && item.Count == 1);
        Assert.Contains(payload.Summary.RuntimeStatusCounts, item => item.Key == "runtime_unavailable" && item.Count == 1);
        Assert.Contains(payload.Summary.Recommendations, item => item.Contains("runtime availability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CapabilityBQualityReviewSummaryArtifactAsync_returns_named_artifact_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilityBQualityReviewSummaryArtifactAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityBQualityReviewSummaryArtifactDto>(result, ctx);
        Assert.Equal("capability_b_quality_review_summary.json", payload.Artifact);
        Assert.Equal("capability_b.backoffice_generation", payload.CapabilityKey);
        Assert.NotNull(payload.Summary);
    }

    [Fact]
    public async Task OperationalSummaryArtifactAsync_returns_runtime_operational_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.OperationalSummaryArtifactAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeOperationalSummaryArtifactDto>(result, ctx);
        Assert.Equal("operational_summary.json", payload.Artifact);
        Assert.Equal(0, payload.Summary.CapabilityACandidateCount);
        Assert.Equal(0, payload.Summary.CapabilityBBacklogCount);
        Assert.Contains(payload.Items, item => item.Key == "capability_a.corpus_enrichment");
        Assert.Contains(payload.Items, item => item.Key == "capability_b.backoffice_generation");
    }

    [Fact]
    public void AdminRuntime_dto_contracts_preserve_expected_top_level_json_shape()
    {
        AssertDtoJsonPropertyNames<AdminRuntimeCatalogResponseDto>("cdcAlignment", "environment", "runtimes", "warmupProfiles", "capabilities");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilitiesResponseDto>("cdcAlignment", "environment", "items", "warmupResults");
        AssertDtoJsonPropertyNames<AdminRuntimeRequalifyResponseDto>("cdcAlignment", "environment", "profileKey", "items", "warmupResults");
        AssertDtoJsonPropertyNames<AdminRuntimeReconcileStaleResponseDto>("cdcAlignment", "environment", "updatedCount", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeWarmupResultsResponseDto>("cdcAlignment", "environment", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeEventsResponseDto>("cdcAlignment", "environment", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeDiagnosticsResponseDto>("cdcAlignment", "environment", "generatedAt", "summary", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeRetrievalKpisResponseDto>("cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBKpisResponseDto>("cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBQualityReviewResponseDto>("cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "totalItems", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBQualityReviewSummaryResponseDto>("cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "summary");
        AssertDtoJsonPropertyNames<AdminRuntimeOperationalSummaryResponseDto>("cdcAlignment", "environment", "generatedAt", "summary", "items");

        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>("cdcAlignment", "environment", "capabilityKey", "profileKey", "totalCandidates", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "profileKey", "totalCandidates", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityACampaignsResponseDto>("cdcAlignment", "environment", "capabilityKey", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityACampaignsArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityACampaignDetailResponseDto>("cdcAlignment", "environment", "capabilityKey", "item");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityACampaignDetailArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "item");

        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>("cdcAlignment", "environment", "capabilityKey", "profileKey", "totalCandidates", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "profileKey", "totalCandidates", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBCampaignsResponseDto>("cdcAlignment", "environment", "capabilityKey", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBCampaignsArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBCampaignDetailResponseDto>("cdcAlignment", "environment", "capabilityKey", "item");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBCampaignDetailArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "item");

        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityAEnqueueResponseDto>("cdcAlignment", "environment", "capabilityKey", "campaignId", "dryRun", "allowUnsafeCandidates", "candidateCount", "plannedCount", "queuedCount", "skippedCount", "reasonCounts", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBEnqueueResponseDto>("cdcAlignment", "environment", "capabilityKey", "campaignId", "dryRun", "force", "candidateCount", "plannedCount", "queuedCount", "skippedCount", "reasonCounts", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBClaimResponseDto>("cdcAlignment", "environment", "capabilityKey", "jobId", "docId", "docPath", "level", "executionMode", "runtimeCapabilityKey", "runtimeCapabilityStatus", "runtimeProfileKey", "campaignId", "leaseToken", "claimedBy", "claimedAt");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBFailResponseDto>("cdcAlignment", "environment", "capabilityKey", "jobId", "docId", "docPath", "level", "campaignId", "leaseToken", "failedBy", "lastError", "status", "failedAt");

        AssertDtoJsonPropertyNames<AdminRuntimeRuntimeCatalogArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "runtimes", "warmupProfiles", "capabilities");
        AssertDtoJsonPropertyNames<AdminRuntimeModelCatalogArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityStateArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeWarmupProfilesArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeWarmupResultsArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeEventsArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeDiagnosticsArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "summary", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeRetrievalKpisArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBKpisArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBQualityReviewArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "totalItems", "items");
        AssertDtoJsonPropertyNames<AdminRuntimeCapabilityBQualityReviewSummaryArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "summary");
        AssertDtoJsonPropertyNames<AdminRuntimeOperationalSummaryArtifactDto>("artifact", "cdcAlignment", "environment", "generatedAt", "summary", "items");
    }

    [Fact]
    public async Task AdminRuntime_core_endpoints_and_artifacts_preserve_expected_top_level_json_shape()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var catalogCtx = BuildAdminContext();
        var catalogResult = await AdminRuntimeEndpoints.CatalogAsync(
            catalogCtx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(catalogResult, catalogCtx, "cdcAlignment", "environment", "runtimes", "warmupProfiles", "capabilities");

        var capabilitiesCtx = BuildAdminContext();
        var capabilitiesResult = await AdminRuntimeEndpoints.CapabilitiesAsync(
            capabilitiesCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(capabilitiesResult, capabilitiesCtx, "cdcAlignment", "environment", "items", "warmupResults");

        var diagnosticsCtx = BuildAdminContext();
        var diagnosticsResult = await AdminRuntimeEndpoints.DiagnosticsAsync(
            diagnosticsCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(diagnosticsResult, diagnosticsCtx, "cdcAlignment", "environment", "generatedAt", "summary", "items");

        var retrievalKpisCtx = BuildAdminContext();
        var retrievalKpisResult = await AdminRuntimeEndpoints.RetrievalKpisAsync(
            retrievalKpisCtx,
            new StubHostEnvironment(),
            Options.Create(new RuntimeGovernanceOptions()));
        await AssertTopLevelJsonContractAsync(retrievalKpisResult, retrievalKpisCtx, "cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");

        var capabilityBKpisCtx = BuildAdminContext();
        var capabilityBKpisResult = await AdminRuntimeEndpoints.CapabilityBKpisAsync(
            capabilityBKpisCtx,
            new StubHostEnvironment(),
            Options.Create(new RuntimeGovernanceOptions()));
        await AssertTopLevelJsonContractAsync(capabilityBKpisResult, capabilityBKpisCtx, "cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");

        var capabilityBQualityReviewCtx = BuildAdminContext();
        var capabilityBQualityReviewResult = await AdminRuntimeEndpoints.CapabilityBQualityReviewAsync(
            capabilityBQualityReviewCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment(),
            limit: 20);
        await AssertTopLevelJsonContractAsync(capabilityBQualityReviewResult, capabilityBQualityReviewCtx, "cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "totalItems", "items");

        var capabilityBQualityReviewSummaryCtx = BuildAdminContext();
        var capabilityBQualityReviewSummaryResult = await AdminRuntimeEndpoints.CapabilityBQualityReviewSummaryAsync(
            capabilityBQualityReviewSummaryCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(capabilityBQualityReviewSummaryResult, capabilityBQualityReviewSummaryCtx, "cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "summary");

        var operationalCtx = BuildAdminContext();
        var operationalResult = await AdminRuntimeEndpoints.OperationalSummaryAsync(
            operationalCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(operationalResult, operationalCtx, "cdcAlignment", "environment", "generatedAt", "summary", "items");

        var eventsCtx = BuildAdminContext();
        var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
            eventsCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: null,
            limit: 20);
        await AssertTopLevelJsonContractAsync(eventsResult, eventsCtx, "cdcAlignment", "environment", "items");

        var warmupCtx = BuildAdminContext();
        var warmupResult = await AdminRuntimeEndpoints.WarmupResultsAsync(
            warmupCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: null,
            limit: 20);
        await AssertTopLevelJsonContractAsync(warmupResult, warmupCtx, "cdcAlignment", "environment", "items");

        var runtimeCatalogArtifactCtx = BuildAdminContext();
        var runtimeCatalogArtifactResult = await AdminRuntimeEndpoints.RuntimeCatalogArtifactAsync(
            runtimeCatalogArtifactCtx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(runtimeCatalogArtifactResult, runtimeCatalogArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "runtimes", "warmupProfiles", "capabilities");

        var modelCatalogArtifactCtx = BuildAdminContext();
        var modelCatalogArtifactResult = await AdminRuntimeEndpoints.ModelCatalogArtifactAsync(
            modelCatalogArtifactCtx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            Options.Create(CreateChatOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(modelCatalogArtifactResult, modelCatalogArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "items");

        var warmupProfilesArtifactCtx = BuildAdminContext();
        var warmupProfilesArtifactResult = await AdminRuntimeEndpoints.WarmupProfilesArtifactAsync(
            warmupProfilesArtifactCtx,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(warmupProfilesArtifactResult, warmupProfilesArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "items");

        var capabilityStateArtifactCtx = BuildAdminContext();
        var capabilityStateArtifactResult = await AdminRuntimeEndpoints.CapabilityStateArtifactAsync(
            capabilityStateArtifactCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(capabilityStateArtifactResult, capabilityStateArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "items");

        var warmupArtifactCtx = BuildAdminContext();
        var warmupArtifactResult = await AdminRuntimeEndpoints.WarmupResultsArtifactAsync(
            warmupArtifactCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: null,
            limit: 20);
        await AssertTopLevelJsonContractAsync(warmupArtifactResult, warmupArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "items");

        var eventsArtifactCtx = BuildAdminContext();
        var eventsArtifactResult = await AdminRuntimeEndpoints.EventsArtifactAsync(
            eventsArtifactCtx,
            ds,
            new StubHostEnvironment(),
            capabilityKey: null,
            limit: 20);
        await AssertTopLevelJsonContractAsync(eventsArtifactResult, eventsArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "items");

        var diagnosticsArtifactCtx = BuildAdminContext();
        var diagnosticsArtifactResult = await AdminRuntimeEndpoints.DiagnosticsArtifactAsync(
            diagnosticsArtifactCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(diagnosticsArtifactResult, diagnosticsArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "summary", "items");

        var retrievalKpisArtifactCtx = BuildAdminContext();
        var retrievalKpisArtifactResult = await AdminRuntimeEndpoints.RetrievalKpisArtifactAsync(
            retrievalKpisArtifactCtx,
            new StubHostEnvironment(),
            Options.Create(new RuntimeGovernanceOptions()));
        await AssertTopLevelJsonContractAsync(retrievalKpisArtifactResult, retrievalKpisArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");

        var capabilityBKpisArtifactCtx = BuildAdminContext();
        var capabilityBKpisArtifactResult = await AdminRuntimeEndpoints.CapabilityBKpisArtifactAsync(
            capabilityBKpisArtifactCtx,
            new StubHostEnvironment(),
            Options.Create(new RuntimeGovernanceOptions()));
        await AssertTopLevelJsonContractAsync(capabilityBKpisArtifactResult, capabilityBKpisArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "policy", "metrics", "alerts", "dashboardPanels");

        var capabilityBQualityReviewArtifactCtx = BuildAdminContext();
        var capabilityBQualityReviewArtifactResult = await AdminRuntimeEndpoints.CapabilityBQualityReviewArtifactAsync(
            capabilityBQualityReviewArtifactCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment(),
            limit: 20);
        await AssertTopLevelJsonContractAsync(capabilityBQualityReviewArtifactResult, capabilityBQualityReviewArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "totalItems", "items");

        var capabilityBQualityReviewSummaryArtifactCtx = BuildAdminContext();
        var capabilityBQualityReviewSummaryArtifactResult = await AdminRuntimeEndpoints.CapabilityBQualityReviewSummaryArtifactAsync(
            capabilityBQualityReviewSummaryArtifactCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(capabilityBQualityReviewSummaryArtifactResult, capabilityBQualityReviewSummaryArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "capabilityKey", "qualityThreshold", "summary");

        var operationalArtifactCtx = BuildAdminContext();
        var operationalArtifactResult = await AdminRuntimeEndpoints.OperationalSummaryArtifactAsync(
            operationalArtifactCtx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());
        await AssertTopLevelJsonContractAsync(operationalArtifactResult, operationalArtifactCtx, "artifact", "cdcAlignment", "environment", "generatedAt", "summary", "items");
    }

    [Fact]
    public async Task RequalifyAsync_blocks_qualification_when_hardware_gate_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var strictOptions = new RuntimeGovernanceOptions
        {
            MinCpuCores = Environment.ProcessorCount + 1000,
            MinAvailableMemoryMb = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)) + 1024
        };

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(strictOptions),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.False(state.Healthy);
        Assert.False(state.Qualified);
        Assert.False(state.Authorized);
        Assert.False(state.Selected);
        Assert.Contains("hardware gate failed", state.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(state.Details);
        var details = JsonSerializer.SerializeToElement(state.Details);
        Assert.True(details.GetProperty("hardware").TryGetProperty("reasons", out _));
    }

    [Fact]
    public async Task RequalifyAsync_blocks_qualification_when_profile_specific_hardware_gate_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var options = new RuntimeGovernanceOptions
        {
            MinCpuCores = 1,
            MinAvailableMemoryMb = 512,
            StrictRerankProfileMinCpuCores = Environment.ProcessorCount + 1000,
            StrictRerankProfileMinAvailableMemoryMb = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)) + 1024
        };

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(options),
            Options.Create(CreateRagOptions(enableRerank: true, rerankBaseUrl: "http://tei.test/", rerankModel: "cross-encoder/ms-marco")),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval", ProfileKey: "strict-rerank"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.False(state.Qualified);
        Assert.Contains("hardware gate failed", state.LastError, StringComparison.OrdinalIgnoreCase);

        var details = JsonSerializer.SerializeToElement(state.Details);
        Assert.Equal("profile", details.GetProperty("hardware").GetProperty("source").GetString());
    }

    [Fact]
    public async Task RequalifyAsync_blocks_qualification_when_runtime_specific_gate_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var options = new RuntimeGovernanceOptions
        {
            MinCpuCores = 1,
            MinAvailableMemoryMb = 512,
            QdrantRuntimeMinCpuCores = Environment.ProcessorCount + 1000
        };

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(options),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.False(state.Qualified);
        Assert.Contains("runtime gate failed", state.LastError, StringComparison.OrdinalIgnoreCase);

        var details = JsonSerializer.SerializeToElement(state.Details);
        Assert.True(details.GetProperty("runtimeGates").TryGetProperty("qdrant", out var qdrantGate));
        Assert.True(qdrantGate.TryGetProperty("reasons", out _));
    }

    [Fact]
    public async Task RequalifyAsync_blocks_qualification_when_performance_budget_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var strictOptions = new RuntimeGovernanceOptions
        {
            MaxWarmupPassDurationMs = 1,
            MaxQdrantCheckMs = 1,
            MaxEmbeddingsCheckMs = 1,
            MaxRerankCheckMs = 1
        };

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new DelayedRuntimeGovernanceHttpClientFactory(delayMs: 25),
            Options.Create(strictOptions),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval", ProfileKey: "strict-local"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.False(state.Qualified);
        Assert.Contains("performance budget failed", state.LastError, StringComparison.OrdinalIgnoreCase);

        var details = JsonSerializer.SerializeToElement(state.Details);
        Assert.True(details.TryGetProperty("checks", out var checks));
        Assert.True(checks[0].GetProperty("performanceBudgetViolations").GetArrayLength() > 0);
    }

    [Fact]
    public async Task RequalifyAsync_blocks_qualification_when_profile_requires_rerank_but_rerank_is_disabled()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval", ProfileKey: "strict-rerank"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.False(state.Qualified);
        Assert.Contains("profile policy failed", state.LastError, StringComparison.OrdinalIgnoreCase);

        var details = JsonSerializer.SerializeToElement(state.Details);
        Assert.True(details.GetProperty("profilePolicy").TryGetProperty("violations", out var violations));
        Assert.Contains("rerank_required_but_disabled", violations.EnumerateArray().Select(v => v.GetString()));
        Assert.True(details.GetProperty("profilePolicy").TryGetProperty("runtimeRequirements", out var runtimeRequirements));
        Assert.True(runtimeRequirements.GetProperty("requireRerank").GetBoolean());
    }

    [Fact]
    public async Task DiagnosticsAsync_surfaces_blockers_for_performance_budget_failure()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var strictOptions = new RuntimeGovernanceOptions
        {
            MaxWarmupPassDurationMs = 1,
            MaxQdrantCheckMs = 1,
            MaxEmbeddingsCheckMs = 1,
            MaxRerankCheckMs = 1
        };

        await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new DelayedRuntimeGovernanceHttpClientFactory(delayMs: 25),
            Options.Create(strictOptions),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval", ProfileKey: "strict-local"));

        var diagnostics = await AdminRuntimeEndpoints.DiagnosticsAsync(
            ctx,
            ds,
            Options.Create(strictOptions),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnostics, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.Contains("performance_budget_failed", retrieval.Blockers);
        Assert.Contains(retrieval.Recommendations, item => item.Contains("less strict profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticsAsync_surfaces_blockers_for_runtime_gate_failure()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var options = new RuntimeGovernanceOptions
        {
            MinCpuCores = 1,
            MinAvailableMemoryMb = 512,
            EmbeddingsRuntimeMinCpuCores = Environment.ProcessorCount + 1000
        };

        await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(options),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"));

        var diagnostics = await AdminRuntimeEndpoints.DiagnosticsAsync(
            ctx,
            ds,
            Options.Create(options),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnostics, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.Contains("runtime_gate_failed", retrieval.Blockers);
        Assert.Contains(retrieval.Recommendations, item => item.Contains("runtime-specific hardware thresholds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticsAsync_surfaces_blockers_for_stale_qualification()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var changedRag = CreateRagOptions();
        changedRag.EnableRerank = true;
        changedRag.RerankModel = "cross-encoder/ms-marco";
        changedRag.RerankBaseUrl = "http://tei-rerank.test/";

        var ctx = BuildAdminContext();
        var diagnostics = await AdminRuntimeEndpoints.DiagnosticsAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(changedRag),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnostics, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.Equal("stale", retrieval.Status);
        Assert.True(retrieval.Stale);
        Assert.True(retrieval.PersistedAuthorized);
        Assert.True(retrieval.PersistedSelected);
        Assert.Equal("qualification_inputs_changed", retrieval.StaleReason);
        Assert.Contains("stale_qualification", retrieval.Blockers);
        Assert.Contains(retrieval.Recommendations, item => item.Contains("requalify this capability", StringComparison.OrdinalIgnoreCase));
        Assert.True(payload.Summary.StaleCapabilities >= 1);
        Assert.True(payload.Summary.PersistedSelectedCapabilities >= 1);
    }

    [Fact]
    public async Task DiagnosticsAsync_surfaces_blockers_for_expired_qualification()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var options = new RuntimeGovernanceOptions
        {
            MaxQualificationAgeHours = 1
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            options,
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE runtime_capability_state SET last_qualified_at = @ts, last_checked_at = @ts WHERE capability_key='core.retrieval';",
                new { ts = DateTimeOffset.UtcNow.AddHours(-3).UtcDateTime });
        }

        var ctx = BuildAdminContext();
        var diagnostics = await AdminRuntimeEndpoints.DiagnosticsAsync(
            ctx,
            ds,
            Options.Create(options),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnostics, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.Equal("stale", retrieval.Status);
        Assert.True(retrieval.Stale);
        Assert.Equal("qualification_expired", retrieval.StaleReason);
        Assert.NotNull(retrieval.QualificationAgeHours);
        Assert.NotNull(retrieval.QualificationExpiresAt);
        Assert.Contains("stale_qualification", retrieval.Blockers);
        Assert.Contains(retrieval.Recommendations, item => item.Contains("requalify this capability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReconcileStaleAsync_persists_stale_invalidation_for_changed_runtime_inputs()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceCommandService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var changedRag = CreateRagOptions();
        changedRag.EmbeddingsModel = "intfloat/multilingual-e5-large";

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.ReconcileStaleAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(changedRag),
            new StubHostEnvironment(),
            new AdminRuntimeReconcileStaleRequestDto("core.retrieval"));

        var payload = await ExecuteResultAsync<AdminRuntimeReconcileStaleResponseDto>(result, ctx);
        Assert.Equal(1, payload.UpdatedCount);

        var updated = Assert.Single(payload.Items);
        Assert.True(updated.Stale);
        Assert.False(updated.Authorized);
        Assert.False(updated.Selected);
        Assert.Equal("qualification stale: requalify required", updated.LastError);

        var details = JsonSerializer.SerializeToElement(updated.Details);
        Assert.Equal("admin_runtime_endpoint", details.GetProperty("staleReconciledBy").GetString());

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var persisted = await conn.QuerySingleAsync<(bool authorized, bool selected, string? last_error)>(
            "SELECT authorized, selected, last_error FROM runtime_capability_state WHERE capability_key='core.retrieval';");
        Assert.False(persisted.authorized);
        Assert.False(persisted.selected);
        Assert.Equal("qualification stale: requalify required", persisted.last_error);
    }

    [Fact]
    public async Task DiagnosticsAsync_surfaces_blockers_for_profile_policy_failure()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval", ProfileKey: "strict-rerank"));

        var diagnostics = await AdminRuntimeEndpoints.DiagnosticsAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnostics, ctx);
        var retrieval = Assert.Single(payload.Items, item => item.Key == "core.retrieval");
        Assert.Contains("profile_policy_failed", retrieval.Blockers);
        Assert.Contains(retrieval.Recommendations, item => item.Contains("enable the required runtime features", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticsAsync_surfaces_live_runtime_blocker_for_capability_b_when_probe_fails()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var ctx = BuildAdminContext(httpClientFactory: new BrokenBackofficeRuntimeGovernanceHttpClientFactory());
            var diagnostics = await AdminRuntimeEndpoints.DiagnosticsAsync(
                ctx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());

            var payload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnostics, ctx);
            var capabilityB = Assert.Single(payload.Items, item => item.Key == "capability_b.backoffice_generation");

            Assert.Equal("selected", capabilityB.Status);
            Assert.Contains("runtime_live_unavailable", capabilityB.Blockers);
            Assert.Contains(capabilityB.Recommendations, item => item.Contains("fall back to client_admin", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("llm chat completion check failed", capabilityB.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    private static RagOptions CreateRagOptions(
        bool enableRerank = false,
        string? rerankBaseUrl = null,
        string? rerankModel = null,
        string? collection = null)
        => new()
        {
            QdrantBaseUrl = "http://qdrant.test/",
            QdrantCollection = collection ?? "knowledge_base",
            EmbeddingsBaseUrl = "http://tei.test/",
            EmbeddingsModel = "intfloat/multilingual-e5-base",
            EnableRerank = enableRerank,
            RerankBaseUrl = rerankBaseUrl,
            RerankModel = rerankModel
        };

    private static ChatOptions CreateChatOptions()
        => new()
        {
            LlmBaseUrl = "http://llm.test/",
            LlmModel = "local"
        };

    private static DefaultHttpContext BuildAdminContext(Guid? tenantId = null, IHttpClientFactory? httpClientFactory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });
        services.AddSingleton<IHttpClientFactory>(httpClientFactory ?? new RuntimeGovernanceHttpClientFactory());

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId ?? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return ctx;
    }

    private static async Task<T> ExecuteResultAsync<T>(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<JsonDocument> ExecuteAnonymousAsync(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body);
    }

    private static void AssertDtoJsonPropertyNames<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetProperties()
            .Select(static property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var expectedOrdered = expected
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedOrdered, actual);
    }

    private static async Task AssertTopLevelJsonContractAsync(IResult result, DefaultHttpContext ctx, params string[] expected)
    {
        using var json = await ExecuteAnonymousAsync(result, ctx);
        var actual = json.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var expectedOrdered = expected
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedOrdered, actual);
    }

    private static CapabilityAHypotheticalQuestionService CreateCapabilityAQuestionService()
        => new(new LocalLlmChatClient(new RuntimeGovernanceHttpClientFactory(), new ChatOptions()));

    private sealed class RuntimeGovernanceHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RuntimeGovernanceHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class DelayedRuntimeGovernanceHttpClientFactory(int delayMs) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RuntimeGovernanceHttpMessageHandler(delayMs))
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class BrokenBackofficeRuntimeGovernanceHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new BrokenBackofficeRuntimeGovernanceHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class RuntimeGovernanceHttpMessageHandler(int delayMs = 0) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/collections/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{ "result": { "status": "green", "points_count": 0 } }""");
            }

            if (path.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {
                  "data": [
                    { "embedding": [0.1, 0.2, 0.3, 0.4] }
                  ]
                }
                """);
            }

            if (path.EndsWith("/rerank", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {
                  "results": [
                    { "index": 0, "score": 0.9 },
                    { "index": 1, "score": 0.8 }
                  ]
                }
                """);
            }

            if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"questions\":[\"How should operators apply the Control Loop Overview in IND570?\",\"When should the IND570 PLC integration guidance be retrieved?\"]}"
                      }
                    }
                  ]
                }
                """);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class BrokenBackofficeRuntimeGovernanceHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Get && path.StartsWith("/collections/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "result": { "status": "green", "points_count": 0 } }""", Encoding.UTF8, "application/json")
                });
            }

            if (path.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        { "embedding": [0.1, 0.2, 0.3, 0.4] }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                });
            }

            if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{ "error": "llm_runtime_unavailable" }""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SAAIA.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
            var databaseName = $"saaia_rt_{Guid.NewGuid():N}";

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
