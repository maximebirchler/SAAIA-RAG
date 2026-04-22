using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityCampaignStore
{
    private static readonly IReadOnlyDictionary<string, int> EmptyReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    internal static async Task<AdminRuntimeCapabilityACampaignDto[]> LoadCapabilityACampaignsAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityACampaignRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  COALESCE((details ->> 'dryRun')::boolean, false) AS "DryRun",
  COALESCE((details ->> 'allowUnsafeCandidates')::boolean, false) AS "AllowUnsafeCandidates",
  COALESCE((details ->> 'candidateCount')::integer, 0) AS "CandidateCount",
  COALESCE((details ->> 'plannedCount')::integer, 0) AS "PlannedCount",
  COALESCE((details ->> 'queuedCount')::integer, 0) AS "QueuedCount",
  COALESCE((details ->> 'skippedCount')::integer, 0) AS "SkippedCount",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_a_campaign_dry_run', 'capability_a_campaign_executed')
  AND details ? 'campaignId'
ORDER BY occurred_at DESC
LIMIT @limit;
""",
            new { capabilityKey, limit },
            cancellationToken: ct)))
            .Select(MapCapabilityACampaignRow)
            .ToArray();

    internal static async Task<AdminRuntimeCapabilityACampaignDetailDto?> LoadCapabilityACampaignDetailAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        Guid campaignId,
        CancellationToken ct)
    {
        var row = await conn.QueryFirstOrDefaultAsync<CapabilityACampaignRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  COALESCE((details ->> 'dryRun')::boolean, false) AS "DryRun",
  COALESCE((details ->> 'allowUnsafeCandidates')::boolean, false) AS "AllowUnsafeCandidates",
  COALESCE((details ->> 'candidateCount')::integer, 0) AS "CandidateCount",
  COALESCE((details ->> 'plannedCount')::integer, 0) AS "PlannedCount",
  COALESCE((details ->> 'queuedCount')::integer, 0) AS "QueuedCount",
  COALESCE((details ->> 'skippedCount')::integer, 0) AS "SkippedCount",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_a_campaign_dry_run', 'capability_a_campaign_executed')
  AND details ->> 'campaignId' = @campaignId
LIMIT 1;
""",
            new
            {
                capabilityKey,
                campaignId = campaignId.ToString()
            },
            cancellationToken: ct));

        if (row is null)
            return null;

        var items = await LoadCapabilityACampaignItemsAsync(conn, capabilityKey, row.CampaignId, row.DetailsJson, ct);
        var summary = MapCapabilityACampaignRow(row);
        return new AdminRuntimeCapabilityACampaignDetailDto(
            CampaignId: summary.CampaignId,
            CapabilityKey: summary.CapabilityKey,
            ProfileKey: summary.ProfileKey,
            Status: summary.Status,
            DryRun: summary.DryRun,
            AllowUnsafeCandidates: summary.AllowUnsafeCandidates,
            CandidateCount: summary.CandidateCount,
            PlannedCount: summary.PlannedCount,
            QueuedCount: summary.QueuedCount,
            SkippedCount: summary.SkippedCount,
            ReasonCounts: summary.ReasonCounts,
            OccurredAt: summary.OccurredAt,
            Items: items);
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignDto[]> LoadCapabilityBCampaignsAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        int limit,
        CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<CapabilityBCampaignRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  COALESCE((details ->> 'dryRun')::boolean, false) AS "DryRun",
  COALESCE((details ->> 'force')::boolean, false) AS "Force",
  COALESCE((details ->> 'candidateCount')::integer, 0) AS "CandidateCount",
  COALESCE((details ->> 'plannedCount')::integer, 0) AS "PlannedCount",
  COALESCE((details ->> 'queuedCount')::integer, 0) AS "QueuedCount",
  COALESCE((details ->> 'skippedCount')::integer, 0) AS "SkippedCount",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_b_campaign_dry_run', 'capability_b_campaign_executed')
  AND details ? 'campaignId'
ORDER BY occurred_at DESC
LIMIT @limit;
""",
            new { capabilityKey, limit },
            cancellationToken: ct))).ToArray();

        var statusCounts = await LoadCapabilityBCampaignJobStatusCountsAsync(conn, rows.Select(static row => row.CampaignId).ToArray(), ct);
        return rows.Select(row => MapCapabilityBCampaignRow(
            row,
            statusCounts.TryGetValue(row.CampaignId, out var counts) ? counts : EmptyReasonCounts))
            .ToArray();
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignDetailDto?> LoadCapabilityBCampaignDetailAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        Guid campaignId,
        CancellationToken ct)
    {
        var row = await conn.QueryFirstOrDefaultAsync<CapabilityBCampaignRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  COALESCE((details ->> 'dryRun')::boolean, false) AS "DryRun",
  COALESCE((details ->> 'force')::boolean, false) AS "Force",
  COALESCE((details ->> 'candidateCount')::integer, 0) AS "CandidateCount",
  COALESCE((details ->> 'plannedCount')::integer, 0) AS "PlannedCount",
  COALESCE((details ->> 'queuedCount')::integer, 0) AS "QueuedCount",
  COALESCE((details ->> 'skippedCount')::integer, 0) AS "SkippedCount",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_b_campaign_dry_run', 'capability_b_campaign_executed')
  AND details ->> 'campaignId' = @campaignId
LIMIT 1;
""",
            new
            {
                capabilityKey,
                campaignId = campaignId.ToString()
            },
            cancellationToken: ct));

        if (row is null)
            return null;

        var items = await LoadCapabilityBCampaignItemsAsync(conn, capabilityKey, row.CampaignId, row.DetailsJson, ct);
        items = await EnrichCapabilityBCampaignItemsAsync(conn, items, ct);
        var summary = MapCapabilityBCampaignRow(row, BuildCapabilityBJobStatusCounts(items), items);
        return new AdminRuntimeCapabilityBCampaignDetailDto(
            summary.CampaignId,
            summary.CapabilityKey,
            summary.ProfileKey,
            summary.Status,
            summary.DryRun,
            summary.Force,
            summary.CandidateCount,
            summary.PlannedCount,
            summary.QueuedCount,
            summary.SkippedCount,
            summary.ReasonCounts,
            summary.JobStatusCounts,
            summary.TrackedJobCount,
            summary.ActiveJobCount,
            summary.TerminalJobCount,
            summary.StoredSummaryCount,
            summary.ProgressPercent,
            summary.OccurredAt,
            items);
    }

    private static IReadOnlyDictionary<string, int> ParseCapabilityAReasonCounts(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (!doc.RootElement.TryGetProperty("reasonCounts", out var reasonCountsElement)
                || reasonCountsElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, int>(StringComparer.Ordinal);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in reasonCountsElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var value))
                    counts[prop.Name] = value;
            }

            return counts;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private static AdminRuntimeCapabilityAEnqueueItemDto[] ParseCapabilityACampaignItems(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return [];

        using var doc = JsonDocument.Parse(detailsJson);
        if (!doc.RootElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<AdminRuntimeCapabilityAEnqueueItemDto>();
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.Object)
                continue;

            Guid? docId = null;
            if (itemElement.TryGetProperty("docId", out var docIdElement)
                && docIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(docIdElement.GetString(), out var parsedDocId))
            {
                docId = parsedDocId;
            }

            Guid? jobId = null;
            if (itemElement.TryGetProperty("jobId", out var jobIdElement)
                && jobIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(jobIdElement.GetString(), out var parsedJobId))
            {
                jobId = parsedJobId;
            }

            var docPath = itemElement.TryGetProperty("docPath", out var docPathElement) && docPathElement.ValueKind == JsonValueKind.String
                ? docPathElement.GetString()
                : null;
            var reason = itemElement.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            var queued = itemElement.TryGetProperty("queued", out var queuedElement) && queuedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? queuedElement.GetBoolean()
                : false;
            var previewText = itemElement.TryGetProperty("previewText", out var previewTextElement) && previewTextElement.ValueKind == JsonValueKind.String
                ? previewTextElement.GetString()
                : null;
            var keySectionTitles = TryReadStringArray(itemElement, "keySectionTitles");
            var suggestedTags = TryReadStringArray(itemElement, "suggestedTags");
            var hypotheticalQuestions = TryReadStringArray(itemElement, "hypotheticalQuestions");
            var qualityScore = TryReadDouble(itemElement, "qualityScore");
            var qualitySignals = TryReadCapabilityAQualitySignals(itemElement, "qualitySignals");

            items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(
                DocId: docId,
                DocPath: docPath,
                Queued: queued,
                JobId: jobId,
                Reason: reason,
                PreviewText: previewText,
                KeySectionTitles: keySectionTitles,
                SuggestedTags: suggestedTags,
                HypotheticalQuestions: hypotheticalQuestions,
                QualityScore: qualityScore,
                QualitySignals: qualitySignals));
        }

        return items.ToArray();
    }

    private static AdminRuntimeCapabilityBEnqueueItemDto[] ParseCapabilityBCampaignItems(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return [];

        using var doc = JsonDocument.Parse(detailsJson);
        if (!doc.RootElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<AdminRuntimeCapabilityBEnqueueItemDto>();
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.Object)
                continue;

            Guid? docId = null;
            if (itemElement.TryGetProperty("docId", out var docIdElement)
                && docIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(docIdElement.GetString(), out var parsedDocId))
            {
                docId = parsedDocId;
            }

            Guid? jobId = null;
            if (itemElement.TryGetProperty("jobId", out var jobIdElement)
                && jobIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(jobIdElement.GetString(), out var parsedJobId))
            {
                jobId = parsedJobId;
            }

            var docPath = itemElement.TryGetProperty("docPath", out var docPathElement) && docPathElement.ValueKind == JsonValueKind.String
                ? docPathElement.GetString()
                : null;
            var reason = itemElement.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            var queued = itemElement.TryGetProperty("queued", out var queuedElement) && queuedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? queuedElement.GetBoolean()
                : false;

            items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(
                DocId: docId,
                DocPath: docPath,
                Queued: queued,
                JobId: jobId,
                Reason: reason));
        }

        return items.ToArray();
    }

    private static string[]? TryReadStringArray(JsonElement itemElement, string propertyName)
    {
        if (!itemElement.TryGetProperty(propertyName, out var propertyElement) || propertyElement.ValueKind != JsonValueKind.Array)
            return null;

        return propertyElement
            .EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static double? TryReadDouble(JsonElement itemElement, string propertyName)
        => itemElement.TryGetProperty(propertyName, out var propertyElement)
           && propertyElement.ValueKind == JsonValueKind.Number
           && propertyElement.TryGetDouble(out var value)
            ? value
            : null;

    private static AdminRuntimeCapabilityAQualitySignalsDto? TryReadCapabilityAQualitySignals(JsonElement itemElement, string propertyName)
    {
        if (!itemElement.TryGetProperty(propertyName, out var signals) || signals.ValueKind != JsonValueKind.Object)
            return null;

        return new AdminRuntimeCapabilityAQualitySignalsDto(
            SectionTitleCount: TryReadInt(signals, "sectionTitleCount") ?? 0,
            ExcerptCount: TryReadInt(signals, "excerptCount") ?? 0,
            SuggestedTagCount: TryReadInt(signals, "suggestedTagCount") ?? 0,
            HypotheticalQuestionCount: TryReadInt(signals, "hypotheticalQuestionCount") ?? 0,
            SectionCoverageScore: TryReadDouble(signals, "sectionCoverageScore") ?? 0d,
            TagScore: TryReadDouble(signals, "tagScore") ?? 0d,
            QuestionScore: TryReadDouble(signals, "questionScore") ?? 0d,
            PreviewScore: TryReadDouble(signals, "previewScore") ?? 0d);
    }

    private static int? TryReadInt(JsonElement itemElement, string propertyName)
        => itemElement.TryGetProperty(propertyName, out var propertyElement)
           && propertyElement.ValueKind == JsonValueKind.Number
           && propertyElement.TryGetInt32(out var value)
            ? value
            : null;

    private static string[]? ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind != JsonValueKind.Array
            ? null
            : doc.RootElement
                .EnumerateArray()
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(static value => value.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
    }

    private static AdminRuntimeCapabilityACampaignDto MapCapabilityACampaignRow(CapabilityACampaignRow row)
        => new(
            CampaignId: row.CampaignId,
            CapabilityKey: row.CapabilityKey,
            ProfileKey: row.ProfileKey,
            Status: string.Equals(row.EventType, "capability_a_campaign_dry_run", StringComparison.Ordinal)
                ? "dry_run"
                : "executed",
            DryRun: row.DryRun,
            AllowUnsafeCandidates: row.AllowUnsafeCandidates,
            CandidateCount: row.CandidateCount,
            PlannedCount: row.PlannedCount,
            QueuedCount: row.QueuedCount,
            SkippedCount: row.SkippedCount,
            ReasonCounts: ParseCapabilityAReasonCounts(row.DetailsJson),
            OccurredAt: row.OccurredAt);

    private static async Task<AdminRuntimeCapabilityAEnqueueItemDto[]> LoadCapabilityACampaignItemsAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        Guid campaignId,
        string? detailsJson,
        CancellationToken ct)
    {
        var items = ParseCapabilityACampaignItems(detailsJson);
        if (items.Length > 0)
            return items;

        return (await conn.QueryAsync<CapabilityACampaignItemRow>(new CommandDefinition(
            """
SELECT
  CASE
    WHEN details ? 'docId' AND NULLIF(details ->> 'docId', '') IS NOT NULL
      THEN CAST(details ->> 'docId' AS uuid)
    ELSE NULL
  END AS "DocId",
  details ->> 'docPath' AS "DocPath",
  CASE
    WHEN details ? 'jobId' AND NULLIF(details ->> 'jobId', '') IS NOT NULL
      THEN CAST(details ->> 'jobId' AS uuid)
    ELSE NULL
  END AS "JobId",
  details ->> 'previewText' AS "PreviewText",
  CASE
    WHEN details ? 'keySectionTitles' THEN (details -> 'keySectionTitles')::text
    ELSE NULL
  END AS "KeySectionTitlesJson",
  CASE
    WHEN details ? 'suggestedTags' THEN (details -> 'suggestedTags')::text
    ELSE NULL
  END AS "SuggestedTagsJson",
  CASE
    WHEN details ? 'hypotheticalQuestions' THEN (details -> 'hypotheticalQuestions')::text
    ELSE NULL
  END AS "HypotheticalQuestionsJson",
  CASE
    WHEN details ? 'qualityScore' AND jsonb_typeof(details -> 'qualityScore') = 'number'
      THEN (details ->> 'qualityScore')::double precision
    ELSE NULL
  END AS "QualityScore",
  CASE
    WHEN details ? 'qualitySignals' THEN (details -> 'qualitySignals')::text
    ELSE NULL
  END AS "QualitySignalsJson",
  occurred_at AS "OccurredAt"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type = 'capability_a_enqueued'
  AND details ->> 'campaignId' = @campaignId
ORDER BY occurred_at ASC;
""",
            new
            {
                capabilityKey,
                campaignId = campaignId.ToString()
            },
            cancellationToken: ct)))
            .Select(static row => new AdminRuntimeCapabilityAEnqueueItemDto(
                DocId: row.DocId,
                DocPath: row.DocPath,
                Queued: true,
                JobId: row.JobId,
                PreviewText: row.PreviewText,
                KeySectionTitles: ParseStringArray(row.KeySectionTitlesJson),
                SuggestedTags: ParseStringArray(row.SuggestedTagsJson),
                HypotheticalQuestions: ParseStringArray(row.HypotheticalQuestionsJson),
                QualityScore: row.QualityScore,
                QualitySignals: ParseCapabilityAQualitySignals(row.QualitySignalsJson)))
            .ToArray();
    }

    private static AdminRuntimeCapabilityAQualitySignalsDto? ParseCapabilityAQualitySignals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return TryReadCapabilityAQualitySignals(doc.RootElement, propertyName: string.Empty)
            ?? (doc.RootElement.ValueKind == JsonValueKind.Object
                ? new AdminRuntimeCapabilityAQualitySignalsDto(
                    SectionTitleCount: TryReadInt(doc.RootElement, "sectionTitleCount") ?? 0,
                    ExcerptCount: TryReadInt(doc.RootElement, "excerptCount") ?? 0,
                    SuggestedTagCount: TryReadInt(doc.RootElement, "suggestedTagCount") ?? 0,
                    HypotheticalQuestionCount: TryReadInt(doc.RootElement, "hypotheticalQuestionCount") ?? 0,
                    SectionCoverageScore: TryReadDouble(doc.RootElement, "sectionCoverageScore") ?? 0d,
                    TagScore: TryReadDouble(doc.RootElement, "tagScore") ?? 0d,
                    QuestionScore: TryReadDouble(doc.RootElement, "questionScore") ?? 0d,
                    PreviewScore: TryReadDouble(doc.RootElement, "previewScore") ?? 0d)
                : null);
    }

    private static AdminRuntimeCapabilityBCampaignDto MapCapabilityBCampaignRow(
        CapabilityBCampaignRow row,
        IReadOnlyDictionary<string, int> jobStatusCounts,
        IReadOnlyCollection<AdminRuntimeCapabilityBEnqueueItemDto>? items = null)
    {
        var progress = BuildCapabilityBCampaignProgress(jobStatusCounts, items, row.QueuedCount);
        return new(
            row.CampaignId,
            row.CapabilityKey,
            row.ProfileKey,
            string.Equals(row.EventType, "capability_b_campaign_dry_run", StringComparison.Ordinal) ? "dry_run" : "executed",
            row.DryRun,
            row.Force,
            row.CandidateCount,
            row.PlannedCount,
            row.QueuedCount,
            row.SkippedCount,
            ParseCapabilityAReasonCounts(row.DetailsJson),
            jobStatusCounts,
            progress.TrackedJobCount,
            progress.ActiveJobCount,
            progress.TerminalJobCount,
            progress.StoredSummaryCount,
            progress.ProgressPercent,
            row.OccurredAt);
    }

    private static async Task<AdminRuntimeCapabilityBEnqueueItemDto[]> LoadCapabilityBCampaignItemsAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        Guid campaignId,
        string? detailsJson,
        CancellationToken ct)
    {
        var items = ParseCapabilityBCampaignItems(detailsJson);
        if (items.Length > 0)
            return items;

        return (await conn.QueryAsync<CapabilityBCampaignItemRow>(new CommandDefinition(
            """
SELECT
  CASE
    WHEN details ? 'docId' AND NULLIF(details ->> 'docId', '') IS NOT NULL
      THEN CAST(details ->> 'docId' AS uuid)
    ELSE NULL
  END AS "DocId",
  details ->> 'docPath' AS "DocPath",
  CASE
    WHEN details ? 'jobId' AND NULLIF(details ->> 'jobId', '') IS NOT NULL
      THEN CAST(details ->> 'jobId' AS uuid)
    ELSE NULL
  END AS "JobId",
  occurred_at AS "OccurredAt"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type = 'capability_b_enqueued'
  AND details ->> 'campaignId' = @campaignId
ORDER BY occurred_at ASC;
""",
            new { capabilityKey, campaignId = campaignId.ToString() },
            cancellationToken: ct)))
            .Select(static row => new AdminRuntimeCapabilityBEnqueueItemDto(
                row.DocId,
                row.DocPath,
                true,
                row.JobId))
            .ToArray();
    }

    private static async Task<AdminRuntimeCapabilityBEnqueueItemDto[]> EnrichCapabilityBCampaignItemsAsync(
        NpgsqlConnection conn,
        AdminRuntimeCapabilityBEnqueueItemDto[] items,
        CancellationToken ct)
    {
        var jobIds = items
            .Where(static item => item.Queued && item.JobId.HasValue)
            .Select(static item => item.JobId!.Value)
            .Distinct()
            .ToArray();
        if (jobIds.Length == 0)
            return items;

        var rows = await conn.QueryAsync<CapabilityBJobStateRow>(new CommandDefinition(
            """
SELECT
  a.job_id AS "JobId",
  a.status AS "JobStatus",
  CASE
    WHEN jsonb_typeof(a.result->'stored')='boolean' THEN (a.result->>'stored')::boolean
    ELSE NULL::boolean
  END AS "ResultStored",
  a.finished_at AS "FinishedAt",
  CASE
    WHEN a.doc_id IS NULL OR d.doc_id IS NULL THEN NULL
    WHEN s.doc_id IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "StoredSummaryFreshness"
FROM admin_jobs a
LEFT JOIN documents d
  ON d.tenant_id = a.tenant_id
 AND d.doc_id = a.doc_id
LEFT JOIN document_summaries s
  ON s.tenant_id = a.tenant_id
 AND s.doc_id = a.doc_id
 AND s.level = a.level
WHERE a.job_id = ANY(@jobIds);
""",
            new { jobIds },
            cancellationToken: ct));

        var lookup = rows.ToDictionary(static row => row.JobId);
        return items.Select(item =>
        {
            if (!item.JobId.HasValue || !lookup.TryGetValue(item.JobId.Value, out var row))
                return item;

            return item with
            {
                JobStatus = row.JobStatus,
                JobResultStored = row.ResultStored,
                JobFinishedAt = row.FinishedAt,
                StoredSummaryFreshness = row.StoredSummaryFreshness
            };
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, int> BuildCapabilityBJobStatusCounts(IReadOnlyCollection<AdminRuntimeCapabilityBEnqueueItemDto> items)
    {
        if (items.Count == 0)
            return EmptyReasonCounts;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!item.Queued)
                continue;

            var key = string.IsNullOrWhiteSpace(item.JobStatus) ? "unknown" : item.JobStatus!;
            counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        return counts;
    }

    private static CapabilityBCampaignProgress BuildCapabilityBCampaignProgress(
        IReadOnlyDictionary<string, int> jobStatusCounts,
        IReadOnlyCollection<AdminRuntimeCapabilityBEnqueueItemDto>? items,
        int expectedQueuedCount)
    {
        var trackedJobCount = jobStatusCounts.Values.Sum();
        var activeJobCount = SumJobStatuses(jobStatusCounts, "queued", "running", "paused");
        var terminalJobCount = SumJobStatuses(jobStatusCounts, "done", "failed", "canceled", "cancelled");
        var storedSummaryCount = items?.Count(static item => item.JobResultStored == true) ?? 0;

        int? progressPercent = null;
        var denominator = Math.Max(expectedQueuedCount, trackedJobCount);
        if (denominator > 0)
        {
            progressPercent = Math.Clamp((int)Math.Round((terminalJobCount * 100.0) / denominator, MidpointRounding.AwayFromZero), 0, 100);
        }

        return new CapabilityBCampaignProgress(
            trackedJobCount,
            activeJobCount,
            terminalJobCount,
            storedSummaryCount,
            progressPercent);
    }

    private static int SumJobStatuses(IReadOnlyDictionary<string, int> jobStatusCounts, params string[] statuses)
    {
        var sum = 0;
        foreach (var status in statuses)
        {
            if (jobStatusCounts.TryGetValue(status, out var count))
                sum += count;
        }

        return sum;
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>> LoadCapabilityBCampaignJobStatusCountsAsync(
        NpgsqlConnection conn,
        IReadOnlyCollection<Guid> campaignIds,
        CancellationToken ct)
    {
        if (campaignIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyDictionary<string, int>>();

        var keys = campaignIds.Select(static id => id.ToString()).ToArray();
        var rows = await conn.QueryAsync<CapabilityBCampaignJobStatusCountRow>(new CommandDefinition(
            """
SELECT
  CAST(a.payload ->> 'campaignId' AS uuid) AS "CampaignId",
  a.status AS "JobStatus",
  COUNT(*)::int AS "Count"
FROM admin_jobs a
WHERE a.payload ->> 'source' = 'capability_b'
  AND a.payload ? 'campaignId'
  AND a.payload ->> 'campaignId' = ANY(@campaignIds)
GROUP BY CAST(a.payload ->> 'campaignId' AS uuid), a.status;
""",
            new { campaignIds = keys },
            cancellationToken: ct));

        return rows
            .GroupBy(static row => row.CampaignId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyDictionary<string, int>)group.ToDictionary(
                    static row => row.JobStatus,
                    static row => row.Count,
                    StringComparer.Ordinal));
    }

    private sealed record CapabilityACampaignRow(
        Guid CampaignId,
        string CapabilityKey,
        string? ProfileKey,
        string EventType,
        bool DryRun,
        bool AllowUnsafeCandidates,
        int CandidateCount,
        int PlannedCount,
        int QueuedCount,
        int SkippedCount,
        DateTimeOffset OccurredAt,
        string? DetailsJson);

    private sealed record CapabilityACampaignItemRow(
        Guid? DocId,
        string? DocPath,
        Guid? JobId,
        string? PreviewText,
        string? KeySectionTitlesJson,
        string? SuggestedTagsJson,
        string? HypotheticalQuestionsJson,
        double? QualityScore,
        string? QualitySignalsJson,
        DateTimeOffset OccurredAt);

    private sealed record CapabilityBCampaignRow(
        Guid CampaignId,
        string CapabilityKey,
        string? ProfileKey,
        string EventType,
        bool DryRun,
        bool Force,
        int CandidateCount,
        int PlannedCount,
        int QueuedCount,
        int SkippedCount,
        DateTimeOffset OccurredAt,
        string? DetailsJson);

    private sealed record CapabilityBCampaignItemRow(
        Guid? DocId,
        string? DocPath,
        Guid? JobId,
        DateTimeOffset OccurredAt);

    private sealed record CapabilityBCampaignProgress(
        int TrackedJobCount,
        int ActiveJobCount,
        int TerminalJobCount,
        int StoredSummaryCount,
        int? ProgressPercent);

    private sealed record CapabilityBJobStateRow(
        Guid JobId,
        string? JobStatus,
        bool? ResultStored,
        DateTimeOffset? FinishedAt,
        string? StoredSummaryFreshness);

    private sealed record CapabilityBCampaignJobStatusCountRow(
        Guid CampaignId,
        string JobStatus,
        int Count);
}
