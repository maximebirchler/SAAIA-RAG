using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBQualityReviewStore
{
    internal static async Task<AdminRuntimeCapabilityBQualityReviewSummaryDto> LoadLowQualitySummarySnapshotAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        double qualityThreshold,
        CancellationToken ct)
    {
        var aggregate = await conn.QuerySingleAsync<CapabilityBQualityReviewSummaryRow>(new CommandDefinition(
            """
SELECT
  COUNT(*)::int AS "TotalLowQualitySummaries",
  COUNT(*) FILTER (WHERE COALESCE((s.summary_meta ->> 'fallbackUsed')::boolean, false))::int AS "FallbackSummaryCount",
  COUNT(*) FILTER (WHERE COALESCE((s.summary_meta ->> 'fallbackUsed')::boolean, false) = false)::int AS "LiveLlmSummaryCount",
  COUNT(*) FILTER (WHERE s.summary_meta ->> 'runtimeCapabilityStatus' = 'runtime_unavailable')::int AS "RuntimeUnavailableCount",
  MIN((s.summary_meta ->> 'qualityScore')::double precision) AS "LowestQualityScore",
  MAX(s.updated_at) AS "LatestUpdatedAt"
FROM document_summaries s
WHERE s.tenant_id = @tenantId
  AND s.summary_meta IS NOT NULL
  AND s.summary_meta ->> 'generator' = 'capability_b_worker_v2'
  AND jsonb_typeof(s.summary_meta -> 'qualityScore') = 'number'
  AND (s.summary_meta ->> 'qualityScore')::double precision < @qualityThreshold;
""",
            new { tenantId, qualityThreshold },
            cancellationToken: ct));

        var strategyRows = await conn.QueryAsync<NamedCountRow>(new CommandDefinition(
            """
SELECT
  COALESCE(NULLIF(s.summary_meta ->> 'strategy', ''), 'unknown') AS "Key",
  COUNT(*)::int AS "Count"
FROM document_summaries s
WHERE s.tenant_id = @tenantId
  AND s.summary_meta IS NOT NULL
  AND s.summary_meta ->> 'generator' = 'capability_b_worker_v2'
  AND jsonb_typeof(s.summary_meta -> 'qualityScore') = 'number'
  AND (s.summary_meta ->> 'qualityScore')::double precision < @qualityThreshold
GROUP BY COALESCE(NULLIF(s.summary_meta ->> 'strategy', ''), 'unknown')
ORDER BY "Count" DESC, "Key" ASC;
""",
            new { tenantId, qualityThreshold },
            cancellationToken: ct));

        var runtimeStatusRows = await conn.QueryAsync<NamedCountRow>(new CommandDefinition(
            """
SELECT
  COALESCE(NULLIF(s.summary_meta ->> 'runtimeCapabilityStatus', ''), 'unknown') AS "Key",
  COUNT(*)::int AS "Count"
FROM document_summaries s
WHERE s.tenant_id = @tenantId
  AND s.summary_meta IS NOT NULL
  AND s.summary_meta ->> 'generator' = 'capability_b_worker_v2'
  AND jsonb_typeof(s.summary_meta -> 'qualityScore') = 'number'
  AND (s.summary_meta ->> 'qualityScore')::double precision < @qualityThreshold
GROUP BY COALESCE(NULLIF(s.summary_meta ->> 'runtimeCapabilityStatus', ''), 'unknown')
ORDER BY "Count" DESC, "Key" ASC;
""",
            new { tenantId, qualityThreshold },
            cancellationToken: ct));

        return new AdminRuntimeCapabilityBQualityReviewSummaryDto(
            aggregate.TotalLowQualitySummaries,
            aggregate.FallbackSummaryCount,
            aggregate.LiveLlmSummaryCount,
            aggregate.RuntimeUnavailableCount,
            aggregate.LowestQualityScore,
            aggregate.LatestUpdatedAt,
            strategyRows.Select(static row => new AdminRuntimeNamedCountDto(row.Key, row.Count)).ToArray(),
            runtimeStatusRows.Select(static row => new AdminRuntimeNamedCountDto(row.Key, row.Count)).ToArray(),
            BuildSummaryRecommendations(aggregate));
    }

    internal static async Task<AdminRuntimeCapabilityBQualityReviewItemDto[]> LoadLowQualitySummariesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        double qualityThreshold,
        int limit,
        CancellationToken ct)
    {
        var rows = await conn.QueryAsync<CapabilityBQualityReviewRow>(new CommandDefinition(
            """
SELECT
  d.doc_id AS "DocId",
  d.doc_path AS "DocPath",
  d.doc_name AS "DocName",
  d.category AS "Category",
  s.level AS "Level",
  (s.summary_meta ->> 'qualityScore')::double precision AS "QualityScore",
  s.summary_meta ->> 'strategy' AS "Strategy",
  COALESCE((s.summary_meta ->> 'fallbackUsed')::boolean, false) AS "FallbackUsed",
  s.summary_meta ->> 'fallbackReason' AS "FallbackReason",
  s.summary_meta ->> 'runtimeCapabilityStatus' AS "RuntimeCapabilityStatus",
  char_length(COALESCE(s.summary_text, '')) AS "SummaryLength",
  s.updated_at AS "UpdatedAt",
  CASE
    WHEN s.summary_meta ? 'qualitySignals' THEN (s.summary_meta -> 'qualitySignals')::text
    ELSE NULL
  END AS "QualitySignalsJson"
FROM document_summaries s
JOIN documents d
  ON d.tenant_id = s.tenant_id
 AND d.doc_id = s.doc_id
WHERE s.tenant_id = @tenantId
  AND s.summary_meta IS NOT NULL
  AND s.summary_meta ->> 'generator' = 'capability_b_worker_v2'
  AND jsonb_typeof(s.summary_meta -> 'qualityScore') = 'number'
  AND (s.summary_meta ->> 'qualityScore')::double precision < @qualityThreshold
ORDER BY (s.summary_meta ->> 'qualityScore')::double precision ASC, s.updated_at DESC
LIMIT @limit;
""",
            new
            {
                tenantId,
                qualityThreshold,
                limit = Math.Clamp(limit, 1, 200)
            },
            cancellationToken: ct));

        return rows
            .Select(MapRow)
            .ToArray();
    }

    private static AdminRuntimeCapabilityBQualityReviewItemDto MapRow(CapabilityBQualityReviewRow row)
    {
        var signals = ParseSignals(row.QualitySignalsJson);
        var recommendations = BuildRecommendations(row, signals);

        return new AdminRuntimeCapabilityBQualityReviewItemDto(
            row.DocId,
            row.DocPath,
            row.DocName,
            row.Category,
            row.Level,
            row.QualityScore,
            row.Strategy,
            row.FallbackUsed,
            row.FallbackReason,
            row.RuntimeCapabilityStatus,
            row.SummaryLength,
            row.UpdatedAt,
            signals,
            recommendations);
    }

    private static AdminRuntimeCapabilityBQualitySignalsDto ParseSignals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new AdminRuntimeCapabilityBQualitySignalsDto(null, null, null, null, null, null, null, null, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AdminRuntimeCapabilityBQualitySignalsDto(
            ReadInt(root, "lineCount"),
            ReadDouble(root, "lengthScore"),
            ReadDouble(root, "structureScore"),
            ReadDouble(root, "sectionCoverageScore"),
            ReadInt(root, "matchedSectionCount"),
            ReadInt(root, "expectedSectionCount"),
            ReadDouble(root, "keywordCoverageScore"),
            ReadInt(root, "matchedKeywordCount"),
            ReadInt(root, "expectedKeywordCount"));
    }

    private static string[] BuildRecommendations(
        CapabilityBQualityReviewRow row,
        AdminRuntimeCapabilityBQualitySignalsDto signals)
    {
        var recommendations = new List<string>();

        if (row.FallbackUsed)
        {
            recommendations.Add(string.IsNullOrWhiteSpace(row.FallbackReason)
                ? "review why this summary used the deterministic fallback instead of the live B runtime"
                : $"review the fallback path because this summary used '{row.FallbackReason}'");
        }

        if (signals.SectionCoverageScore is < 0.6)
            recommendations.Add("review section coverage because the summary does not echo enough indexed section titles");

        if (signals.KeywordCoverageScore is < 0.45)
            recommendations.Add("review excerpt coverage because too few operational keywords from the document made it into the summary");

        if (signals.StructureScore is < 0.7 || signals.LineCount is < 2 or > 6)
            recommendations.Add("review structure because the summary falls outside the expected 2 to 6 short lines/paragraphs");

        if (signals.LengthScore is < 0.6 || row.SummaryLength < 140 || row.SummaryLength > 980)
            recommendations.Add("review length because the summary is outside the preferred compact backoffice range");

        if (recommendations.Count == 0)
            recommendations.Add("review this summary manually because its aggregate quality score is below the configured threshold");

        return recommendations.ToArray();
    }

    private static string[] BuildSummaryRecommendations(CapabilityBQualityReviewSummaryRow summary)
    {
        var recommendations = new List<string>();

        if (summary.TotalLowQualitySummaries == 0)
        {
            recommendations.Add("no low-quality capability B summaries are currently stored below the configured threshold");
            return recommendations.ToArray();
        }

        if (summary.RuntimeUnavailableCount > 0)
            recommendations.Add("investigate runtime availability first because some low-quality summaries were produced while the live B runtime was unavailable");

        if (summary.FallbackSummaryCount > 0)
            recommendations.Add("review fallback-heavy periods because deterministic summaries are over-represented in the low-quality bucket");

        if (summary.LowestQualityScore is < 0.4)
            recommendations.Add("prioritize the worst summaries first because at least one stored result is far below the current quality threshold");

        if (recommendations.Count == 0)
            recommendations.Add("review prompt drift and retrieval context quality because low-scoring summaries are accumulating without obvious runtime fallback signals");

        return recommendations.ToArray();
    }

    private static int? ReadInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static double? ReadDouble(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    private sealed record CapabilityBQualityReviewRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        string Level,
        double QualityScore,
        string? Strategy,
        bool FallbackUsed,
        string? FallbackReason,
        string? RuntimeCapabilityStatus,
        int SummaryLength,
        DateTimeOffset UpdatedAt,
        string? QualitySignalsJson);

    private sealed record CapabilityBQualityReviewSummaryRow(
        int TotalLowQualitySummaries,
        int FallbackSummaryCount,
        int LiveLlmSummaryCount,
        int RuntimeUnavailableCount,
        double? LowestQualityScore,
        DateTimeOffset? LatestUpdatedAt);

    private sealed record NamedCountRow(
        string Key,
        int Count);
}
