using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IEnumerable<string> ExtractSourceBackedTreeFollowupTitles(ToolResults toolResults, string query)
    {
        var queryTerms = BuildSourceBackedTreeFollowupQueryTerms(query).ToArray();
        var rankedTitles = new List<(string Title, int Score, int Index)>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(2))
        {
            foreach (var rawLabel in ExtractTreeNavigationAnchorLabels(item.Result))
            {
                foreach (var title in ExpandTreeNavigationAnchorLabel(rawLabel))
                {
                    var cleaned = CleanNavigationRouteAnchorTitle(title);
                    if (!IsUsableSourceBackedOptionTitle(cleaned)
                        || LooksLikeNavigationIndexHeadingTitle(cleaned)
                        || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
                        || !emitted.Add(cleaned))
                    {
                        continue;
                    }

                    var score = ComputeTreeNavigationAnchorFollowupScore(cleaned, rawLabel, queryTerms);
                    if (score <= 0 && !LooksLikeDocumentTreeNavigationAnchor(rawLabel, cleaned))
                        continue;

                    rankedTitles.Add((cleaned, score, index++));
                }
            }
        }

        return rankedTitles
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Title)
            .Take(18)
            .ToArray();
    }

    private static IEnumerable<string> ExtractSourceBackedDocumentNavigationFollowupTitles(ToolResults toolResults, string query)
    {
        var queryTerms = BuildSourceBackedTreeFollowupQueryTerms(query).ToArray();
        var rankedTitles = new List<(string Title, int Score, int Index)>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var item in EnumerateRecentSourceBackedNavigationItems(toolResults, query))
        {
            foreach (var candidate in ExtractDocumentNavigationFollowupLabels(item.Result))
            {
                foreach (var title in ExpandTreeNavigationAnchorLabel(candidate.Label))
                {
                    var cleaned = CleanNavigationRouteAnchorTitle(title);
                    if (!IsUsableSourceBackedOptionTitle(cleaned)
                        || LooksLikeNavigationIndexHeadingTitle(cleaned)
                        || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
                        || LooksLikeWeakSourceBackedOptionTitle(cleaned)
                        || (UsesSourceBackedPlanningCoverage(query)
                            && LooksLikeSubjectlessReferenceNavigationFollowupLabel(candidate, cleaned, queryTerms))
                        || !emitted.Add(cleaned))
                    {
                        continue;
                    }

                    var score = ComputeTreeNavigationAnchorFollowupScore(cleaned, candidate.RawLabel, queryTerms)
                                + candidate.ScoreHint;
                    rankedTitles.Add((cleaned, score, index++));
                }
            }
        }

        return rankedTitles
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Title)
            .Take(24)
            .ToArray();
    }

    private static IEnumerable<string> ExtractSourceBackedSummaryFollowupTitles(ToolResults toolResults, string query)
    {
        var queryTerms = BuildSourceBackedTreeFollowupQueryTerms(query).ToArray();
        var guardSubjectlessReferenceAnchors = UsesSourceBackedPlanningCoverage(query);
        return ExtractSummarySearchFollowupLabels(toolResults, query)
            .SelectMany(candidate => ExpandTreeNavigationAnchorLabel(candidate.Label)
                .Select(CleanNavigationRouteAnchorTitle)
                .Where(IsUsableSourceBackedOptionTitle)
                .Where(static title => !LooksLikeNavigationIndexHeadingTitle(title))
                .Where(static title => !LooksLikeNoisyStructuredPlanningCandidateTitle(title))
                .Where(static title => !LooksLikeWeakSourceBackedOptionTitle(title))
                .Where(title => !guardSubjectlessReferenceAnchors
                                || !LooksLikeSubjectlessReferenceNavigationFollowupLabel(candidate, title, queryTerms)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
    }

    private static IEnumerable<SourceBackedDocumentNavigationFollowupLabel> ExtractSummarySearchFollowupLabels(
        ToolResults toolResults,
        string query)
    {
        var queryTerms = BuildSourceBackedTreeFollowupQueryTerms(query).ToArray();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "summary.search" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(ResolveSourceBackedSummaryFollowupResultWindow(query)))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var sourceElement = TryGetObject(entry, "source") ?? TryGetObject(entry, "Source");
                var docId = NullIfWhiteSpace(TryGetString(entry, "docId") ?? TryGetString(entry, "DocId"))
                            ?? (sourceElement.HasValue ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "docId") ?? TryGetString(sourceElement.Value, "DocId")) : null);
                var docPath = NullIfWhiteSpace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath"))
                              ?? (sourceElement.HasValue ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "docPath") ?? TryGetString(sourceElement.Value, "DocPath")) : null);
                var docName = NullIfWhiteSpace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName"))
                              ?? (sourceElement.HasValue ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "docName") ?? TryGetString(sourceElement.Value, "DocName")) : null)
                              ?? NullIfWhiteSpace(Path.GetFileName(docPath ?? string.Empty));
                var categoryPath = NullIfWhiteSpace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath"))
                                   ?? NullIfWhiteSpace(TryGetString(entry, "category") ?? TryGetString(entry, "Category"))
                                   ?? (sourceElement.HasValue
                                       ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "categoryPath") ?? TryGetString(sourceElement.Value, "CategoryPath"))
                                         ?? NullIfWhiteSpace(TryGetString(sourceElement.Value, "category") ?? TryGetString(sourceElement.Value, "Category"))
                                       : null);
                var targetPageStart = ReadSourceBackedNavigationTargetPageStart(entry)
                                      ?? (sourceElement.HasValue ? ReadSourceBackedNavigationTargetPageStart(sourceElement.Value) : null);
                var targetPageEnd = ReadSourceBackedNavigationTargetPageEnd(entry, targetPageStart)
                                    ?? (sourceElement.HasValue ? ReadSourceBackedNavigationTargetPageEnd(sourceElement.Value, targetPageStart) : null);

                foreach (var rawLabel in ExtractSummarySearchCandidateLabels(entry, sourceElement))
                {
                    var cleaned = CleanNavigationRouteAnchorTitle(rawLabel);
                    if (string.IsNullOrWhiteSpace(cleaned))
                        continue;

                    var key = $"{docId}|{docPath}|{targetPageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{targetPageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{NormalizeLexicalLookup(cleaned)}";
                    if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                        continue;

                    var score = 1 + ComputeTreeNavigationAnchorFollowupScore(cleaned, rawLabel, queryTerms);
                    if (LooksLikeDocumentTreeNavigationAnchor(docPath ?? docName, cleaned))
                        score++;
                    yield return new SourceBackedDocumentNavigationFollowupLabel(
                        cleaned,
                        string.Join(" | ", new[] { rawLabel, docName, docPath, categoryPath }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                        score,
                        docId,
                        docPath,
                        categoryPath,
                        targetPageStart,
                        targetPageEnd);
                }
            }
        }
    }

    private static IEnumerable<string> ExtractSummarySearchCandidateLabels(JsonElement entry, JsonElement? sourceElement)
    {
        var directLabel = TryGetString(entry, "label")
                          ?? TryGetString(entry, "Label")
                          ?? TryGetString(entry, "title")
                          ?? TryGetString(entry, "Title");
        if (!string.IsNullOrWhiteSpace(directLabel))
            yield return directLabel;

        var docName = TryGetString(entry, "docName") ?? TryGetString(entry, "DocName");
        if (!string.IsNullOrWhiteSpace(docName))
            yield return Path.GetFileNameWithoutExtension(docName) ?? docName;

        foreach (var card in ExtractRagHitMatchedContentCards(entry) ?? Array.Empty<RagHitContentCardSummary>())
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                yield return card.Title;

            foreach (var signal in card.Signals ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(signal))
                    yield return signal;
            }
        }

        var profileSignals = BuildSourceProfileSignalsRef(entry);
        foreach (var hint in EnumerateSourceProfileHints(profileSignals).Take(12))
            yield return hint;

        if (sourceElement.HasValue)
        {
            foreach (var card in ExtractRagHitMatchedContentCards(sourceElement.Value) ?? Array.Empty<RagHitContentCardSummary>())
            {
                if (!string.IsNullOrWhiteSpace(card.Title))
                    yield return card.Title;

                foreach (var signal in card.Signals ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(signal))
                        yield return signal;
                }
            }

            foreach (var hint in EnumerateSourceProfileHints(BuildSourceProfileSignalsRef(sourceElement.Value)).Take(12))
                yield return hint;
        }
    }

    private static IEnumerable<SourceBackedDocumentNavigationFollowupLabel> ExtractDocumentNavigationFollowupLabels(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var entries = items.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .ToArray();

        for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
        {
            var entry = entries[entryIndex];
            var docId = CollapseWhitespace(TryGetString(entry, "docId") ?? TryGetString(entry, "DocId") ?? string.Empty);
            var docPath = CollapseWhitespace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty);
            var docName = CollapseWhitespace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName") ?? Path.GetFileName(docPath));
            var categoryPath = CollapseWhitespace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? string.Empty);
            var kind = CollapseWhitespace(TryGetString(entry, "kind") ?? TryGetString(entry, "Kind") ?? string.Empty);
            var method = CollapseWhitespace(TryGetString(entry, "resolutionMethod") ?? TryGetString(entry, "ResolutionMethod") ?? string.Empty);
            var hasTargetChunk = TryGetBool(entry, "hasTargetChunk") ?? TryGetBool(entry, "HasTargetChunk") ?? false;
            var hasTargetAnchor = TryGetBool(entry, "hasTargetAnchor") ?? TryGetBool(entry, "HasTargetAnchor") ?? false;
            var confidence = TryGetDouble(entry, "confidence") ?? TryGetDouble(entry, "Confidence");
            var targetPageStart = ReadSourceBackedNavigationTargetPageStart(entry);
            var targetPageEnd = ReadSourceBackedNavigationTargetPageEnd(entry, targetPageStart);
            targetPageEnd ??= InferSourceBackedNavigationTargetPageEndFromFollowingEntry(
                entries,
                entryIndex,
                docId,
                docPath,
                categoryPath,
                targetPageStart);

            var scoreHint = 1;
            if (hasTargetChunk)
                scoreHint += 3;
            if (hasTargetAnchor)
                scoreHint += 2;
            if (targetPageStart is not null)
                scoreHint += 2;
            if (targetPageEnd is not null && targetPageStart is not null && targetPageEnd.Value >= targetPageStart.Value)
                scoreHint += 1;
            if (confidence is >= 0.65)
                scoreHint += 2;
            if (confidence is >= 0.9)
                scoreHint += 1;
            if (kind.Contains("title", StringComparison.OrdinalIgnoreCase)
                || method.Contains("chunk", StringComparison.OrdinalIgnoreCase))
            {
                scoreHint += 1;
            }

            foreach (var label in ExtractDocumentNavigationEntryCandidateLabels(entry))
            {
                var raw = string.Join(" | ", new[] { label, docName, docPath, kind, method }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                yield return new SourceBackedDocumentNavigationFollowupLabel(
                    label,
                    raw,
                    scoreHint,
                    string.IsNullOrWhiteSpace(docId) ? null : docId,
                    string.IsNullOrWhiteSpace(docPath) ? null : docPath,
                    string.IsNullOrWhiteSpace(categoryPath) ? null : categoryPath,
                    targetPageStart,
                    targetPageEnd);
            }
        }
    }

    private static IEnumerable<ToolResults.Item> EnumerateRecentSourceBackedNavigationItems(ToolResults toolResults, string query)
    {
        var window = UsesSourceBackedPlanningCoverage(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            ? 12
            : 3;

        return toolResults.Items
            .Where(static item => item.ToolName == "documents.navigation" && string.IsNullOrWhiteSpace(item.Error))
            .TakeLast(window);
    }

    private static int ResolveSourceBackedSummaryFollowupResultWindow(string query)
        => UsesSourceBackedPlanningCoverage(query)
           || LooksLikeGenericCollectionOrListRequest(query)
           || LooksLikeBroadSourceBackedCompositionRequest(query)
            ? 8
            : 3;

    private static IEnumerable<string> ExtractDocumentNavigationEntryCandidateLabels(JsonElement entry)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "label", "Label",
                     "title", "Title",
                     "sectionTitle", "SectionTitle",
                     "heading", "Heading",
                     "headingPath", "HeadingPath",
                     "titlePath", "TitlePath",
                     "anchorTitle", "AnchorTitle",
                     "name", "Name"
                 })
        {
            var label = CollapseWhitespace(TryGetString(entry, name) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (emitted.Add(NormalizeLexicalLookup(label)))
                yield return label;
        }
    }

    private static int? ReadSourceBackedNavigationTargetPageStart(JsonElement entry)
        => TryGetFirstInt(
            entry,
            "targetPageStart",
            "TargetPageStart",
            "pageStart",
            "PageStart",
            "page_start",
            "fromPage",
            "FromPage",
            "pageFrom",
            "PageFrom",
            "sourcePage",
            "SourcePage",
            "pageNumber",
            "PageNumber",
            "page",
            "Page",
            "p",
            "P");

    private static int? ReadSourceBackedNavigationTargetPageEnd(JsonElement entry, int? pageStart)
    {
        var pageEnd = TryGetFirstInt(
            entry,
            "targetPageEnd",
            "TargetPageEnd",
            "pageEnd",
            "PageEnd",
            "page_end",
            "toPage",
            "ToPage",
            "pageTo",
            "PageTo",
            "endPage",
            "EndPage");

        return pageEnd;
    }

    private static int? TryGetFirstInt(JsonElement entry, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetInt(entry, name);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static int? InferSourceBackedNavigationTargetPageEndFromFollowingEntry(
        IReadOnlyList<JsonElement> entries,
        int currentIndex,
        string? docId,
        string? docPath,
        string? categoryPath,
        int? targetPageStart)
    {
        if (targetPageStart is null || targetPageStart.Value <= 0)
            return null;

        for (var i = currentIndex + 1; i < entries.Count; i++)
        {
            var next = entries[i];
            var nextStart = ReadSourceBackedNavigationTargetPageStart(next);
            if (nextStart is null || nextStart.Value <= targetPageStart.Value)
                continue;

            var nextDocId = CollapseWhitespace(TryGetString(next, "docId") ?? TryGetString(next, "DocId") ?? string.Empty);
            var nextDocPath = CollapseWhitespace(TryGetString(next, "docPath") ?? TryGetString(next, "DocPath") ?? string.Empty);
            var nextCategoryPath = CollapseWhitespace(TryGetString(next, "categoryPath") ?? TryGetString(next, "CategoryPath") ?? string.Empty);
            if (!IsSameSourceBackedNavigationScope(docId, docPath, categoryPath, nextDocId, nextDocPath, nextCategoryPath))
                continue;

            var boundedEnd = targetPageStart.Value + MaxInferredSourceBackedNavigationPageSpan;
            var previousSectionEnd = Math.Max(targetPageStart.Value, nextStart.Value - 1);
            return Math.Min(previousSectionEnd, boundedEnd);
        }

        return null;
    }

    private static bool IsSameSourceBackedNavigationScope(
        string? docId,
        string? docPath,
        string? categoryPath,
        string? candidateDocId,
        string? candidateDocPath,
        string? candidateCategoryPath)
    {
        var normalizedDocId = NormalizeLooseLookup(docId);
        var normalizedCandidateDocId = NormalizeLooseLookup(candidateDocId);
        if (!string.IsNullOrWhiteSpace(normalizedDocId) && !string.IsNullOrWhiteSpace(normalizedCandidateDocId))
            return string.Equals(normalizedDocId, normalizedCandidateDocId, StringComparison.Ordinal);

        var normalizedDocPath = NormalizeLooseLookup(docPath);
        var normalizedCandidateDocPath = NormalizeLooseLookup(candidateDocPath);
        if (!string.IsNullOrWhiteSpace(normalizedDocPath) && !string.IsNullOrWhiteSpace(normalizedCandidateDocPath))
            return string.Equals(normalizedDocPath, normalizedCandidateDocPath, StringComparison.Ordinal);

        var normalizedCategoryPath = NormalizeLooseLookup(categoryPath);
        var normalizedCandidateCategoryPath = NormalizeLooseLookup(candidateCategoryPath);
        return !string.IsNullOrWhiteSpace(normalizedCategoryPath)
               && !string.IsNullOrWhiteSpace(normalizedCandidateCategoryPath)
               && string.Equals(normalizedCategoryPath, normalizedCandidateCategoryPath, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ExtractTreeNavigationAnchorLabels(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("markdown", out var markdown)
            && markdown.ValueKind == JsonValueKind.String)
        {
            foreach (var label in ExtractMarkdownTreeNavigationAnchorLabels(markdown.GetString()))
                yield return label;
        }

        foreach (var label in ExtractJsonTreeNavigationAnchorLabels(result, depth: 0))
            yield return label;
    }

    private static IEnumerable<string> ExtractMarkdownTreeNavigationAnchorLabels(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            yield break;

        foreach (var raw in markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = CollapseWhitespace(raw);
            text = Regex.Replace(text, @"^[\s\-\*\+\u2022\u00b7|`>\\/.]+", string.Empty, RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"^(?:[????]+\s*)+", string.Empty, RegexOptions.CultureInvariant);
            text = CollapseWhitespace(text.Trim());
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private static IEnumerable<string> ExtractJsonTreeNavigationAnchorLabels(JsonElement element, int depth)
    {
        if (depth > 6)
            yield break;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var label = TryBuildTreeJsonStructureLabel(element);
                if (!string.IsNullOrWhiteSpace(label))
                    yield return label;

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && IsTreeStructureProperty(property.Name))
                    {
                        foreach (var childLabel in ExtractJsonTreeNavigationAnchorLabels(property.Value, depth + 1))
                            yield return childLabel;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var childLabel in ExtractJsonTreeNavigationAnchorLabels(item, depth + 1))
                        yield return childLabel;
                }

                break;
        }
    }

    private static IEnumerable<string> ExpandTreeNavigationAnchorLabel(string? rawLabel)
    {
        var cleaned = CollapseWhitespace(rawLabel ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '"', '\'', '\u2022', '\u00b7', '-', '\u2013');
        if (string.IsNullOrWhiteSpace(cleaned))
            yield break;

        cleaned = Regex.Replace(cleaned, @"\s*\(\s*\d+\s+(?:docs?|documents?|fichiers?|files?)\s*\)\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"^(?:(?:navigationOnly|orientationOnly)\s+\w+\s*:\s*)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"^(?:surfaceType\s*=\s*[^;]+;\s*)?(?:sourceScope\s*=\s*[^;]+;\s*)?(?:isFinalEvidence\s*=\s*false;\s*)?(?:requiresConcreteRetrieval\s*=\s*true;\s*)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = CollapseWhitespace(cleaned);
        if (string.IsNullOrWhiteSpace(cleaned))
            yield break;

        yield return cleaned;

        foreach (var segment in Regex.Split(cleaned, @"\s*>\s*|[\\/]+", RegexOptions.CultureInvariant))
        {
            var part = CollapseWhitespace(segment)
                .Trim(' ', '.', ',', ';', ':', '"', '\'', '\u2022', '\u00b7', '-', '\u2013');
            if (string.IsNullOrWhiteSpace(part))
                continue;

            yield return part;

            if (Regex.IsMatch(part, @"\.(?:pdf|docx?|xlsx?|pptx?|txt|md)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var withoutExtension = CollapseWhitespace(Path.GetFileNameWithoutExtension(part));
                if (!string.IsNullOrWhiteSpace(withoutExtension))
                    yield return withoutExtension;
            }
        }
    }

    private static IEnumerable<string> BuildSourceBackedTreeFollowupQueryTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(query ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(query);

        foreach (var term in ExtractQuerySignalTerms(normalized)
                     .Concat(ExtractPlanningRetrievalTerms(normalized))
                     .Concat(ExtractPlanningConstraintRetrievalTerms(normalized))
                     .SelectMany(BuildRetrievalTermVariants)
                     .Where(static term => term.Length >= 4)
                     .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
                     .Where(static term => !IsGenericPlanningCoverageTerm(term))
                     .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
                     .Distinct(StringComparer.Ordinal)
                     .Take(12))
        {
            yield return term;
        }
    }

    private static int ComputeTreeNavigationAnchorFollowupScore(string title, string rawLabel, IReadOnlyList<string> queryTerms)
    {
        var normalizedTitle = NormalizeLexicalLookup($"{title} {rawLabel}");
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return 0;

        var score = queryTerms.Count(term => normalizedTitle.Contains(NormalizeLexicalLookup(term), StringComparison.Ordinal));
        if (LooksLikeDocumentTreeNavigationAnchor(rawLabel, title))
            score += 1;
        if (Regex.IsMatch(normalizedTitle, @"\b(?:title|titre|heading|section|chapter|chapitre|document|file|fichier)\b", RegexOptions.CultureInvariant))
            score += 1;

        return score;
    }

    private static bool LooksLikeDocumentTreeNavigationAnchor(string? rawLabel, string? title)
    {
        var value = NormalizeLexicalLookup($"{rawLabel} {title}");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Regex.IsMatch(value, @"\b(?:pdf|docx?|xlsx?|pptx?|txt|md)\b", RegexOptions.CultureInvariant)
               || Regex.IsMatch(value, @"\b(?:document|documents|file|files|fichier|fichiers|docpath|path)\b", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> BuildRouteAnchorFollowupContentTerms(string? query, string language)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        switch (NormalizeLanguageCode(language))
        {
            case "fr":
                yield return "contenu";
                yield return "details";
                yield return "etapes";
                yield return "methode";
                yield return "procedure";
                yield return "quantites";
                break;
            case "es":
                yield return "contenido";
                yield return "detalles";
                yield return "pasos";
                yield return "metodo";
                yield return "procedimiento";
                yield return "cantidades";
                break;
            case "pt":
                yield return "conteudo";
                yield return "detalhes";
                yield return "passos";
                yield return "metodo";
                yield return "procedimento";
                yield return "quantidades";
                break;
            case "de":
                yield return "inhalt";
                yield return "details";
                yield return "schritte";
                yield return "methode";
                yield return "verfahren";
                yield return "mengen";
                break;
            case "it":
                yield return "contenuto";
                yield return "dettagli";
                yield return "passaggi";
                yield return "metodo";
                yield return "procedura";
                yield return "quantita";
                break;
            default:
                yield return "content";
                yield return "details";
                yield return "steps";
                yield return "method";
                yield return "procedure";
                yield return "quantities";
                break;
        }

        if (UsesSourceBackedPlanningCoverage(query)
            || Regex.IsMatch(normalizedQuery, @"\b(?:plan|planning|programme|schedule|agenda|calendrier|semana|woche|settimana)\b", RegexOptions.CultureInvariant))
        {
            yield return "details";
            yield return "content";
            yield return "constraints";
        }

        if (LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeBroadSynthesisRequestShape(query))
        {
            yield return "options";
            yield return "examples";
            yield return "candidates";
        }
    }

    private static void AddGeneratedSourceBackedFollowupQuery(
        List<string> queries,
        HashSet<string> emitted,
        string? query)
    {
        var sanitized = SanitizeSourceBackedLlmExplorationQuery(query);
        if (string.IsNullOrWhiteSpace(sanitized))
            return;

        var key = NormalizeGeneratedSourceBackedExplorationQueryForDedup(sanitized);
        if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
            return;

        queries.Add(sanitized);
    }

    private static IEnumerable<string> ExtractSourceBackedRouteAnchorFollowupTitles(ToolResults toolResults, string query)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in EnumerateRagHitSummaries(toolResults))
        {
            if (!IsRouteDiscoveryAnchorHit(hit)
                && !LooksLikeNavigationOnlyHit(hit)
                && !LooksLikeResolvedRouteTargetHit(hit)
                && !HasRouteContentCardCue(hit))
            {
                continue;
            }

            foreach (var title in ExtractRouteDiscoveryTitleCues(hit)
                         .Concat(ExtractSourceBackedTitleCandidates(hit))
                         .Select(CleanNavigationRouteAnchorTitle)
                         .Where(IsUsableSourceBackedOptionTitle))
            {
                if (LooksLikeWeakSourceBackedOptionTitle(title)
                    || (UsesSourceBackedPlanningCoverage(query) && LooksLikeWeakStructuredPlanningAnchorFollowupTitle(title)))
                    continue;

                var key = NormalizeLexicalLookup(title);
                if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                    continue;

                yield return title;
            }
        }
    }
}
