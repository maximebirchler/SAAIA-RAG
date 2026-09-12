using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private IReadOnlyList<AdvancedAnalysisSearchRequest> ParsePlan(
        string raw,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<string> availableCategories)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            if (!document.RootElement.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException();
            }

            var maximumQueries = ResolveMaximumPlanQueries(request);
            var result = new List<AdvancedAnalysisSearchRequest>();
            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowedCategories = BuildAllowedPlannerCategories(
                request,
                availableCategories);
            foreach (var item in queries.EnumerateArray())
            {
                if (result.Count >= maximumQueries
                    || item.ValueKind != JsonValueKind.Object)
                {
                    break;
                }
                var query = NormalizeCorpusSearchQuery(
                    ReadString(item, "query"));
                if (string.IsNullOrWhiteSpace(query)
                    || query.Length > 8_000
                    || !dedupe.Add(query))
                {
                    continue;
                }
                var category = ReadString(item, "category");
                if (string.IsNullOrWhiteSpace(category)
                    || !allowedCategories.Contains(category))
                {
                    category = string.Empty;
                }
                var topK = item.TryGetProperty("topK", out var topKValue)
                           && topKValue.TryGetInt32(out var parsedTopK)
                    ? Math.Clamp(parsedTopK, 1, 60)
                    : 20;
                result.Add(new AdvancedAnalysisSearchRequest(
                    query,
                    string.IsNullOrWhiteSpace(category) ? null : category,
                    topK,
                    MaxPerDocument: request.Handoff.Load.StructuredLayout
                        ? 8
                        : null,
                    MaxPerPage: request.Handoff.Load.StructuredLayout
                        ? 2
                        : null));
            }

            if (result.Count == 0)
            {
                result.Add(new AdvancedAnalysisSearchRequest(
                    request.Handoff.RequestText,
                    Category: null,
                    TopK: Math.Clamp(
                        Math.Max(12, request.Handoff.Load.AtomicEvidenceCount),
                        1,
                        60)));
            }
            return result;
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_planner_protocol_invalid");
        }
    }

    private IReadOnlyList<AdvancedAnalysisSearchRequest> ParseResearchReview(
        string raw,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<string> availableCategories)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            var decision = ReadString(document.RootElement, "decision")
                .Trim()
                .ToLowerInvariant();
            if (decision == "ready")
                return [];
            if (decision != "search_more")
                throw new JsonException();

            var queries = ParsePlan(raw, request, availableCategories);
            if (queries.Count == 0)
                throw new JsonException();
            return queries;
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_research_review_protocol_invalid");
        }
        catch (AdvancedAnalysisProviderException ex) when (
            ex.ErrorCode == "advanced_planner_protocol_invalid")
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_research_review_protocol_invalid");
        }
    }

    private static bool ShouldRunAdaptiveResearch(
        AdvancedAnalysisProviderRequest request)
    {
        var load = request.Handoff.Load;
        return load.StructuredLayout
               || load.AnswerUnitCount >= 4
               || load.AtomicEvidenceCount >= 4
               || load.PlanKind.Contains(
                   "comparison",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAttemptSynthesisRecovery(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<PromptEvidenceItem> evidence)
    {
        if (!request.Handoff.Load.StructuredLayout)
            return false;

        var load = request.Handoff.Load;
        var structuredUnits = checked(
            Math.Max(1, load.RowCount) * Math.Max(1, load.ColumnCount));
        var requiredUnits = Math.Max(load.AnswerUnitCount, structuredUnits);
        if (string.Equals(
                result.Outcome,
                "insufficient_documentation",
                StringComparison.Ordinal))
        {
            return evidence.Count >= requiredUnits;
        }

        return RequiresDistinctStructuredSelection(load)
               && string.Equals(
                   result.Outcome,
                   "answered",
                   StringComparison.Ordinal)
               && !AnalyzeDistinctSelectedItems(load, result, evidence).IsValid;
    }

    private static AdvancedAnalysisProviderResult
        EnforceDistinctStructuredSelection(
            AdvancedAnalysisProviderRequest request,
            AdvancedAnalysisProviderResult result,
            IReadOnlyList<PromptEvidenceItem> evidence,
            string? synthesisRecoveryErrorCode)
    {
        var validation = AnalyzeDistinctSelectedItems(
            request.Handoff.Load,
            result,
            evidence);
        if (!RequiresDistinctStructuredSelection(request.Handoff.Load)
            || !string.Equals(result.Outcome, "answered", StringComparison.Ordinal)
            || validation.IsValid)
        {
            return result;
        }

        var detail = BuildDistinctSelectionFailureDetail(
            request.Handoff.Language,
            validation,
            synthesisRecoveryErrorCode);
        var answer = request.Handoff.Language.Trim().ToLowerInvariant() switch
        {
            "en" => "I cannot provide the requested grid because the final selection could not be verified as distinct without invention. " + detail,
            "de" => "Ich kann die angeforderte Tabelle nicht liefern, weil die endgültige Auswahl nicht ohne Erfindungen als eindeutig verschieden geprüft werden konnte. " + detail,
            _ => "Je ne peux pas fournir la grille demandée, car la sélection finale n'a pas pu être vérifiée comme entièrement distincte sans invention. " + detail
        };
        return new AdvancedAnalysisProviderResult
        {
            Outcome = "insufficient_documentation",
            AnswerText = answer
        };
    }

    private static bool RequiresDistinctStructuredSelection(
        AdvancedAnalysisLoadDescriptor load)
        => load.StructuredLayout
           && load.AnswerUnitCount >= 4
           && (load.SelectionPolicy.Contains(
                   "distinct",
                   StringComparison.OrdinalIgnoreCase)
               || load.AtomicEvidenceMode.Contains(
                   "one_per",
                   StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> BuildAllowedPlannerCategories(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<string> availableCategories)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in availableCategories)
        {
            var normalized = (category ?? string.Empty).Trim();
            if (normalized.Length is > 0 and <= 2_000)
                allowed.Add(normalized);
        }
        foreach (var rawPath in request.Handoff.Load.CandidateScopePaths)
        {
            var normalized = (rawPath ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .Trim('/');
            if (normalized.Length == 0)
                continue;
            var separator = normalized.IndexOf('/');
            var firstSegment = separator < 0
                ? normalized
                : normalized[..separator];
            if (firstSegment.Length > 0
                && !firstSegment.Contains('.', StringComparison.Ordinal))
            {
                allowed.Add(firstSegment);
            }
        }
        return allowed;
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        OrderEvidenceForPrompt(
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> allEvidence,
            IReadOnlyList<IReadOnlyList<AdvancedAnalysisResolvedEvidence>> groups)
    {
        var ordered = new List<AdvancedAnalysisResolvedEvidence>(
            allEvidence.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maximumGroupSize = groups.Count == 0
            ? 0
            : groups.Max(static group => group.Count);
        for (var index = 0; index < maximumGroupSize; index++)
        {
            foreach (var group in groups)
            {
                if (index >= group.Count)
                    continue;
                var item = group[index];
                var evidenceId = item.Reference.EvidenceId?.Trim();
                if (!string.IsNullOrWhiteSpace(evidenceId)
                    && seen.Add(evidenceId))
                {
                    ordered.Add(item);
                }
            }
        }
        foreach (var item in allEvidence)
        {
            var evidenceId = item.Reference.EvidenceId?.Trim();
            if (!string.IsNullOrWhiteSpace(evidenceId)
                && seen.Add(evidenceId))
            {
                ordered.Add(item);
            }
        }
        return ordered;
    }

    internal static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        OrderEvidenceForQuery(
            string query,
            string? documentHint,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
    {
        var queryTokens = ExtractEvidenceRankingTokens(query, documentHint);
        if (queryTokens.Count == 0 || evidence.Count < 2)
            return evidence;
        return evidence
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Score = ScoreEvidenceText(item.Content, queryTokens)
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Item)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractEvidenceRankingTokens(
        string query,
        string? documentHint)
    {
        var documentTokens = Regex.Matches(
                documentHint ?? string.Empty,
                @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return Regex.Matches(query ?? string.Empty, @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .Where(static token => token.Length >= 3)
            .Where(static token => !EvidenceRankingStopwords.Contains(token))
            .Where(token => !documentTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int ScoreEvidenceText(
        string content,
        IReadOnlyList<string> queryTokens)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0;
        var tokens = Regex.Matches(
                content.ToLowerInvariant(),
                @"[\p{L}\p{N}]+")
            .Select(static match => match.Value)
            .ToArray();
        if (tokens.Length == 0)
            return 0;
        var frequencies = tokens
            .GroupBy(static token => token, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var matched = 0;
        var repeated = 0;
        foreach (var token in queryTokens)
        {
            if (!frequencies.TryGetValue(token, out var count))
                continue;
            matched++;
            repeated += Math.Min(count, 3);
        }
        return checked(matched * 100 + repeated * 10);
    }

    private static readonly HashSet<string> EvidenceRankingStopwords = new(
        [
            "avec", "dans", "pour", "sans", "sous", "entre", "depuis",
            "cela", "cette", "celui", "celle", "ceux", "elles", "leurs",
            "quel", "quelle", "quels", "quelles", "dont", "quoi", "être",
            "avoir", "faire", "dire", "exige", "exiger", "compare",
            "comparaison", "document", "norme", "standard", "from", "with",
            "into", "that", "this", "these", "those", "what", "which",
            "where", "when", "have", "does", "must", "documented"
        ],
        StringComparer.Ordinal);

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        FilterEvidenceToRequestedDocumentSet(
            AdvancedAnalysisProviderRequest request,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
    {
        var load = request.Handoff.Load;
        var isExplicitDocumentSet = load.BoundedNamedDocumentExtraction
                                    || !string.IsNullOrWhiteSpace(
                                        load.RequestedDocumentName)
                                    || (load.SelectionPolicy.Contains(
                                            "explicit",
                                            StringComparison.OrdinalIgnoreCase)
                                        && (load.AtomicEvidenceType.Contains(
                                                "compar",
                                                StringComparison.OrdinalIgnoreCase)
                                            || load.PlanKind.Contains(
                                                "compar",
                                                StringComparison.OrdinalIgnoreCase)));
        if (!isExplicitDocumentSet)
            return evidence;

        var requestedDocuments = GetRequestedDocumentIdentifiers(request)
            .Select(NormalizeDocumentIdentifier)
            .ToArray();
        if (requestedDocuments.Length == 0)
            return evidence;

        return evidence.Where(item =>
        {
            var fileName = NormalizeDocumentIdentifier(
                item.Reference.FileName
                ?? item.Reference.DocPath
                ?? string.Empty);
            return requestedDocuments.Any(requested =>
                fileName.Contains(requested, StringComparison.Ordinal)
                || requested.Contains(fileName, StringComparison.Ordinal));
        }).ToArray();
    }

    private IReadOnlyList<AdvancedAnalysisSearchRequest>
        AddRequiredDocumentQueries(
            AdvancedAnalysisProviderRequest request,
            IReadOnlyList<AdvancedAnalysisSearchRequest> plannedQueries)
    {
        var load = request.Handoff.Load;
        var documentScoped = load.BoundedNamedDocumentExtraction
                             || !string.IsNullOrWhiteSpace(
                                 load.RequestedDocumentName)
                             || load.SelectionPolicy.Contains(
                                 "explicit",
                                 StringComparison.OrdinalIgnoreCase);
        if (!documentScoped)
            return plannedQueries;

        var documents = GetRequestedDocumentIdentifiers(request);
        if (documents.Count == 0)
            return plannedQueries;

        var maximumQueries = ResolveMaximumPlanQueries(request);
        var result = new List<AdvancedAnalysisSearchRequest>(maximumQueries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var context = BuildDocumentQueryContext(
                request.Handoff.RequestText,
                document,
                documents);
            var query = NormalizeCorpusSearchQuery(
                string.Join(' ', document, context));
            if (query.Length == 0 || !seen.Add(query))
                continue;
            result.Add(new AdvancedAnalysisSearchRequest(
                query,
                Category: null,
                TopK: Math.Clamp(
                    Math.Max(20, load.AtomicEvidenceCount * 4),
                    1,
                    60),
                DocumentHint: document));
            if (result.Count >= maximumQueries)
                return result;
        }
        foreach (var planned in plannedQueries)
        {
            var documentHint = ResolvePlannedDocumentHint(
                planned.Query,
                documents);
            var scopedPlanned = documentHint is null
                ? planned
                : planned with { DocumentHint = documentHint };
            if (!seen.Add(scopedPlanned.Query))
                continue;
            result.Add(scopedPlanned);
            if (result.Count >= maximumQueries)
                break;
        }
        return result;
    }

    private static string? ResolvePlannedDocumentHint(
        string query,
        IReadOnlyList<string> documents)
    {
        if (documents.Count == 1)
            return documents[0];

        var normalizedQuery = NormalizeDocumentIdentifier(query);
        var matches = documents
            .Where(document => PlannedQueryIdentifiesDocument(
                normalizedQuery,
                query,
                document))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool PlannedQueryIdentifiesDocument(
        string normalizedQuery,
        string rawQuery,
        string document)
    {
        var normalizedDocument = NormalizeDocumentIdentifier(document);
        if (normalizedDocument.Length >= 6
            && normalizedQuery.Contains(
                normalizedDocument,
                StringComparison.Ordinal))
        {
            return true;
        }

        var queryTokens = Regex.Matches(rawQuery ?? string.Empty, @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var identifierTokens = Regex.Matches(
                document ?? string.Empty,
                @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identifierTokens.Length < 2)
            return false;
        var matched = identifierTokens.Count(queryTokens.Contains);
        return matched >= Math.Min(3, identifierTokens.Length);
    }

    private static string BuildDocumentQueryContext(
        string requestText,
        string document,
        IReadOnlyList<string> allDocuments)
    {
        var source = requestText ?? string.Empty;
        var start = source.IndexOf(document, StringComparison.OrdinalIgnoreCase);
        var segment = source;
        if (start >= 0)
        {
            var end = source.Length;
            foreach (var other in allDocuments)
            {
                if (string.Equals(
                        other,
                        document,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = source.IndexOf(
                    other,
                    start + document.Length,
                    StringComparison.OrdinalIgnoreCase);
                if (candidate >= 0 && candidate < end)
                    end = candidate;
            }
            segment = source[start..end];
        }
        foreach (var candidate in allDocuments)
        {
            segment = Regex.Replace(
                segment,
                Regex.Escape(candidate),
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return NormalizeCorpusSearchQuery(segment);
    }

    private static IReadOnlyList<string> GetRequestedDocumentIdentifiers(
        AdvancedAnalysisProviderRequest request)
    {
        var candidates = (string.IsNullOrWhiteSpace(
                request.Handoff.Load.RequestedDocumentName)
                ? Array.Empty<string>()
                : [request.Handoff.Load.RequestedDocumentName])
            .Concat(ExtractDocumentIdentifiers(request.Handoff.RequestText));
        var documents = new List<string>();
        foreach (var raw in candidates)
        {
            var value = raw.Trim();
            var normalized = NormalizeDocumentIdentifier(value);
            if (normalized.Length < 6)
                continue;
            if (documents.Any(existing =>
            {
                var existingNormalized = NormalizeDocumentIdentifier(existing);
                return normalized == existingNormalized
                       || normalized.EndsWith(
                           existingNormalized,
                           StringComparison.Ordinal)
                       || existingNormalized.EndsWith(
                           normalized,
                           StringComparison.Ordinal);
            }))
            {
                continue;
            }
            documents.Add(value);
        }
        return documents;
    }

    private static IEnumerable<string> ExtractDocumentIdentifiers(string text)
    {
        const string formalIdentifierPattern =
            @"\b(?:[A-Z]{2,8}[\s/]+){1,4}\d{2,6}(?:[-:/.]\d{1,6})*(?:\s+(?:19|20)\d{2})?\b";
        const string fileReferencePattern =
            @"(?:^|[\s""'(])(?<file>[\p{L}\p{N}_()+&.,'’\-]+(?:\s+[\p{L}\p{N}_()+&.,'’\-]+){0,12}\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv))(?=$|[\s""'),;])";
        var source = text ?? string.Empty;
        return Regex.Matches(
                source,
                formalIdentifierPattern,
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value.Trim())
            .Concat(Regex.Matches(
                    source,
                    fileReferencePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(static match => match.Groups["file"].Value.Trim()));
    }

    private static string NormalizeDocumentIdentifier(string value)
        => Regex.Replace(
            value ?? string.Empty,
            @"[^\p{L}\p{N}]",
            string.Empty,
            RegexOptions.CultureInvariant).ToLowerInvariant();

    private int ResolveMaximumPlanQueries(
        AdvancedAnalysisProviderRequest request)
    {
        var configured = Math.Clamp(_options.MaximumPlanQueries, 1, 32);
        var load = request.Handoff.Load;
        if (GetRequestedDocumentIdentifiers(request).Count >= 2)
            return Math.Min(configured, 4);
        if (load.BoundedNamedDocumentExtraction)
            return Math.Min(configured, 2);
        if (load.StructuredLayout && load.ColumnCount > 0)
        {
            return Math.Min(
                configured,
                Math.Clamp(checked(load.ColumnCount * 2), 2, 12));
        }
        if (load.PlanKind.Contains("comparison", StringComparison.OrdinalIgnoreCase))
            return Math.Min(configured, 4);
        return Math.Min(configured, 6);
    }

    private static string NormalizeCorpusSearchQuery(string raw)
    {
        var query = Regex.Replace(
            raw ?? string.Empty,
            @"(?i)\bsite:\S+",
            " ");
        query = Regex.Replace(query, @"(?i)\bhttps?://\S+", " ");
        query = Regex.Replace(query, @"(?i)\bwww\.\S+", " ");
        query = Regex.Replace(query, @"\s+", " ").Trim();
        return query.Trim(' ', ',', ';', ':', '-', '|');
    }
}
