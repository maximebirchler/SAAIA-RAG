using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityAEnrichmentStore
{
    internal static async Task<AdminRuntimeCapabilityAEnrichmentCandidateDto[]> LoadCandidatesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IngestionOptions ingest,
        CapabilityAHypotheticalQuestionService? hypotheticalQuestionService,
        string? category,
        int limit,
        IReadOnlyList<string>? reasonFilters,
        CancellationToken ct)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        var rows = await conn.QueryAsync<CapabilityAEnrichmentCandidateRow>(new CommandDefinition(
            """
WITH current_docs AS (
  SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    d.status AS "Status",
    COALESCE(d.ingestion_version, 0) AS "IngestionVersion",
    COALESCE(d.indexed_version, 0) AS "IndexedVersion",
    COALESCE(d.auto_ingest_paused, false) AS "AutoIngestPaused",
    d.auto_ingest_pause_reason AS "AutoIngestPauseReason"
  FROM documents d
  WHERE d.tenant_id = @tenant
    AND (@category IS NULL OR d.category = @category)
    AND COALESCE(d.status, '') NOT IN ('missing', 'deleted')
)
SELECT
  cd."DocId",
  cd."DocPath",
  cd."DocName",
  cd."Category",
  cd."Status",
  cd."IngestionVersion",
  cd."IndexedVersion",
  cd."AutoIngestPaused",
  cd."AutoIngestPauseReason",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasRevision",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN retrieval_chunks rc ON rc.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasRetrievalChunks",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN retrieval_chunks rc ON rc.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion"
      AND (
        NOT (rc.metadata ? 'offsetStart')
        OR NOT (rc.metadata ? 'offsetEnd')
        OR jsonb_typeof(rc.metadata->'offsetStart') <> 'number'
        OR jsonb_typeof(rc.metadata->'offsetEnd') <> 'number')) AS "HasRetrievalChunkOffsetsMissing",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN exact_match_entries eme ON eme.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasExactMatchEntries",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN exact_match_entries eme ON eme.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion"
      AND (
        NOT (eme.metadata ? 'offsetStart')
        OR NOT (eme.metadata ? 'offsetEnd')
        OR jsonb_typeof(eme.metadata->'offsetStart') <> 'number'
        OR jsonb_typeof(eme.metadata->'offsetEnd') <> 'number')) AS "HasExactMatchOffsetsMissing",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN contextual_text_entries cte ON cte.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasContextualTextEntries"
FROM current_docs cd
ORDER BY cd."Category", cd."DocPath"
LIMIT @limit;
""",
            new { tenant = tenantId, category = normalizedCategory, limit = Math.Clamp(limit, 1, 200) },
            cancellationToken: ct));

        var reasonFilterSet = reasonFilters?
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<AdminRuntimeCapabilityAEnrichmentCandidateDto>();
        foreach (var row in rows)
        {
            var reasons = BuildReasons(row);
            if (reasons.Count == 0)
                continue;

            if (reasonFilterSet is not null && reasonFilterSet.Count > 0 && !reasons.Any(reason => reasonFilterSet.Contains(reason)))
                continue;

            var fileExists = ResolveFileExists(ingest, row.DocPath);
            if (!fileExists)
                reasons.Add("document_file_not_found");

            var recommendedAction = fileExists ? "enqueue_reindex" : "inspect_document_source";
            var priorityScore = BuildPriorityScore(reasons, row);
            var semanticPreview = await BuildSemanticPreviewAsync(conn, tenantId, row, hypotheticalQuestionService, ct);

            candidates.Add(new AdminRuntimeCapabilityAEnrichmentCandidateDto(
                DocId: row.DocId,
                DocPath: row.DocPath,
                DocName: row.DocName,
                Category: row.Category,
                Status: row.Status,
                IngestionVersion: row.IngestionVersion,
                IndexedVersion: row.IndexedVersion,
                PriorityScore: priorityScore,
                FileExists: fileExists,
                RecommendedAction: recommendedAction,
                Reasons: reasons
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(static reason => reason.Equals("document_file_not_found", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                PreviewText: semanticPreview.PreviewText,
                KeySectionTitles: semanticPreview.KeySectionTitles,
                SuggestedTags: semanticPreview.SuggestedTags,
                HypotheticalQuestions: semanticPreview.HypotheticalQuestions));
        }

        return candidates
            .OrderByDescending(static candidate => candidate.PriorityScore)
            .ThenBy(static candidate => candidate.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildHypotheticalQuestions(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var questions = new List<string>();

        foreach (var title in sectionTitles.Take(2))
        {
            var normalizedTitle = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
                continue;

            questions.Add($"What does {docName} say about {normalizedTitle}?");
            questions.Add($"Which requirements from {docName} apply to {normalizedTitle}?");
        }

        if (questions.Count == 0 && excerpts.Count > 0)
        {
            var excerptLead = NormalizePreviewText(excerpts[0]);
            if (excerptLead.Length > 80)
                excerptLead = excerptLead[..80].TrimEnd() + "...";
            questions.Add($"What are the key operational requirements described in {docName}?");
            questions.Add($"How does {docName} frame this topic: {excerptLead}");
        }

        return questions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static List<string> BuildReasons(CapabilityAEnrichmentCandidateRow row)
    {
        var reasons = new List<string>();

        if (row.IndexedVersion <= 0)
            reasons.Add("never_indexed");
        if (row.IngestionVersion > row.IndexedVersion)
            reasons.Add("indexed_version_outdated");
        if (row.IndexedVersion > 0 && !row.HasRevision)
            reasons.Add("revision_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasRetrievalChunks)
            reasons.Add("retrieval_chunks_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && row.HasRetrievalChunks && row.HasRetrievalChunkOffsetsMissing)
            reasons.Add("retrieval_chunk_offsets_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasExactMatchEntries)
            reasons.Add("exact_match_entries_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && row.HasExactMatchEntries && row.HasExactMatchOffsetsMissing)
            reasons.Add("exact_match_offsets_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasContextualTextEntries)
            reasons.Add("contextual_text_entries_missing");
        if (row.AutoIngestPaused)
            reasons.Add("auto_ingest_paused");

        return reasons;
    }

    private static async Task<CapabilityASemanticPreview> BuildSemanticPreviewAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        CapabilityAEnrichmentCandidateRow row,
        CapabilityAHypotheticalQuestionService? hypotheticalQuestionService,
        CancellationToken ct)
    {
        if (row.IndexedVersion <= 0 || !row.HasRevision)
        {
            return new CapabilityASemanticPreview(
                PreviewText: null,
                KeySectionTitles: Array.Empty<string>(),
                SuggestedTags: BuildSuggestedTags(row, Array.Empty<string>()),
                HypotheticalQuestions: Array.Empty<string>());
        }

        var sectionTitles = await RuntimeGovernanceService.LoadCapabilityBSectionTitlesAsync(
            conn,
            tenantId,
            row.DocId,
            row.IndexedVersion,
            limit: 3,
            ct);
        var excerpts = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsAsync(
            conn,
            tenantId,
            row.DocId,
            row.IndexedVersion,
            limit: 2,
            ct);

        var questions = hypotheticalQuestionService is null
            ? BuildHypotheticalQuestions(row.DocName, sectionTitles, excerpts)
            : await hypotheticalQuestionService.BuildQuestionsAsync(row.DocName, sectionTitles, excerpts, ct);
        var previewText = BuildPreviewText(row, sectionTitles, excerpts);
        var tags = BuildSuggestedTags(row, sectionTitles);

        return new CapabilityASemanticPreview(
            PreviewText: previewText,
            KeySectionTitles: sectionTitles,
            SuggestedTags: tags,
            HypotheticalQuestions: questions);
    }

    private static string? BuildPreviewText(
        CapabilityAEnrichmentCandidateRow row,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        if (excerpts.Count > 0)
        {
            var normalizedExcerpt = NormalizePreviewText(excerpts[0]);
            return normalizedExcerpt.Length <= 240
                ? normalizedExcerpt
                : normalizedExcerpt[..237] + "...";
        }

        if (sectionTitles.Count > 0)
            return $"{row.DocName} covers {string.Join(", ", sectionTitles.Select(NormalizeTitle))}.";

        return null;
    }

    private static IReadOnlyList<string> BuildSuggestedTags(
        CapabilityAEnrichmentCandidateRow row,
        IReadOnlyList<string> sectionTitles)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(row.Category))
            tags.Add(NormalizeTag(row.Category));

        foreach (var token in ExtractTagTokens(Path.GetFileNameWithoutExtension(row.DocName)))
            tags.Add(token);

        foreach (var title in sectionTitles)
        {
            foreach (var token in ExtractTagTokens(title))
                tags.Add(token);
        }

        return tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static IEnumerable<string> ExtractTagTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var token in value
            .Split([' ', '-', '_', '/', '\\', ',', ';', ':', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTag)
            .Where(static token => token.Length >= 3))
        {
            yield return token;
        }
    }

    private static string NormalizeTag(string value)
        => new(value
            .Trim()
            .ToLowerInvariant()
            .Where(static ch => char.IsLetterOrDigit(ch) || ch == '-')
            .ToArray());

    private static string NormalizeTitle(string value)
        => string.Join(" ", value
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();

    private static string NormalizePreviewText(string value)
        => string.Join(" ", value
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();

    private static int BuildPriorityScore(IReadOnlyCollection<string> reasons, CapabilityAEnrichmentCandidateRow row)
    {
        var score = reasons.Count;
        if (row.IndexedVersion <= 0)
            score += 5;
        if (row.IngestionVersion > row.IndexedVersion)
            score += 3;
        if (reasons.Contains("retrieval_chunks_missing", StringComparer.OrdinalIgnoreCase))
            score += 3;
        if (reasons.Contains("retrieval_chunk_offsets_missing", StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (reasons.Contains("exact_match_entries_missing", StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (reasons.Contains("exact_match_offsets_missing", StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (reasons.Contains("contextual_text_entries_missing", StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (reasons.Contains("document_file_not_found", StringComparer.OrdinalIgnoreCase))
            score += 1;
        return score;
    }

    private static bool ResolveFileExists(IngestionOptions ingest, string docPath)
    {
        if (string.IsNullOrWhiteSpace(ingest.DocumentsRoot))
            return false;

        try
        {
            var absolutePath = DocPathNormalizer.ToAbsoluteFromRelative(docPath, ingest.DocumentsRoot);
            return File.Exists(absolutePath);
        }
        catch
        {
            return false;
        }
    }

    private sealed record CapabilityAEnrichmentCandidateRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string Category,
        string Status,
        int IngestionVersion,
        int IndexedVersion,
        bool AutoIngestPaused,
        string? AutoIngestPauseReason,
        bool HasRevision,
        bool HasRetrievalChunks,
        bool HasRetrievalChunkOffsetsMissing,
        bool HasExactMatchEntries,
        bool HasExactMatchOffsetsMissing,
        bool HasContextualTextEntries);

    private sealed record CapabilityASemanticPreview(
        string? PreviewText,
        IReadOnlyList<string> KeySectionTitles,
        IReadOnlyList<string> SuggestedTags,
        IReadOnlyList<string> HypotheticalQuestions);
}
