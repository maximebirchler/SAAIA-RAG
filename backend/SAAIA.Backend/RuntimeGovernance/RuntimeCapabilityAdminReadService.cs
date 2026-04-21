using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityAdminReadService
{
    private const string CdcAlignment = "v3.0";
    private const string CapabilityACandidatesJsonArtifact = "capability_a_candidates.json";
    private const string CapabilityACampaignsJsonArtifact = "capability_a_campaigns.json";
    private const string CapabilityACampaignDetailJsonArtifact = "capability_a_campaign_detail.json";
    private const string CapabilityBCandidatesJsonArtifact = "capability_b_candidates.json";
    private const string CapabilityBCampaignsJsonArtifact = "capability_b_campaigns.json";
    private const string CapabilityBCampaignDetailJsonArtifact = "capability_b_campaign_detail.json";
    private const string CapabilityBQualityReviewJsonArtifact = "capability_b_quality_review.json";
    private const string CapabilityBQualityReviewSummaryJsonArtifact = "capability_b_quality_review_summary.json";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>> GetCapabilityAEnrichmentCandidatesAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IngestionOptions ingest,
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityAOperationActivity("capability_a_candidates");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await RuntimeCapabilityGateService.EnsureCapabilityReadyAsync(conn, CapabilityACorpusEnrichmentKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_candidates",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    candidateCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>(null, gate.Error);
            }

            var candidates = await RuntimeCapabilityAEnrichmentStore.LoadCandidatesAsync(
                conn,
                tenantId,
                ingest,
                hypotheticalQuestionService,
                category,
                limit,
                reasonFilters: null,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_candidates",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length);

            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>(
                new AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityACorpusEnrichmentKey,
                    gate.State!.ProfileKey,
                    candidates.Length,
                    candidates),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_candidates",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: 0,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>> GetCapabilityAEnrichmentCandidatesArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IngestionOptions ingest,
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityACandidatesJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await GetCapabilityAEnrichmentCandidatesAsync(
                tenantId,
                ds,
                options,
                rag,
                ingest,
                hypotheticalQuestionService,
                env,
                category,
                limit,
                ct);

            if (result.Error is not null || result.Payload is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>(null, result.Error ?? "capability_a_candidates_unavailable");
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACandidatesJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>(
                new AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto(
                    CapabilityACandidatesJsonArtifact,
                    result.Payload.CdcAlignment,
                    result.Payload.Environment,
                    DateTimeOffset.UtcNow,
                    result.Payload.CapabilityKey,
                    result.Payload.ProfileKey,
                    result.Payload.TotalCandidates,
                    result.Payload.Items),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignsResponseDto> GetCapabilityACampaignsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_a_campaigns");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityCampaignStore.LoadCapabilityACampaignsAsync(
                conn,
                CapabilityACorpusEnrichmentKey,
                Math.Clamp(limit, 1, 100),
                ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaigns", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignsResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityACorpusEnrichmentKey,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaigns", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignsArtifactDto> GetCapabilityACampaignsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityACampaignsJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaigns = await GetCapabilityACampaignsAsync(ds, env, limit, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignsJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignsArtifactDto(
                CapabilityACampaignsJsonArtifact,
                campaigns.CdcAlignment,
                campaigns.Environment,
                DateTimeOffset.UtcNow,
                campaigns.CapabilityKey,
                campaigns.Items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignsJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignDetailResponseDto?> GetCapabilityACampaignAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_a_campaign_detail");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var item = await RuntimeCapabilityCampaignStore.LoadCapabilityACampaignDetailAsync(
                conn,
                CapabilityACorpusEnrichmentKey,
                campaignId,
                ct);
            if (item is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignDetailResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityACorpusEnrichmentKey,
                item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignDetailArtifactDto?> GetCapabilityACampaignArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityACampaignDetailJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaign = await GetCapabilityACampaignAsync(ds, env, campaignId, ct);
            if (campaign is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignDetailJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignDetailArtifactDto(
                CapabilityACampaignDetailJsonArtifact,
                campaign.CdcAlignment,
                campaign.Environment,
                DateTimeOffset.UtcNow,
                campaign.CapabilityKey,
                campaign.Item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>> GetCapabilityBBackofficeCandidatesAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_candidates");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await RuntimeCapabilityGateService.EnsureCapabilityReadyAsync(conn, CapabilityBBackofficeGenerationKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_candidates",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    candidateCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>(null, gate.Error);
            }

            var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
                conn,
                tenantId,
                category,
                limit,
                options,
                includeFresh: false,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_candidates",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length);

            return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>(
                new AdminRuntimeCapabilityBBackofficeCandidatesResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityBBackofficeGenerationKey,
                    gate.State!.ProfileKey,
                    candidates.Length,
                    candidates),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_candidates",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: 0,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>> GetCapabilityBBackofficeCandidatesArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBCandidatesJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await GetCapabilityBBackofficeCandidatesAsync(
                tenantId,
                ds,
                options,
                rag,
                env,
                category,
                limit,
                ct);

            if (result.Error is not null || result.Payload is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>(null, result.Error ?? "capability_b_candidates_unavailable");
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCandidatesJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>(
                new AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto(
                    CapabilityBCandidatesJsonArtifact,
                    result.Payload.CdcAlignment,
                    result.Payload.Environment,
                    DateTimeOffset.UtcNow,
                    result.Payload.CapabilityKey,
                    result.Payload.ProfileKey,
                    result.Payload.TotalCandidates,
                    result.Payload.Items),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignsResponseDto> GetCapabilityBCampaignsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_b_campaigns");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityCampaignStore.LoadCapabilityBCampaignsAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                Math.Clamp(limit, 1, 100),
                ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaigns", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignsResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaigns", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignsArtifactDto> GetCapabilityBCampaignsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBCampaignsJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaigns = await GetCapabilityBCampaignsAsync(ds, env, limit, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignsJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignsArtifactDto(
                CapabilityBCampaignsJsonArtifact,
                campaigns.CdcAlignment,
                campaigns.Environment,
                DateTimeOffset.UtcNow,
                campaigns.CapabilityKey,
                campaigns.Items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignsJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignDetailResponseDto?> GetCapabilityBCampaignAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_b_campaign_detail");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var item = await RuntimeCapabilityCampaignStore.LoadCapabilityBCampaignDetailAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                campaignId,
                ct);
            if (item is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaign_detail", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignDetailResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignDetailArtifactDto?> GetCapabilityBCampaignArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBCampaignDetailJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaign = await GetCapabilityBCampaignAsync(ds, env, campaignId, ct);
            if (campaign is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignDetailJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignDetailArtifactDto(
                CapabilityBCampaignDetailJsonArtifact,
                campaign.CdcAlignment,
                campaign.Environment,
                DateTimeOffset.UtcNow,
                campaign.CapabilityKey,
                campaign.Item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBQualityReviewResponseDto> GetCapabilityBQualityReviewAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_b_quality_review");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var threshold = Math.Clamp(options.CapabilityBQualityScoreTarget, 0d, 1d);
            var items = await RuntimeCapabilityBQualityReviewStore.LoadLowQualitySummariesAsync(
                conn,
                tenantId,
                threshold,
                limit,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_quality_review", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBQualityReviewResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                CapabilityBBackofficeGenerationKey,
                threshold,
                items.Length,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_quality_review", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBQualityReviewArtifactDto> GetCapabilityBQualityReviewArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBQualityReviewJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var review = await GetCapabilityBQualityReviewAsync(tenantId, ds, options, env, limit, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBQualityReviewJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBQualityReviewArtifactDto(
                CapabilityBQualityReviewJsonArtifact,
                review.CdcAlignment,
                review.Environment,
                review.GeneratedAt,
                review.CapabilityKey,
                review.QualityThreshold,
                review.TotalItems,
                review.Items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBQualityReviewJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBQualityReviewSummaryResponseDto> GetCapabilityBQualityReviewSummaryAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        IHostEnvironment env,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_b_quality_review_summary");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var threshold = Math.Clamp(options.CapabilityBQualityScoreTarget, 0d, 1d);
            var summary = await RuntimeCapabilityBQualityReviewStore.LoadLowQualitySummarySnapshotAsync(
                conn,
                tenantId,
                threshold,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_quality_review_summary", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBQualityReviewSummaryResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                CapabilityBBackofficeGenerationKey,
                threshold,
                summary);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_quality_review_summary", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBQualityReviewSummaryArtifactDto> GetCapabilityBQualityReviewSummaryArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        IHostEnvironment env,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBQualityReviewSummaryJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var review = await GetCapabilityBQualityReviewSummaryAsync(tenantId, ds, options, env, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBQualityReviewSummaryJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBQualityReviewSummaryArtifactDto(
                CapabilityBQualityReviewSummaryJsonArtifact,
                review.CdcAlignment,
                review.Environment,
                review.GeneratedAt,
                review.CapabilityKey,
                review.QualityThreshold,
                review.Summary);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBQualityReviewSummaryJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }
}
