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
    d.auto_ingest_pause_reason AS "AutoIngestPauseReason",
    profile.language AS "ProfileLanguage"
  FROM documents d
  LEFT JOIN LATERAL (
    SELECT NULLIF(BTRIM(p.language), '') AS language
    FROM document_profiles p
    JOIN document_revisions r
      ON r.revision_id = p.revision_id
     AND r.tenant_id = p.tenant_id
     AND r.doc_id = p.doc_id
    WHERE p.tenant_id = d.tenant_id
      AND p.doc_id = d.doc_id
      AND r.indexed_version = COALESCE(d.indexed_version, 0)
    ORDER BY
      (NULLIF(BTRIM(p.language), 'und') IS NULL) ASC,
      CASE p.profile_version
        WHEN 'llm_backoffice_v1' THEN 0
        WHEN 'foundation_v1' THEN 1
        ELSE 2
      END,
      r.published_at DESC NULLS LAST,
      p.profile_version ASC
    LIMIT 1
  ) profile ON true
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
  cd."ProfileLanguage",
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
                HypotheticalQuestions: semanticPreview.HypotheticalQuestions,
                QualityScore: semanticPreview.QualityScore,
                QualitySignals: semanticPreview.QualitySignals));
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
        IReadOnlyList<string> excerpts,
        string? documentLanguage = null)
    {
        var resolvedLanguage = ResolveQuestionLanguage(documentLanguage, docName, sectionTitles, excerpts);
        var primaryLanguage = DocumentLanguageResolver.PrimarySubtag(resolvedLanguage);
        var questions = new List<string>();

        foreach (var title in sectionTitles.Take(2))
        {
            var normalizedTitle = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
                continue;

            AddSectionQuestions(questions, primaryLanguage, docName, normalizedTitle);
        }

        if (questions.Count == 0 && excerpts.Count > 0)
        {
            var excerptLead = NormalizePreviewText(excerpts[0]);
            if (excerptLead.Length > 80)
                excerptLead = excerptLead[..80].TrimEnd() + "...";
            AddExcerptQuestions(questions, primaryLanguage, docName, excerptLead);
        }

        return questions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static string ResolveQuestionLanguage(
        string? documentLanguage,
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var explicitLanguage = DocumentLanguageResolver.FirstKnownLanguage(documentLanguage);
        if (!string.IsNullOrWhiteSpace(explicitLanguage))
            return explicitLanguage;

        return DocumentLanguageResolver.DetectDominantLanguage(string.Join(' ', new[]
        {
            docName,
            string.Join(' ', sectionTitles),
            string.Join(' ', excerpts)
        })) ?? "und";
    }

    private static void AddSectionQuestions(List<string> questions, string primaryLanguage, string docName, string title)
    {
        switch (primaryLanguage)
        {
            case "fr":
                questions.Add($"Que dit {docName} sur {title} ?");
                questions.Add($"Quels elements de {docName} concernent {title} ?");
                break;
            case "es":
                questions.Add($"Que dice {docName} sobre {title}?");
                questions.Add($"Que elementos de {docName} se aplican a {title}?");
                break;
            case "pt":
                questions.Add($"O que diz {docName} sobre {title}?");
                questions.Add($"Que elementos de {docName} se aplicam a {title}?");
                break;
            case "de":
                questions.Add($"Was sagt {docName} ueber {title}?");
                questions.Add($"Welche Elemente aus {docName} gelten fuer {title}?");
                break;
            case "it":
                questions.Add($"Che cosa dice {docName} su {title}?");
                questions.Add($"Quali elementi di {docName} si applicano a {title}?");
                break;
            case "en":
            case "":
            case "und":
                questions.Add($"What does {docName} say about {title}?");
                questions.Add($"Which requirements from {docName} apply to {title}?");
                break;
            default:
                questions.Add($"{docName}: {title}?");
                questions.Add($"{title}: {docName}?");
                break;
        }
    }

    private static void AddExcerptQuestions(List<string> questions, string primaryLanguage, string docName, string excerptLead)
    {
        switch (primaryLanguage)
        {
            case "fr":
                questions.Add($"Quels sont les points cles de {docName} ?");
                questions.Add($"{docName}: {excerptLead} ?");
                break;
            case "es":
                questions.Add($"Cuales son los puntos clave de {docName}?");
                questions.Add($"{docName}: {excerptLead}?");
                break;
            case "pt":
                questions.Add($"Quais sao os pontos principais de {docName}?");
                questions.Add($"{docName}: {excerptLead}?");
                break;
            case "de":
                questions.Add($"Was sind die wichtigsten Punkte in {docName}?");
                questions.Add($"{docName}: {excerptLead}?");
                break;
            case "it":
                questions.Add($"Quali sono i punti principali di {docName}?");
                questions.Add($"{docName}: {excerptLead}?");
                break;
            case "en":
            case "":
            case "und":
                questions.Add($"What are the key operational requirements described in {docName}?");
                questions.Add($"How does {docName} frame this topic: {excerptLead}");
                break;
            default:
                questions.Add($"{docName}: {excerptLead}?");
                questions.Add($"{excerptLead}: {docName}?");
                break;
        }
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
            var fallbackTags = hypotheticalQuestionService is null
                ? BuildSuggestedTags(row, Array.Empty<string>())
                : await hypotheticalQuestionService.BuildTagsAsync(row.DocName, row.Category, Array.Empty<string>(), Array.Empty<string>(), ct);
            var fallbackQuality = BuildQualitySignals(null, Array.Empty<string>(), Array.Empty<string>(), fallbackTags, Array.Empty<string>());
            return new CapabilityASemanticPreview(
                PreviewText: null,
                KeySectionTitles: Array.Empty<string>(),
                SuggestedTags: fallbackTags,
                HypotheticalQuestions: Array.Empty<string>(),
                QualityScore: fallbackQuality.Score,
                QualitySignals: fallbackQuality.Signals);
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
            ? BuildHypotheticalQuestions(row.DocName, sectionTitles, excerpts, row.ProfileLanguage)
            : await hypotheticalQuestionService.BuildQuestionsAsync(row.DocName, sectionTitles, excerpts, ct, row.ProfileLanguage);
        var previewText = BuildPreviewText(row, sectionTitles, excerpts);
        var tags = hypotheticalQuestionService is null
            ? BuildSuggestedTags(row, sectionTitles)
            : await hypotheticalQuestionService.BuildTagsAsync(row.DocName, row.Category, sectionTitles, excerpts, ct);
        var quality = BuildQualitySignals(previewText, sectionTitles, excerpts, tags, questions);

        return new CapabilityASemanticPreview(
            PreviewText: previewText,
            KeySectionTitles: sectionTitles,
            SuggestedTags: tags,
            HypotheticalQuestions: questions,
            QualityScore: quality.Score,
            QualitySignals: quality.Signals);
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
        {
            var normalizedTitles = sectionTitles.Select(NormalizeTitle).ToArray();
            var language = DocumentLanguageResolver.PrimarySubtag(
                DocumentLanguageResolver.FirstKnownLanguage(row.ProfileLanguage)
                ?? DocumentLanguageResolver.DetectDominantLanguage(string.Join(' ', normalizedTitles)));
            return language switch
            {
                "fr" => $"{row.DocName} couvre {string.Join(", ", normalizedTitles)}.",
                "es" => $"{row.DocName} cubre {string.Join(", ", normalizedTitles)}.",
                "pt" => $"{row.DocName} cobre {string.Join(", ", normalizedTitles)}.",
                "de" => $"{row.DocName} behandelt {string.Join(", ", normalizedTitles)}.",
                "it" => $"{row.DocName} copre {string.Join(", ", normalizedTitles)}.",
                "en" or "" or "und" => $"{row.DocName} covers {string.Join(", ", normalizedTitles)}.",
                _ => $"{row.DocName}: {string.Join(", ", normalizedTitles)}."
            };
        }

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

    private static (double Score, AdminRuntimeCapabilityAQualitySignalsDto Signals) BuildQualitySignals(
        string? previewText,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        IReadOnlyList<string> suggestedTags,
        IReadOnlyList<string> hypotheticalQuestions)
    {
        var sectionScore = Math.Min(1d, sectionTitles.Count / 3d);
        var tagScore = Math.Min(1d, suggestedTags.Count / 4d);
        var questionScore = Math.Min(1d, hypotheticalQuestions.Count / 2d);
        var previewScore = string.IsNullOrWhiteSpace(previewText)
            ? 0d
            : previewText.Length >= 80
                ? 1d
                : 0.5d;

        var score = Math.Round(
            (sectionScore * 0.30)
            + (tagScore * 0.25)
            + (questionScore * 0.30)
            + (previewScore * 0.15),
            2,
            MidpointRounding.AwayFromZero);

        return (score, new AdminRuntimeCapabilityAQualitySignalsDto(
            SectionTitleCount: sectionTitles.Count,
            ExcerptCount: excerpts.Count,
            SuggestedTagCount: suggestedTags.Count,
            HypotheticalQuestionCount: hypotheticalQuestions.Count,
            SectionCoverageScore: Math.Round(sectionScore, 2, MidpointRounding.AwayFromZero),
            TagScore: Math.Round(tagScore, 2, MidpointRounding.AwayFromZero),
            QuestionScore: Math.Round(questionScore, 2, MidpointRounding.AwayFromZero),
            PreviewScore: Math.Round(previewScore, 2, MidpointRounding.AwayFromZero)));
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
        string? ProfileLanguage,
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
        IReadOnlyList<string> HypotheticalQuestions,
        double QualityScore,
        AdminRuntimeCapabilityAQualitySignalsDto QualitySignals);
}
