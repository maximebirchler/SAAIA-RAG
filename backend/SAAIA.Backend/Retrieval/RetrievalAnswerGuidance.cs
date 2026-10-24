using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalRows;
using static SAAIA.Backend.Retrieval.RetrievalText;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalAnswerGuidance
{

    internal static string ResolveProvenance(RagMatch match)
        => $"retriever:{ResolveRetriever(match)}";


    internal static RagAnswerGuidanceDto BuildAnswerGuidance(string query, IReadOnlyList<RagMatch> matches)
        => BuildAnswerGuidance(
            query,
            matches,
            new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase));


    internal static RagAnswerGuidanceDto BuildAnswerGuidance(
        string query,
        IReadOnlyList<RagMatch> matches,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
    {
        var normalized = NormalizeQueryForGuidance(query);
        var guidanceLanguage = ResolveGuidanceLanguage(query);
        var matchedContext = BuildMatchedRetrievalContext(matches);
        var matchedDocHints = matchedContext.DocHints;

        if (ContainsPlaceholderStandard(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "missing_standard_identifier",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "missing_standard_identifier", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (matches.Count == 0)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: "no_relevant_source_found",
                ResponseShape: "no_source_match",
                QualificationNote: BuildNoRelevantSourceNote(guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsCertificationScopeQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "certification_requires_precise_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "certification_requires_precise_scope", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsBroadComplianceQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "broad_compliance_requires_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "broad_compliance_requires_scope", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsAmbiguousBareFragmentQuery(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "ambiguous_bare_fragment_requires_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "ambiguous_bare_fragment_requires_scope", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        var qualityRisk = AssessGuidanceQualityRisk(matches, extractionQualityByMatch);
        if (qualityRisk.RequiresCaveat)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: qualityRisk.Reason,
                ResponseShape: DetermineResponseShape(normalized, behavior: "answer_with_caveat"),
                QualificationNote: BuildSourceQualityQualificationNote(matchedContext, guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsSufficiencyQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer",
                Reason: "document_scope_limit_can_be_answered_directly",
                ResponseShape: "qualified_answer",
                MatchedDocHints: matchedDocHints);
        }

        var simpleDocumentSelection = ContainsSimpleDocumentSelectionQuestion(normalized);
        var needsQualification =
            ContainsRiskRecommendationQuestion(normalized)
            || ContainsQuickCustomerReplySelectionQuestion(normalized)
            || (!simpleDocumentSelection && (
                ContainsComplianceLanguage(normalized)
                || ContainsCustomerReplyLanguage(normalized)
                || ContainsProjectAssessmentLanguage(normalized)
                || ContainsRiskRecommendationQuestion(normalized)));

        if (needsQualification)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: "high_impact_or_safety_answer_requires_qualification",
                ResponseShape: DetermineResponseShape(normalized, behavior: "answer_with_caveat"),
                QualificationNote: BuildHighImpactSafetyQualificationNote(normalized, matchedContext, simpleDocumentSelection, guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        return new RagAnswerGuidanceDto(
            Behavior: "answer",
            Reason: "documented_question_with_relevant_sources",
            ResponseShape: DetermineResponseShape(normalized, behavior: "answer"),
            MatchedDocHints: matchedDocHints);
    }


    private static GuidanceQualityRisk AssessGuidanceQualityRisk(
        IReadOnlyList<RagMatch> matches,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
    {
        foreach (var match in matches.Take(5))
        {
            var quality = ResolveExtractionQuality(match, extractionQualityByMatch);
            if (quality is null)
                continue;

            if (quality.DocumentManualReviewRecommended == true || quality.PageManualReviewRecommended == true)
                return new GuidanceQualityRisk(true, "source_quality_requires_manual_review_caveat");

            var confidence = quality.PageExtractionConfidence ?? quality.DocumentExtractionConfidence;
            if (confidence is <= 0.45)
                return new GuidanceQualityRisk(true, "source_quality_low_confidence_caveat");

            var status = string.Join(' ', quality.PageQualityStatus, quality.DocumentQualityStatus, quality.TextStatus)
                .ToLowerInvariant();
            if (status.Contains("ocr_failed", StringComparison.Ordinal)
                || status.Contains("empty_text", StringComparison.Ordinal)
                || status.Contains("low_text", StringComparison.Ordinal)
                || status.Contains("low_confidence", StringComparison.Ordinal)
                || status.Contains("manual_review", StringComparison.Ordinal))
            {
                return new GuidanceQualityRisk(true, "source_quality_status_caveat");
            }
        }

        return new GuidanceQualityRisk(false, "source_quality_ok");
    }


    internal static RagItemProvenanceDto BuildProvenanceInfo(RagMatch match, string? sourceHash = null, bool allowLegacyHashFallback = true)
        => new(
            Channel: ResolveRetriever(match),
            Label: ResolveProvenance(match),
            SourceHash: sourceHash ?? (allowLegacyHashFallback ? match.HashDoc : null),
            ChunkId: match.ChunkId,
            PageStart: match.PageStart,
            PageEnd: match.PageEnd,
            OffsetStart: match.OffsetStart,
            OffsetEnd: match.OffsetEnd);


    internal static RagItemContextDto BuildContextInfo(RagMatch match)
    {
        var contentRole = match.ContentRole;
        var navigationReason = match.NavigationReason;
        var navigationScore = match.NavigationScore;
        var contentDensityScore = match.ContentDensityScore;

        if (ShouldPromoteMeasuredSequentialContentSignal(match))
        {
            contentRole = RetrievalContentClassifier.ContentRole;
            navigationReason = null;
            navigationScore = 0.0;
            contentDensityScore = Math.Max(contentDensityScore ?? 0.0, 0.70);
        }

        return new(
            ChunkType: match.ChunkType,
            SectionTitle: match.SectionTitle,
            HeadingPath: match.HeadingPath,
            PrevChunkId: match.PrevChunkId,
            NextChunkId: match.NextChunkId,
            SameSectionChunkId: match.SameSectionChunkId,
            ContentRole: contentRole,
            NavigationReason: navigationReason,
            OriginalChunkType: match.OriginalChunkType,
            NavigationScore: navigationScore,
            ContentDensityScore: contentDensityScore,
            SourceUnitOrdinals: match.SourceUnitOrdinals,
            SourceUnitStartOrdinal: match.SourceUnitStartOrdinal,
            SourceUnitEndOrdinal: match.SourceUnitEndOrdinal,
            SourceUnitCount: match.SourceUnitCount,
            ChunkComposition: match.ChunkComposition);
    }


    private static bool ShouldPromoteMeasuredSequentialContentSignal(RagMatch match)
    {
        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase)
            || RetrievalContentClassifier.IsNavigationChunkType(match.ChunkType))
        {
            return false;
        }

        var text = match.Text ?? string.Empty;
        if (!RetrievalContentClassifier.LooksLikeMeasuredSequentialContent(text))
            return false;

        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";

        return !HasStrongNavigationalMarker(folded, padded)
            && !ContainsExplicitTableOfContentsMarker(folded, padded)
            && !ContainsShortTableOfContentsMarker(padded);
    }


    internal static async Task<IReadOnlyDictionary<string, string>> LoadTopCategoryRefsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var topLevelPaths = matches
            .Select(match => ExtractTopLevelCategoryPath(BuildDocumentCategoryPath(match.DocPath)))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (topLevelPaths.Length == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
  path          AS "Path",
  display_order AS "DisplayOrder"
FROM documents_catalog_categories
WHERE tenant_id=@tenant
  AND path = ANY(@paths);
""";

        var rows = await conn.QueryAsync<TopCategoryOrderRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, paths = topLevelPaths },
            cancellationToken: ct));

        return rows.ToDictionary(
            static row => row.Path,
            static row => BuildCategoryRef(row.DisplayOrder),
            StringComparer.OrdinalIgnoreCase);
    }


    internal static string? BuildDocumentCategory(string? docPath)
    {
        var topLevel = ExtractTopLevelCategoryPath(BuildDocumentCategoryPath(docPath));
        return string.IsNullOrWhiteSpace(topLevel)
            ? null
            : topLevel.ToLowerInvariant();
    }


    internal static string? ResolveMatchCategory(RagMatch match, string? responseCategory)
    {
        var storedCategory = NormalizeRagCategory(match.Category);
        if (!string.IsNullOrWhiteSpace(storedCategory))
            return storedCategory;

        return BuildDocumentCategory(match.DocPath) ?? NormalizeRagCategory(responseCategory);
    }


    internal static string? NormalizeRagCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();


    internal static string? BuildDocumentCategoryPath(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var normalized = docPath.Trim().Replace('\\', '/').Trim('/');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex <= 0)
            return null;

        return normalized[..slashIndex];
    }


    internal static string? ResolveCategoryRef(string? categoryPath, IReadOnlyDictionary<string, string> categoryRefsByTopLevelPath)
    {
        var topLevel = ExtractTopLevelCategoryPath(categoryPath);
        if (string.IsNullOrWhiteSpace(topLevel))
            return null;

        return categoryRefsByTopLevelPath.TryGetValue(topLevel, out var categoryRef)
            ? categoryRef
            : null;
    }


    internal static string? BuildSnippet(string? text, int maxLength = 500, string? query = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        var anchor = FindQuerySnippetAnchor(normalized, query);
        if (anchor >= 0)
            return BuildCenteredSnippet(normalized, anchor, maxLength);

        // Cut at last sentence boundary within limit
        var cutoff = normalized.LastIndexOf('.', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = normalized.LastIndexOf(' ', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return normalized[..(cutoff + 1)].TrimEnd();
    }


    private static int FindQuerySnippetAnchor(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
            return -1;

        var folded = BuildFoldedTextIndex(text);
        if (folded.Text.Length == 0 || folded.OriginalIndexes.Count == 0)
            return -1;

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !SnippetAnchorLowPriorityTerms.Contains(token))
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length == 0)
        {
            tokens = ExtractLexicalQueryTokens(query)
                .OrderByDescending(static token => token.Length)
                .ThenBy(static token => token, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var token in tokens)
        {
            var index = folded.Text.IndexOf(token, StringComparison.Ordinal);
            if (index >= 0 && index < folded.OriginalIndexes.Count)
                return folded.OriginalIndexes[index];
        }

        return -1;
    }


    private static string BuildCenteredSnippet(string text, int anchor, int maxLength)
    {
        if (maxLength <= 20 || text.Length <= maxLength)
            return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();

        var start = Math.Max(0, anchor - (maxLength / 3));
        start = MoveSnippetStartToBoundary(text, start, anchor);

        if (start + maxLength > text.Length)
            start = Math.Max(0, text.Length - maxLength);

        var length = Math.Min(maxLength, text.Length - start);
        var end = start + length;
        end = MoveSnippetEndToBoundary(text, end, start);

        var snippet = text[start..end].Trim();
        if (start > 0)
            snippet = "..." + snippet.TrimStart();
        if (end < text.Length)
            snippet = snippet.TrimEnd() + "...";

        return snippet.Length <= maxLength
            ? snippet
            : snippet[..maxLength].TrimEnd();
    }


    private static int MoveSnippetStartToBoundary(string text, int start, int anchor)
    {
        if (start <= 0)
            return 0;

        var searchLength = Math.Max(0, anchor - start);
        if (searchLength == 0)
            return start;

        var sentence = text.LastIndexOfAny(new[] { '.', '!', '?', '\n', '\r' }, start + searchLength - 1, searchLength);
        if (sentence >= start && sentence + 1 < anchor)
            return sentence + 1;

        var space = text.IndexOf(' ', start);
        return space >= 0 && space < anchor ? space + 1 : start;
    }


    private static int MoveSnippetEndToBoundary(string text, int end, int start)
    {
        if (end >= text.Length)
            return text.Length;

        var sentence = text.LastIndexOfAny(new[] { '.', '!', '?', '\n', '\r' }, end - 1, Math.Max(0, end - start));
        if (sentence > start + ((end - start) / 2))
            return sentence + 1;

        var space = text.LastIndexOf(' ', end - 1, Math.Max(0, end - start));
        return space > start + ((end - start) / 2) ? space : end;
    }


    private static FoldedTextIndex BuildFoldedTextIndex(string value)
    {
        var sb = new StringBuilder(value.Length);
        var indexes = new List<int>(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var normalized = value[i].ToString().Normalize(NormalizationForm.FormD);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                foreach (var folded in FoldSnippetChar(ch))
                {
                    sb.Append(char.ToLowerInvariant(folded));
                    indexes.Add(i);
                }
            }
        }

        return new FoldedTextIndex(sb.ToString().Normalize(NormalizationForm.FormC), indexes);
    }


    private static string FoldSnippetChar(char ch)
        => ch switch
        {
            'œ' or 'Œ' => "oe",
            'æ' or 'Æ' => "ae",
            'ß' => "ss",
            'ø' or 'Ø' => "o",
            'ł' or 'Ł' => "l",
            'đ' or 'Đ' => "d",
            _ => ch.ToString()
        };


    private static readonly HashSet<string> SnippetAnchorLowPriorityTerms = new(StringComparer.Ordinal)
    {
        "document",
        "documents",
        "fiche",
        "claire",
        "faire",
        "peux",
        "veux",
        "donne",
        "trouve",
        "etapes",
        "etape",
        "temps",
        "source",
        "sources",
        "page",
        "pages"
    };


    private sealed record FoldedTextIndex(string Text, IReadOnlyList<int> OriginalIndexes);


    internal static bool DetectHasTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        // Heuristic: rows with pipe separators or tab-separated columns
        var lines = text.Split('\n');
        var pipeLines = lines.Count(l => l.Contains('|') && l.Count(c => c == '|') >= 2);
        return pipeLines >= 2;
    }


    internal static bool DetectHasWarning(string? text, string? chunkType)
    {
        if (string.Equals(chunkType, "warning", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var upper = text.AsSpan();
        return upper.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("DANGER", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("CAUTION", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("AVERTISSEMENT", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase);
    }


    internal static string? ComputeDataHash(IReadOnlyList<RagMatch> matches)
    {
        if (matches.Count == 0)
            return null;

        using var sha = SHA256.Create();
        foreach (var match in matches)
        {
            var canonical = $"{match.DocId}|{match.ChunkId}|{match.IngestionVersion}|{match.HashDoc}|{match.EmbeddingBasis}";
            var bytes = Encoding.UTF8.GetBytes(canonical);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }


    internal static IReadOnlyList<string> ExtractMatchedDocHints(IReadOnlyList<RagMatch> matches)
    {
        var hints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            CollectDocumentHints(match.DocName, hints);
            CollectDocumentHints(match.DocPath, hints);
        }

        if (hints.Count == 0)
            return Array.Empty<string>();

        var ordered = hints.OrderBy(static h => h, StringComparer.Ordinal).ToArray();
        var filtered = ordered
            .Where(hint => !ordered.Any(other =>
                !string.Equals(other, hint, StringComparison.Ordinal)
                && other.Length < hint.Length
                && hint.EndsWith(other, StringComparison.Ordinal)))
            .ToArray();

        return filtered.Length == 0 ? ordered : filtered;
    }


    internal static MatchedRetrievalContext BuildMatchedRetrievalContext(IReadOnlyList<RagMatch> matches)
    {
        var hints = ExtractMatchedDocHints(matches);

        return new MatchedRetrievalContext(hints);
    }


    private static bool ContainsPlaceholderStandard(string normalizedQuery)
        => normalizedQuery.Contains("norme xxx", StringComparison.Ordinal)
           || normalizedQuery.Contains("standard xxx", StringComparison.Ordinal);


    private static bool ContainsCertificationScopeQuestion(string normalizedQuery)
    {
        var functionSafetyClaim =
            (normalizedQuery.Contains("fonction de securite", StringComparison.Ordinal)
                && (normalizedQuery.Contains("affirmer", StringComparison.Ordinal)
                    || normalizedQuery.Contains("adapte", StringComparison.Ordinal)
                    || normalizedQuery.Contains("adapted", StringComparison.Ordinal)
                    || normalizedQuery.Contains("claim", StringComparison.Ordinal)))
            || (normalizedQuery.Contains("safety function", StringComparison.Ordinal)
                && (normalizedQuery.Contains("claim", StringComparison.Ordinal)
                    || normalizedQuery.Contains("adapted", StringComparison.Ordinal)));
        if (functionSafetyClaim)
            return !ContainsSimpleDocumentSelectionQuestion(normalizedQuery);

        return (normalizedQuery.Contains("certification", StringComparison.Ordinal)
                || normalizedQuery.Contains("certifie", StringComparison.Ordinal)
                || normalizedQuery.Contains("certifiee", StringComparison.Ordinal)
                || normalizedQuery.Contains("homologation", StringComparison.Ordinal)
                || normalizedQuery.Contains("approval", StringComparison.Ordinal)
                || normalizedQuery.Contains("approved", StringComparison.Ordinal)
                || normalizedQuery.Contains("qualification", StringComparison.Ordinal))
            && !HasSpecificReferenceLookup(normalizedQuery)
            && !ContainsSimpleDocumentSelectionQuestion(normalizedQuery);
    }


    private static bool ContainsComplianceLanguage(string normalizedQuery)
        => normalizedQuery.Contains("respecte", StringComparison.Ordinal)
           || normalizedQuery.Contains("conforme", StringComparison.Ordinal)
           || normalizedQuery.Contains("conformite", StringComparison.Ordinal)
           || normalizedQuery.Contains("compliance", StringComparison.Ordinal);


    private static bool ContainsHighImpactClaimLanguage(string normalizedQuery)
    {
        var foldedQuery = FoldDiacritics(normalizedQuery);
        return HighImpactClaimTerms.Any(term => ContainsNormalizedWholeTerm(foldedQuery, term));
    }


    private static readonly string[] HighImpactClaimTerms =
    [
        "garantir",
        "garantie",
        "obligation",
        "obligatoire",
        "reglementaire",
        "regulatory",
        "legal",
        "securite",
        "safety",
        "protection",
        "protege",
        "danger",
        "risque",
        "risques",
        "risk",
        "risks",
        "critique",
        "critical",
        "fiabilite",
        "reliability"
    ];


    private static bool ContainsNormalizedWholeTerm(string normalizedText, string term)
        => Regex.IsMatch(
            normalizedText,
            $@"(?:^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:$|[^\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant);


    private static bool ContainsBroadComplianceQuestion(string normalizedQuery)
        => ContainsComplianceLanguage(normalizedQuery)
           && !HasSpecificReferenceLookup(normalizedQuery)
           && ContainsBroadScopeLanguage(normalizedQuery);


    private static bool ContainsBroadScopeLanguage(string normalizedQuery)
        => normalizedQuery.Contains("partout", StringComparison.Ordinal)
           || normalizedQuery.Contains("en general", StringComparison.Ordinal)
           || normalizedQuery.Contains("global", StringComparison.Ordinal)
           || normalizedQuery.Contains("globalement", StringComparison.Ordinal)
           || normalizedQuery.Contains("juste", StringComparison.Ordinal)
           || normalizedQuery.Contains("sans plus", StringComparison.Ordinal)
           || normalizedQuery.Contains("overall", StringComparison.Ordinal)
           || normalizedQuery.Contains("everywhere", StringComparison.Ordinal)
           || normalizedQuery.Contains("generally", StringComparison.Ordinal)
           || normalizedQuery.Contains("in general", StringComparison.Ordinal);


    internal static bool ContainsCustomerReplyLanguage(string normalizedQuery)
        => normalizedQuery.Contains("on lui repond", StringComparison.Ordinal)
           || normalizedQuery.Contains("tu repondrais", StringComparison.Ordinal)
           || normalizedQuery.Contains("reponse prudente", StringComparison.Ordinal)
           || normalizedQuery.Contains("qu est ce qu il faut lui demander", StringComparison.Ordinal)
           || normalizedQuery.Contains("qu'est ce qu'il faut lui demander", StringComparison.Ordinal);


    private static bool ContainsProjectAssessmentLanguage(string normalizedQuery)
        => normalizedQuery.Contains("projet", StringComparison.Ordinal)
           || normalizedQuery.Contains("notre systeme", StringComparison.Ordinal)
           || normalizedQuery.Contains("notre installation", StringComparison.Ordinal);


    private static bool ContainsSufficiencyQuestion(string normalizedQuery)
        => normalizedQuery.Contains("suffit a lui seul", StringComparison.Ordinal)
           || normalizedQuery.Contains("suffit a elle seule", StringComparison.Ordinal);


    internal static bool ContainsSimpleDocumentSelectionQuestion(string normalizedQuery)
        => normalizedQuery.Contains("quel document faut il citer", StringComparison.Ordinal)
           || normalizedQuery.Contains("quel document faut-il citer", StringComparison.Ordinal)
           || normalizedQuery.Contains("lequel pour parler", StringComparison.Ordinal);


    private static bool ContainsAmbiguousBareFragmentQuery(string normalizedQuery)
    {
        var trimmed = normalizedQuery.Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
            return false;

        if (ContainsComparisonOrSynthesisLanguage(trimmed)
            || ContainsDirectFactualAnswerQuestion(trimmed)
            || ContainsSufficiencyQuestion(trimmed)
            || ContainsSimpleDocumentSelectionQuestion(trimmed))
        {
            return false;
        }

        if (Regex.IsMatch(
                trimmed,
                @"\b(?:que|quel|quelle|quels|quelles|comment|pourquoi|quand|ou|où|donne|donnes|donnez|montre|montrez|explique|expliquez|cherche|recherche|trouve|liste|resume|resumer|peux|pouvez|should|what|which|how|why|when|where|give|show|explain|find|search|list|summari[sz]e)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 2 or > 7)
            return false;

        var startsWithOpenArticle = Regex.IsMatch(
            trimmed,
            @"^(?:un|une|des|du|de la|de l|a|an|some|una|unos|unas|um|uma|ein|eine)\b",
            RegexOptions.CultureInvariant);
        if (!startsWithOpenArticle)
            return false;

        if (LooksLikeShortAudienceConstrainedGenericFragment(trimmed))
            return true;

        return Regex.IsMatch(
            trimmed,
            @"\b(?:classique|classic|traditionnel|traditionnelle|traditional|standard\p{L}*|estandar|padrao|typique|typical|courant|courante|common|generique|generic|habituel|habituelle|usual|simple|basique|basic)\b",
            RegexOptions.CultureInvariant);
    }


    private static bool ContainsComparisonOrSynthesisLanguage(string normalizedQuery)
        => ContainsAny(
            normalizedQuery,
            "compare",
            "comparer",
            "comparaison",
            "comparatif",
            "difference",
            "differences",
            "synthese",
            "distingue",
            "distinguer",
            "vs",
            "versus",
            "comparison",
            "comparing",
            "compare",
            "vergleich",
            "vergleiche",
            "comparar");


    private static bool ContainsRiskRecommendationQuestion(string normalizedQuery)
    {
        if (ContainsDirectFactualAnswerQuestion(normalizedQuery))
            return false;

        if (normalizedQuery.Contains("compare", StringComparison.Ordinal)
            || normalizedQuery.Contains("comparer", StringComparison.Ordinal)
            || normalizedQuery.Contains("difference", StringComparison.Ordinal)
            || normalizedQuery.Contains("distingue", StringComparison.Ordinal)
            || normalizedQuery.Contains("distinguer", StringComparison.Ordinal)
            || normalizedQuery.Contains("expliquer le fonctionnement", StringComparison.Ordinal)
            || normalizedQuery.Contains("explique simplement", StringComparison.Ordinal)
            || normalizedQuery.Contains("quand on utiliserait", StringComparison.Ordinal)
            || normalizedQuery.Contains("quand utiliser", StringComparison.Ordinal))
        {
            return false;
        }

        if (!ContainsCustomerOrAdviceLanguage(normalizedQuery))
        {
            return false;
        }

        return ContainsOperationalAdviceLanguage(normalizedQuery)
            || ContainsHighImpactClaimLanguage(normalizedQuery)
            || ContainsBroadComplianceQuestion(normalizedQuery);
    }


    private static bool ContainsCustomerOrAdviceLanguage(string normalizedQuery)
        => normalizedQuery.Contains("client", StringComparison.Ordinal)
           || normalizedQuery.Contains("on peut", StringComparison.Ordinal)
           || normalizedQuery.Contains("peut dire", StringComparison.Ordinal)
           || normalizedQuery.Contains("dit quoi", StringComparison.Ordinal)
           || normalizedQuery.Contains("demande", StringComparison.Ordinal)
           || normalizedQuery.Contains("repondre", StringComparison.Ordinal)
           || normalizedQuery.Contains("repondrais", StringComparison.Ordinal)
           || normalizedQuery.Contains("recommend", StringComparison.Ordinal)
           || normalizedQuery.Contains("recommendation", StringComparison.Ordinal);


    private static bool ContainsOperationalAdviceLanguage(string normalizedQuery)
        => ContainsCustomerReplyLanguage(normalizedQuery)
           || ContainsOperationalInstallationAdvice(normalizedQuery)
           || ContainsOperationalIntegrationSelection(normalizedQuery)
           || ContainsOperationalTargetAdvice(normalizedQuery)
           || ContainsOperationalUseAdvice(normalizedQuery);


    private static bool ContainsOperationalInstallationAdvice(string normalizedQuery)
        => (normalizedQuery.Contains("installer", StringComparison.Ordinal)
            || normalizedQuery.Contains("install", StringComparison.Ordinal))
           && (ContainsHighImpactClaimLanguage(normalizedQuery)
               || ContainsHighImpactSafetyContextLanguage(normalizedQuery));


    private static bool ContainsHighImpactSafetyContextLanguage(string normalizedQuery)
    {
        // General safety policy: operational guidance in high-impact industrial safety contexts stays qualified.
        var foldedQuery = FoldDiacritics(normalizedQuery);
        return HighImpactSafetyContextTerms.Any(term => foldedQuery.Contains(term, StringComparison.Ordinal));
    }


    private static readonly string[] HighImpactSafetyContextTerms =
    [
        "zone potentiellement explosive",
        "zone explosive",
        "atmosphere explosive",
        "zone dangereuse",
        "zone a risque",
        "environnement dangereux",
        "environnement a risque",
        "securite industrielle",
        "risque d explosion",
        "risques industriels",
        "prevention explosion",
        "hazardous area",
        "hazardous location",
        "hazardous environment",
        "dangerous area",
        "explosive atmosphere",
        "potentially explosive atmosphere",
        "explosion hazard",
        "explosion risk",
        "explosion protection",
        "explosion prevention",
        "industrial safety",
        "safety critical",
        "seguridad industrial",
        "area peligrosa",
        "zona peligrosa",
        "atmosfera explosiva",
        "riesgo de explosion",
        "seguranca industrial",
        "area perigosa",
        "zona perigosa",
        "risco de explosao",
        "industrielle sicherheit",
        "explosionsgefahrdeter bereich",
        "explosionsgefaehrdeter bereich",
        "gefahrlicher bereich",
        "explosionsgefahr",
        "sicurezza industriale",
        "area pericolosa",
        "atmosfera esplosiva",
        "rischio di esplosione"
    ];


    private static bool ContainsOperationalIntegrationSelection(string normalizedQuery)
        => (normalizedQuery.Contains("relier", StringComparison.Ordinal)
            || normalizedQuery.Contains("raccorder", StringComparison.Ordinal)
            || normalizedQuery.Contains("connecter", StringComparison.Ordinal)
            || normalizedQuery.Contains("integrer", StringComparison.Ordinal)
            || normalizedQuery.Contains("connect", StringComparison.Ordinal)
            || normalizedQuery.Contains("integrate", StringComparison.Ordinal))
           && (normalizedQuery.Contains("lequel", StringComparison.Ordinal)
               || normalizedQuery.Contains("quel document", StringComparison.Ordinal)
               || normalizedQuery.Contains("documents aide", StringComparison.Ordinal)
               || normalizedQuery.Contains("aide le plus", StringComparison.Ordinal));


    private static bool ContainsOperationalTargetAdvice(string normalizedQuery)
        => (normalizedQuery.Contains("viser", StringComparison.Ordinal)
            || normalizedQuery.Contains("target", StringComparison.Ordinal))
           && ContainsHighImpactClaimLanguage(normalizedQuery);


    private static bool ContainsOperationalUseAdvice(string normalizedQuery)
        => (normalizedQuery.Contains("utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("use ", StringComparison.Ordinal))
           && (ContainsCustomerReplyLanguage(normalizedQuery)
               || normalizedQuery.Contains("repondre quoi", StringComparison.Ordinal)
               || normalizedQuery.Contains("document en dit quelque chose", StringComparison.Ordinal)
               || normalizedQuery.Contains("point de vigilance", StringComparison.Ordinal)
               || (ContainsHighImpactClaimLanguage(normalizedQuery)
                   && (normalizedQuery.Contains(" pour ", StringComparison.Ordinal)
                       || normalizedQuery.Contains(" comme ", StringComparison.Ordinal))));


    private static bool ContainsDirectFactualAnswerQuestion(string normalizedQuery)
    {
        return normalizedQuery.Contains("c est autorise", StringComparison.Ordinal)
            || normalizedQuery.Contains("est ce autorise", StringComparison.Ordinal)
            || normalizedQuery.Contains("est-ce autorise", StringComparison.Ordinal)
            || normalizedQuery.Contains("est ce qu on peut utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("est-ce qu on peut utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("est ce qu'on peut utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("se connecte", StringComparison.Ordinal)
            || normalizedQuery.Contains("couvre aussi", StringComparison.Ordinal)
            || normalizedQuery.Contains("couvre ", StringComparison.Ordinal)
            || normalizedQuery.Contains("toutes les versions", StringComparison.Ordinal)
            || normalizedQuery.Contains("tous les modeles", StringComparison.Ordinal)
            || normalizedQuery.Contains("all versions", StringComparison.Ordinal)
            || normalizedQuery.Contains("all models", StringComparison.Ordinal);
    }


    private static bool ContainsQuickCustomerReplySelectionQuestion(string normalizedQuery)
        => normalizedQuery.Contains("repondre vite au client", StringComparison.Ordinal)
           || normalizedQuery.Contains("ouvrir en premier", StringComparison.Ordinal)
           || normalizedQuery.Contains("ouvre en premier", StringComparison.Ordinal)
           || normalizedQuery.Contains("lequel des deux docs", StringComparison.Ordinal)
           || (normalizedQuery.Contains("par lequel", StringComparison.Ordinal)
               && normalizedQuery.Contains("client", StringComparison.Ordinal));


    private static string ResolveGuidanceLanguage(string query)
    {
        var primary = DocumentLanguageResolver.PrimarySubtag(DocumentLanguageResolver.DetectDominantLanguage(query));
        if (primary is "en" or "es" or "pt" or "de" or "it")
            return primary;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (ContainsAny(normalized, " estandar ", " una ", " unos ", " unas "))
            return "es";
        if (ContainsAny(normalized, " padrao ", " um ", " uma ", " voce "))
            return "pt";
        if (ContainsAny(normalized, " eine ", " ein ", " welche ", " welches ", " standardkontrolle "))
            return "de";
        if (ContainsAny(normalized, " controllo ", " quale ", " perimetro ", " documento "))
            return "it";
        if (ContainsAny(normalized, " a standard ", " an ", " which ", " what ", " scope "))
            return "en";

        return "fr";
    }


    private static string BuildNoRelevantSourceNote(string guidanceLanguage)
        => guidanceLanguage switch
        {
            "en" => "No sufficiently relevant source was retrieved for this query.",
            "es" => "No se ha recuperado ninguna fuente suficientemente pertinente para esta consulta.",
            "pt" => "Nenhuma fonte suficientemente relevante foi recuperada para esta pergunta.",
            "de" => "Fuer diese Anfrage wurde keine ausreichend relevante Quelle gefunden.",
            "it" => "Non e stata recuperata nessuna fonte sufficientemente pertinente per questa richiesta.",
            _ => "Aucune source suffisamment pertinente n'a ete retrouvee pour cette question."
        };


    private static string BuildClarifyingQuestion(string normalizedQuery, MatchedRetrievalContext matchedContext, string reason, string guidanceLanguage)
    {
        if (string.Equals(reason, "certification_requires_precise_scope", StringComparison.Ordinal))
        {
            return guidanceLanguage switch
            {
                "en" => "Which certification or qualification do you mean, for which exact scope, which item, and which usage conditions?",
                "es" => "A que certificacion o cualificacion te refieres, con que alcance exacto, que elemento y que condiciones de uso?",
                "pt" => "A que certificacao ou qualificacao se refere, com que escopo exato, que elemento e que condicoes de uso?",
                "de" => "Welche Zertifizierung oder Qualifikation meinst du, fuer welchen genauen Umfang, welches Element und welche Nutzungsbedingungen?",
                "it" => "A quale certificazione o qualificazione ti riferisci, con quale perimetro esatto, quale elemento e quali condizioni d'uso?",
                _ => "Tu parles de quelle certification ou qualification, pour quel perimetre exact, quel element concerne et quelles conditions d'utilisation ?"
            };
        }

        if (string.Equals(reason, "broad_compliance_requires_scope", StringComparison.Ordinal))
        {
            return guidanceLanguage switch
            {
                "en" => "Which exact requirement are you targeting, for which scope, equipment or document, and under which installation or usage conditions?",
                "es" => "Que requisito exacto buscas, con que alcance, para que equipo o documento, y con que condiciones de instalacion o uso?",
                "pt" => "Qual requisito exato voce quer tratar, com que escopo, para qual equipamento ou documento, e com quais condicoes de instalacao ou uso?",
                "de" => "Welche genaue Anforderung meinst du, fuer welchen Umfang, welches Geraet oder Dokument und unter welchen Installations- oder Nutzungsbedingungen?",
                "it" => "Quale requisito preciso intendi, con quale perimetro, per quale apparecchiatura o documento e con quali condizioni di installazione o uso?",
                _ => "Tu vises quelle exigence precise, sur quel perimetre, pour quel equipement ou document, et avec quelles conditions d'installation ou d'utilisation ?"
            };
        }

        if (string.Equals(reason, "ambiguous_bare_fragment_requires_scope", StringComparison.Ordinal))
        {
            return guidanceLanguage switch
            {
                "en" => "Which exact item, document or scope should I use so I do not choose one source arbitrarily?",
                "es" => "Que elemento, documento o alcance exacto debo usar para no escoger una fuente de forma arbitraria?",
                "pt" => "Qual elemento, documento ou escopo exato devo usar para nao escolher uma fonte arbitrariamente?",
                "de" => "Welches genaue Element, Dokument oder welchen Umfang soll ich verwenden, damit ich keine Quelle willkuerlich auswaehle?",
                "it" => "Quale elemento, documento o perimetro esatto devo usare per non scegliere una fonte in modo arbitrario?",
                _ => "Quel element, document ou perimetre exact dois-je utiliser pour ne pas choisir une source arbitrairement ?"
            };
        }

        return guidanceLanguage switch
        {
            "en" => "Which exact requirement do you mean, for which scope, which item or document, and under which usage conditions?",
            "es" => "A que requisito exacto te refieres, con que alcance, para que elemento o documento, y con que condiciones de uso?",
            "pt" => "A qual requisito exato voce se refere, com que escopo, para qual elemento ou documento, e com quais condicoes de uso?",
            "de" => "Welche genaue Anforderung meinst du, fuer welchen Umfang, welches Element oder Dokument und unter welchen Nutzungsbedingungen?",
            "it" => "A quale requisito esatto ti riferisci, con quale perimetro, quale elemento o documento e quali condizioni d'uso?",
            _ => "Tu parles de quelle exigence exactement, sur quel perimetre, pour quel element ou document, et avec quelles conditions d'utilisation ?"
        };
    }


    private static string BuildHighImpactSafetyQualificationNote(string normalizedQuery, MatchedRetrievalContext matchedContext, bool simpleDocumentSelection, string guidanceLanguage)
    {
        var hintLabel = FormatHintLabel(matchedContext.DocHints);

        if (ContainsQuickCustomerReplySelectionQuestion(normalizedQuery) || simpleDocumentSelection)
        {
            return guidanceLanguage switch
            {
                "en" => $"The retrieved documents{hintLabel} can help choose an initial source, but the scope, document version, affected items and context still need checking before making it a final answer.",
                "es" => $"Los documentos recuperados{hintLabel} pueden ayudar a escoger una primera fuente, pero aun hay que verificar el alcance, la version del documento, los elementos afectados y el contexto antes de dar una respuesta definitiva.",
                "pt" => $"Os documentos recuperados{hintLabel} podem ajudar a escolher uma primeira fonte, mas ainda e preciso verificar o escopo, a versao do documento, os elementos envolvidos e o contexto antes de dar uma resposta definitiva.",
                "de" => $"Die gefundenen Dokumente{hintLabel} koennen bei der ersten Quellenauswahl helfen, aber Umfang, Dokumentversion, betroffene Elemente und Kontext muessen vor einer endgueltigen Antwort noch geprueft werden.",
                "it" => $"I documenti recuperati{hintLabel} possono aiutare a scegliere una prima fonte, ma bisogna ancora verificare perimetro, versione del documento, elementi interessati e contesto prima di dare una risposta definitiva.",
                _ => $"Les documents retrouves{hintLabel} peuvent aider a choisir une premiere source, mais il faut encore verifier le perimetre, la version du document, les elements concernes et le contexte avant d'en faire une reponse definitive."
            };
        }

        return guidanceLanguage switch
        {
            "en" => $"The retrieved documents{hintLabel} can inform the topic, but the scope, affected items, usage conditions and project context still need framing before asserting compliance, an obligation or a final recommendation.",
            "es" => $"Los documentos recuperados{hintLabel} pueden aclarar el tema, pero aun hay que acotar el alcance, los elementos afectados, las condiciones de uso y el contexto del proyecto antes de afirmar conformidad, obligacion o recomendacion definitiva.",
            "pt" => $"Os documentos recuperados{hintLabel} podem esclarecer o tema, mas ainda e preciso enquadrar o escopo, os elementos envolvidos, as condicoes de uso e o contexto do projeto antes de afirmar conformidade, obrigacao ou recomendacao definitiva.",
            "de" => $"Die gefundenen Dokumente{hintLabel} koennen das Thema einordnen, aber Umfang, betroffene Elemente, Nutzungsbedingungen und Projektkontext muessen noch geklaert werden, bevor Konformitaet, Pflicht oder endgueltige Empfehlung behauptet wird.",
            "it" => $"I documenti recuperati{hintLabel} possono chiarire il tema, ma bisogna ancora definire perimetro, elementi interessati, condizioni d'uso e contesto di progetto prima di affermare conformita, obbligo o raccomandazione definitiva.",
            _ => $"Les documents retrouves{hintLabel} peuvent eclairer le sujet, mais il faut encore cadrer le perimetre, les elements concernes, les conditions d'utilisation et le contexte projet avant d'affirmer une conformite, une obligation ou une recommandation definitive."
        };
    }


    private static string BuildSourceQualityQualificationNote(MatchedRetrievalContext matchedContext, string guidanceLanguage)
    {
        var hintLabel = FormatHintLabel(matchedContext.DocHints);
        return guidanceLanguage switch
        {
            "en" => $"The retrieved evidence{hintLabel} appears usable, but at least one selected source has weak extraction or OCR quality. Answer from the cited passages, and explicitly mention uncertainty if a value, wording or page detail looks ambiguous.",
            "es" => $"La evidencia recuperada{hintLabel} parece utilizable, pero al menos una fuente seleccionada tiene calidad de extraccion u OCR debil. Responde a partir de los pasajes citados y menciona la incertidumbre si un valor, una formulacion o un detalle de pagina parece ambiguo.",
            "pt" => $"As evidencias recuperadas{hintLabel} parecem utilizaveis, mas pelo menos uma fonte selecionada tem qualidade fraca de extracao ou OCR. Responda a partir dos trechos citados e mencione a incerteza se um valor, uma formulacao ou um detalhe de pagina parecer ambiguo.",
            "de" => $"Die gefundenen Belege{hintLabel} wirken nutzbar, aber mindestens eine ausgewaehlte Quelle hat schwache Extraktions- oder OCR-Qualitaet. Antworte aus den zitierten Passagen und nenne Unsicherheit, wenn ein Wert, eine Formulierung oder ein Seitendetail mehrdeutig wirkt.",
            "it" => $"Le evidenze recuperate{hintLabel} sembrano utilizzabili, ma almeno una fonte selezionata ha qualita di estrazione o OCR debole. Rispondi dai passaggi citati e segnala l'incertezza se un valore, una formulazione o un dettaglio di pagina sembra ambiguo.",
            _ => $"Les elements retrouves{hintLabel} semblent exploitables, mais au moins une source selectionnee a une qualite d'extraction ou OCR faible. Reponds depuis les passages cites et signale l'incertitude si une valeur, une formulation ou un detail de page semble ambigu."
        };
    }


    private static string DetermineResponseShape(string normalizedQuery, string behavior)
    {
        if (string.Equals(behavior, "ask_clarification", StringComparison.Ordinal))
            return "clarify";

        if (normalizedQuery.Contains("compare", StringComparison.Ordinal)
            || normalizedQuery.Contains("difference", StringComparison.Ordinal)
            || normalizedQuery.Contains("comparer", StringComparison.Ordinal))
            return "comparison";

        if (normalizedQuery.Contains("montre", StringComparison.Ordinal)
            || normalizedQuery.Contains("passage", StringComparison.Ordinal)
            || normalizedQuery.Contains("ou dans le document", StringComparison.Ordinal))
            return "locate_passage";

        if (normalizedQuery.Contains("quel document", StringComparison.Ordinal)
            || normalizedQuery.Contains("manuel", StringComparison.Ordinal)
            || normalizedQuery.Contains("ouvrir", StringComparison.Ordinal)
            || normalizedQuery.Contains("commencer", StringComparison.Ordinal))
            return "locate_document";

        if (normalizedQuery.Contains("resumer", StringComparison.Ordinal)
            || normalizedQuery.Contains("resume", StringComparison.Ordinal)
            || normalizedQuery.Contains("reponse courte", StringComparison.Ordinal)
            || normalizedQuery.Contains("expliquer simplement", StringComparison.Ordinal))
            return "summary";

        if (string.Equals(behavior, "answer_with_caveat", StringComparison.Ordinal))
            return "qualified_answer";

        return "direct_answer";
    }


    private static void CollectDocumentHints(string? rawText, HashSet<string> hints)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var stem = ExtractDocumentStem(rawText);
        if (string.IsNullOrWhiteSpace(stem))
            return;

        var localHints = new HashSet<string>(StringComparer.Ordinal);
        string? fallbackAlpha = null;
        string? previousAlpha = null;

        foreach (var part in stem.Split([' ', '-', '_', '/', '\\', '.', ',', ';', ':', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            var compact = CompactDocumentHintToken(part);
            if (string.IsNullOrEmpty(compact))
            {
                previousAlpha = null;
                continue;
            }

            if (compact.All(char.IsDigit))
            {
                if (!string.IsNullOrWhiteSpace(previousAlpha) && compact.Length is >= 2 and <= 6)
                    localHints.Add($"{previousAlpha}{compact}");

                if (compact.Length >= 4)
                    localHints.Add(compact);

                previousAlpha = null;
                continue;
            }

            if (compact.Any(char.IsDigit))
            {
                if (compact.Length is >= 4 and <= 16)
                    localHints.Add(compact);

                previousAlpha = null;
                continue;
            }

            if (IsMeaningfulDocumentHint(compact))
            {
                fallbackAlpha ??= compact;
                previousAlpha = compact.Length <= 5 ? compact : null;
                continue;
            }

            if (IsShortReferencePrefix(compact))
            {
                previousAlpha = compact;
                continue;
            }

            previousAlpha = null;
        }

        if (localHints.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(fallbackAlpha))
                hints.Add(fallbackAlpha);
            return;
        }

        foreach (var hint in localHints)
            hints.Add(hint);
    }


    private static string ExtractDocumentStem(string rawText)
    {
        var normalized = rawText.Replace('\\', '/');
        var tail = normalized[(normalized.LastIndexOf('/') + 1)..];
        var dotIndex = tail.LastIndexOf('.');
        return dotIndex > 0 ? tail[..dotIndex] : tail;
    }


    private static string CompactDocumentHintToken(string token)
    {
        Span<char> buffer = stackalloc char[token.Length];
        var length = 0;

        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToUpperInvariant(ch);
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }


    private static bool IsMeaningfulDocumentHint(string token)
        => token.Length >= 4
           && token.All(char.IsLetter)
           && !DocumentHintStopwords.Contains(token);


    private static bool IsShortReferencePrefix(string token)
        => token.Length is >= 2 and <= 5
           && token.All(char.IsLetter)
           && !DocumentHintStopwords.Contains(token);


    private static string FormatHintLabel(IReadOnlyList<string> hints)
        => hints.Count switch
        {
            0 => string.Empty,
            1 => $" ({hints[0]})",
            2 => $" ({hints[0]} + {hints[1]})",
            _ => $" ({hints[0]} + {hints[1]} + autres sources)"
        };


    private static bool HasSpecificReferenceLookup(string normalizedQuery)
    {
        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(normalizedQuery);
        return ExactMatchEntryExtractor.ExtractLookupTerms(normalizedQuery)
            .Any(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal));
    }


    internal static string NormalizeQueryForGuidance(string query)
        => $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";


    internal static string RepairLikelyMojibakeText(string text)
    {
        text = RepairSinglePassMojibakeText(text);
        if (string.IsNullOrEmpty(text)
            || (!text.Contains('Ã', StringComparison.Ordinal)
                && !text.Contains('Â', StringComparison.Ordinal)
                && !text.Contains('Å', StringComparison.Ordinal)
                && !text.Contains("â€", StringComparison.Ordinal)))
        {
            return text;
        }

        var value = text;
        for (var i = 0; i < 2; i++)
        {
            var repaired = value
                .Replace("Ã©", "é", StringComparison.Ordinal)
                .Replace("Ã¨", "è", StringComparison.Ordinal)
                .Replace("Ãª", "ê", StringComparison.Ordinal)
                .Replace("Ã«", "ë", StringComparison.Ordinal)
                .Replace("Ã ", "à", StringComparison.Ordinal)
                .Replace("Ã¢", "â", StringComparison.Ordinal)
                .Replace("Ã¤", "ä", StringComparison.Ordinal)
                .Replace("Ã§", "ç", StringComparison.Ordinal)
                .Replace("Ã®", "î", StringComparison.Ordinal)
                .Replace("Ã¯", "ï", StringComparison.Ordinal)
                .Replace("Ã´", "ô", StringComparison.Ordinal)
                .Replace("Ã¶", "ö", StringComparison.Ordinal)
                .Replace("Ã¹", "ù", StringComparison.Ordinal)
                .Replace("Ã»", "û", StringComparison.Ordinal)
                .Replace("Ã¼", "ü", StringComparison.Ordinal)
                .Replace("Ã‰", "É", StringComparison.Ordinal)
                .Replace("Ãˆ", "È", StringComparison.Ordinal)
                .Replace("ÃŠ", "Ê", StringComparison.Ordinal)
                .Replace("Ã€", "À", StringComparison.Ordinal)
                .Replace("Ã‡", "Ç", StringComparison.Ordinal)
                .Replace("Å“", "œ", StringComparison.Ordinal)
                .Replace("Å’", "Œ", StringComparison.Ordinal)
                .Replace("â€™", "'", StringComparison.Ordinal)
                .Replace("â€˜", "'", StringComparison.Ordinal)
                .Replace("â€œ", "\"", StringComparison.Ordinal)
                .Replace("â€", "\"", StringComparison.Ordinal)
                .Replace("â€“", "-", StringComparison.Ordinal)
                .Replace("â€”", "-", StringComparison.Ordinal)
                .Replace("Â°", "°", StringComparison.Ordinal)
                .Replace("Â", " ", StringComparison.Ordinal);

            if (string.Equals(repaired, value, StringComparison.Ordinal))
                break;
            value = repaired;
        }

        value = RepairSinglePassMojibakeText(value);
        return Regex.Replace(value, @"\s+", " ").Trim();
    }


    private static string RepairSinglePassMojibakeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("\u00C3\u0192\u00C2\u00A9", "\u00E9", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00A8", "\u00E8", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00AA", "\u00EA", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00AB", "\u00EB", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00A0", "\u00E0", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00A2", "\u00E2", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00A4", "\u00E4", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00A7", "\u00E7", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00AE", "\u00EE", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00AF", "\u00EF", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00B4", "\u00F4", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00B6", "\u00F6", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00B9", "\u00F9", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00BB", "\u00FB", StringComparison.Ordinal)
            .Replace("\u00C3\u0192\u00C2\u00BC", "\u00FC", StringComparison.Ordinal)
            .Replace("\u00C3\u00A9", "\u00E9", StringComparison.Ordinal)
            .Replace("\u00C3\u00A8", "\u00E8", StringComparison.Ordinal)
            .Replace("\u00C3\u00AA", "\u00EA", StringComparison.Ordinal)
            .Replace("\u00C3\u00AB", "\u00EB", StringComparison.Ordinal)
            .Replace("\u00C3\u00A0", "\u00E0", StringComparison.Ordinal)
            .Replace("\u00C3\u00A2", "\u00E2", StringComparison.Ordinal)
            .Replace("\u00C3\u00A4", "\u00E4", StringComparison.Ordinal)
            .Replace("\u00C3\u00A7", "\u00E7", StringComparison.Ordinal)
            .Replace("\u00C3\u00AE", "\u00EE", StringComparison.Ordinal)
            .Replace("\u00C3\u00AF", "\u00EF", StringComparison.Ordinal)
            .Replace("\u00C3\u00B4", "\u00F4", StringComparison.Ordinal)
            .Replace("\u00C3\u00B6", "\u00F6", StringComparison.Ordinal)
            .Replace("\u00C3\u00B9", "\u00F9", StringComparison.Ordinal)
            .Replace("\u00C3\u00BB", "\u00FB", StringComparison.Ordinal)
            .Replace("\u00C3\u00BC", "\u00FC", StringComparison.Ordinal)
            .Replace("\u00C3\u0089", "\u00C9", StringComparison.Ordinal)
            .Replace("\u00C3\u0088", "\u00C8", StringComparison.Ordinal)
            .Replace("\u00C3\u008A", "\u00CA", StringComparison.Ordinal)
            .Replace("\u00C3\u0080", "\u00C0", StringComparison.Ordinal)
            .Replace("\u00C3\u0087", "\u00C7", StringComparison.Ordinal)
            .Replace("\u00C5\u0093", "\u0153", StringComparison.Ordinal)
            .Replace("\u00C5\u0092", "\u0152", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u2122", "'", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u02DC", "'", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u0153", "\"", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u009D", "\"", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u201C", "-", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u201D", "-", StringComparison.Ordinal)
            .Replace("\u00C2\u00B0", "\u00B0", StringComparison.Ordinal)
            .Replace("\u00C2", " ", StringComparison.Ordinal);
    }


    internal static string NormalizeQuery(string s)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0)
            return s;

        var sb = new StringBuilder(s.Length);
        var inWhitespace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }

        return sb.ToString();
    }

}
