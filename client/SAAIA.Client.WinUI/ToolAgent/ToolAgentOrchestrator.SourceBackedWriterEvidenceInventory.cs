using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedCandidateLeadsForWriter(
        ToolResults toolResults,
        string query,
        string language,
        int? maxItems = null,
        int? maxChars = null)
    {
        if (!toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return "none";

        var lines = new List<string>();
        var isPlanning = LooksLikeAnyDocumentaryPlanningRequest(query);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var targetSlots = isPlanning
            ? ResolveSourceBackedPlanningTargetItemCount(query)
            : 0;
        var minimumCandidates = isPlanning
            ? ResolveMinimumBroadSourceBackedSynthesisHitCount(query)
            : 0;
        var maxCandidates = isPlanning
            ? Math.Clamp(
                Math.Max(Math.Max(12, minimumCandidates + 4), targetSlots),
                12,
                targetSlots >= 10 ? 32 : 18)
            : 6;
        if (maxItems is > 0)
            maxCandidates = Math.Min(maxCandidates, maxItems.Value);
        if (isPlanning)
        {
            var rosterMaxItems = maxItems is > 0
                ? Math.Max(1, maxItems.Value)
                : Math.Clamp(
                    Math.Max(Math.Max(targetSlots, minimumCandidates), targetSlots + 4),
                    12,
                    targetSlots >= 10 ? 32 : 18);
            var sourceRoster = BuildSourceBackedPlanningSourceRosterForWriter(
                toolResults,
                query,
                language,
                rosterMaxItems);
            if (!string.IsNullOrWhiteSpace(sourceRoster))
                return LimitSourceBackedCandidateLeadInventory(sourceRoster, maxItems, maxChars);
        }

        var candidates = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                maxCandidates,
                NormalizeLanguageCode(language))
            .ToList();

        foreach (var candidate in candidates)
        {
            AddSourceBackedCandidateLeadLine(lines, "item", candidate, query, language);
        }

        if (isPlanning)
        {
            var candidatePageKeys = candidates
                .Select(static candidate => BuildRagHitVisiblePageMergeKey(candidate.Hit))
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var acceptedCandidateKeys = candidates
                .Select(BuildSourceBackedPlanningCandidateKey)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shouldExposeSourcePageContext = lines.Count < Math.Min(maxCandidates, Math.Max(1, minimumCandidates));
            if (shouldExposeSourcePageContext)
            {
                var planningContextLimit = Math.Max(0, maxCandidates - lines.Count);
                var fallbackHits = EnumerateRagHitSummaries(toolResults)
                    .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
                    .Where(hit => !strictStructuredPlanning
                        || ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, query))
                    .Where(hit => !candidatePageKeys.Contains(BuildRagHitVisiblePageMergeKey(hit)))
                    .OrderByDescending(static hit => BackendSelectionHintsPreferUsableEvidence(hit) ? 1 : 0)
                    .ThenByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(hit))
                    .ThenByDescending(static hit => hit.Score)
                    .Take(planningContextLimit)
                    .ToList();

                foreach (var hit in fallbackHits)
                {
                    var title = ExtractReadablePartialPlanningLeadTitle(hit, query);
                    if (string.IsNullOrWhiteSpace(title))
                        title = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 140);
                    if (string.IsNullOrWhiteSpace(title))
                        title = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(title))
                        title = "source-backed context";

                    AddSourceBackedCandidateLeadLine(lines, "source_page", title, hit, language);
                }
            }

            AddAnnotatedSourceBackedPlanningCandidateLeadLines(
                lines,
                toolResults,
                query,
                language,
                acceptedCandidateKeys,
                Math.Clamp(maxCandidates / 2, 4, 8));

            return LimitSourceBackedCandidateLeadInventory(
                lines.Count == 0
                    ? "none"
                    : string.Join(Environment.NewLine, lines.Take(maxCandidates + Math.Clamp(maxCandidates / 2, 4, 8))),
                maxItems,
                maxChars);
        }

        var candidateKeys = candidates
            .Select(static candidate => BuildRagHitVisiblePageMergeKey(candidate.Hit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextLimit = lines.Count == 0 ? 8 : Math.Max(2, 8 - lines.Count);
        var contextHits = EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .Where(hit => !candidateKeys.Contains(BuildRagHitVisiblePageMergeKey(hit)))
            .OrderByDescending(static hit => LooksLikeNavigationOnlyHit(hit) ? 0 : ComputeSourceBackedEvidenceRichnessScore(hit))
            .ThenByDescending(static hit => hit.Score)
            .Take(contextLimit)
            .ToList();

        foreach (var hit in contextHits)
        {
            var isDiscoveryAnchor = LooksLikeNavigationOnlyHit(hit) && IsRouteDiscoveryAnchorHit(hit);
            var title = isDiscoveryAnchor
                ? ExtractRouteDiscoveryTitleCue(hit)
                : ExtractReadablePartialPlanningLeadTitle(hit, query);
            if (string.IsNullOrWhiteSpace(title))
                title = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(title))
                title = "source-backed context";

            AddSourceBackedCandidateLeadLine(lines, isDiscoveryAnchor ? "navigation" : "context", title, hit, language);
        }

        return LimitSourceBackedCandidateLeadInventory(
            lines.Count == 0
                ? "none"
                : string.Join(Environment.NewLine, lines.Take(isPlanning ? maxCandidates : 10)),
            maxItems,
            maxChars);
    }

    private static string LimitSourceBackedCandidateLeadInventory(string inventory, int? maxItems, int? maxChars)
    {
        if (string.IsNullOrWhiteSpace(inventory)
            || string.Equals(inventory.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        var itemLimit = maxItems is > 0 ? maxItems.Value : int.MaxValue;
        var charLimit = maxChars is > 0 ? maxChars.Value : int.MaxValue;
        if (itemLimit == int.MaxValue && charLimit == int.MaxValue)
            return inventory;

        var selected = new List<string>();
        var currentChars = 0;
        foreach (var rawLine in inventory.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (selected.Count >= itemLimit)
                break;

            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var separatorChars = selected.Count == 0 ? 0 : Environment.NewLine.Length;
            if (currentChars + separatorChars + line.Length > charLimit)
            {
                if (selected.Count == 0)
                    selected.Add(TruncateForPrompt(line, Math.Max(1, charLimit)));
                break;
            }

            selected.Add(line);
            currentChars += separatorChars + line.Length;
        }

        return selected.Count == 0
            ? "none"
            : string.Join(Environment.NewLine, selected);
    }

    private static int? ResolveEvidenceInventoryItemLimitForPromptChars(int? maxChars, string? query = null)
    {
        if (maxChars is not > 0)
            return null;

        int limit;
        if (maxChars.Value <= 1200)
        {
            limit = 4;
        }
        else if (maxChars.Value <= 2400)
        {
            limit = 8;
        }
        else if (maxChars.Value <= 4200)
        {
            limit = 12;
        }
        else
        {
            limit = 18;
        }

        if (!LooksLikeAnyDocumentaryPlanningRequest(query)
            && !ShouldGateStructuredSourceBackedPlanningCoverage(query))
        {
            return limit;
        }

        var targetSlots = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(query));
        if (maxChars.Value <= 4200)
            return Math.Max(limit, Math.Min(targetSlots, 12));

        var planningCap = maxChars.Value <= 7000 ? 24 : 32;
        return Math.Max(limit, Math.Clamp(targetSlots, 12, planningCap));
    }

    private static string BuildSourceBackedPlanningSourceRosterForWriter(
        ToolResults toolResults,
        string query,
        string language,
        int maxItems)
    {
        if (maxItems <= 0)
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var normalizedQuery = query ?? string.Empty;
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var sourcePages = EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, normalizedQuery))
            .Where(hit => !strictStructuredPlanning
                || ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, normalizedQuery))
            .OrderByDescending(static hit => BackendSelectionHintsPreferUsableEvidence(hit) ? 1 : 0)
            .ThenByDescending(static hit => hit.MatchedContentCards?.Count ?? 0)
            .ThenByDescending(static hit => GetBestRagEvidenceText(hit).Length)
            .ThenByDescending(static hit => hit.Score)
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(maxItems)
            .ToList();
        if (sourcePages.Count == 0)
            return string.Empty;

        var lines = new List<string>(sourcePages.Count);
        foreach (var hit in sourcePages)
        {
            AddSourceBackedPlanningSourceRosterLine(lines, hit, query, language);
        }
        if (!lines.Any(static line => line.Contains("role=\"source_page\"", StringComparison.OrdinalIgnoreCase)))
        {
            var contextSourcePage = sourcePages.FirstOrDefault(hit =>
            {
                var title = BuildSourceBackedPlanningRuntimeTraceTitle(hit, query);
                return !ShouldMarkPlanningSourceRosterLineAsConcreteItem(hit, title, query)
                    && string.IsNullOrWhiteSpace(ClassifyPlanningSourceRosterDiagnosticRejection(hit, title, query));
            });
            if (contextSourcePage is not null)
                AddSourceBackedPlanningSourceRosterLine(lines, contextSourcePage, query, language, forcedRole: "source_page");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildWriterEvidenceRosterForCitationMapping(
        ToolResults toolResults,
        string query,
        string language,
        bool allowVisibleSourceFallback = true,
        bool allowDiagnosticRows = true)
    {
        var writerRoster = BuildSourceBackedCandidateLeadsForWriter(toolResults, query, language);
        if (ContainsWriterEvidenceItems(writerRoster))
        {
            var mappingRoster = FilterWriterEvidenceRosterForCitationMapping(writerRoster, allowDiagnosticRows);
            if (ContainsWriterEvidenceItems(mappingRoster))
            {
                if (allowVisibleSourceFallback
                    && LooksLikeAnyDocumentaryPlanningRequest(query))
                {
                    var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
                    var minimumCandidates = ResolveMinimumBroadSourceBackedSynthesisHitCount(query);
                    var visibleRoster = BuildVisibleStructuredPlanningSourceRosterForWriter(
                        toolResults,
                        query,
                        language,
                        Math.Clamp(Math.Max(targetSlots, minimumCandidates), 12, targetSlots >= 10 ? 32 : 18));
                    var mappingRows = CountEvidenceInventoryRows(mappingRoster);
                    var visibleRows = CountEvidenceInventoryRows(visibleRoster);
                    var requiredRows = visibleRows > 0
                        ? Math.Min(Math.Max(1, targetSlots), visibleRows)
                        : Math.Max(1, Math.Min(targetSlots, minimumCandidates));
                    if (mappingRows < requiredRows && visibleRows > mappingRows)
                        return visibleRoster;
                }

                return mappingRoster;
            }
        }

        if (allowVisibleSourceFallback
            && LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
            var minimumCandidates = ResolveMinimumBroadSourceBackedSynthesisHitCount(query);
            var visibleRoster = BuildVisibleStructuredPlanningSourceRosterForWriter(
                toolResults,
                query,
                language,
                Math.Clamp(Math.Max(targetSlots, minimumCandidates), 12, targetSlots >= 10 ? 32 : 18));
            if (!string.IsNullOrWhiteSpace(visibleRoster))
                return visibleRoster;
        }

        return writerRoster;
    }

    private static bool ContainsWriterEvidenceItems(string? inventory)
        => !string.IsNullOrWhiteSpace(inventory)
           && inventory.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase);

    private static int CountEvidenceInventoryRows(string? inventory, string? role = null)
    {
        if (string.IsNullOrWhiteSpace(inventory))
            return 0;

        return inventory
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line =>
                line.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(role)
                    || line.Contains($"role=\"{role}\"", StringComparison.OrdinalIgnoreCase)));
    }

    private static string FilterWriterEvidenceRosterForCitationMapping(
        string? inventory,
        bool allowDiagnosticRows = true)
    {
        if (string.IsNullOrWhiteSpace(inventory))
            return string.Empty;

        return string.Join(
            Environment.NewLine,
            inventory
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(static line => !line.Contains("role=\"candidate_rejected\"", StringComparison.OrdinalIgnoreCase))
                .Where(static line => !line.Contains("codeDecision=\"rejected_by_code\"", StringComparison.OrdinalIgnoreCase))
                .Where(line => allowDiagnosticRows
                    || (!line.Contains("role=\"candidate_not_selected\"", StringComparison.OrdinalIgnoreCase)
                        && !line.Contains("codeDecision=\"not_selected_by_code\"", StringComparison.OrdinalIgnoreCase))));
    }

    private static string BuildVisibleStructuredPlanningSourceRosterForWriter(
        ToolResults toolResults,
        string query,
        string language,
        int maxItems)
    {
        if (maxItems <= 0)
            return string.Empty;

        var sources = BuildVisibleStructuredPlanningSourcePool(toolResults, query)
            .Take(maxItems)
            .ToList();
        if (sources.Count == 0)
            return string.Empty;

        var lines = new List<string>(sources.Count);
        foreach (var source in sources)
        {
            var evidenceId = BuildWriterEvidenceItemId(lines.Count);
            var visibleSource = string.IsNullOrWhiteSpace(source.DocName)
                ? Path.GetFileName(source.DocPath)
                : source.DocName;
            if (string.IsNullOrWhiteSpace(visibleSource))
                visibleSource = source.Label;
            if (string.IsNullOrWhiteSpace(visibleSource))
                visibleSource = source.DocPath;

            var title = BuildVisibleStructuredPlanningSourceRosterTitle(source, query, language);
            var citation = BuildInlineSourceCitationForWriter(visibleSource, source.PageStart, language);
            var pageKey = BuildSourceRefVisiblePageMergeKey(source);
            var role = string.IsNullOrWhiteSpace(source.SelectionHintEvidenceRole)
                ? "source_page"
                : CollapseWhitespace(source.SelectionHintEvidenceRole);
            var supportCue = BuildVisibleStructuredPlanningSourceRosterSupportCue(source);
            var supportSuffix = string.IsNullOrWhiteSpace(supportCue)
                ? string.Empty
                : $" evidence=\"{EscapeEvidenceRosterAttribute(supportCue)}\"";
            lines.Add(
                $"EVIDENCE_ITEM id=\"{evidenceId}\" use=\"[{evidenceId}]\" role=\"source_page\" evidenceRole=\"{EscapeEvidenceRosterAttribute(role)}\" title=\"{EscapeEvidenceRosterAttribute(title)}\" citation=\"{EscapeEvidenceRosterAttribute(citation)}\" source=\"{EscapeEvidenceRosterAttribute(visibleSource)}\" page=\"{Math.Max(1, source.PageStart)}\" pageKey=\"{EscapeEvidenceRosterAttribute(pageKey)}\" instruction=\"LLM decides if this source is useful; if used, output only [{evidenceId}] and let code render the citation\"{supportSuffix}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildVisibleStructuredPlanningSourceRosterTitle(
        ToolMemory.SourceRef source,
        string query,
        string language)
    {
        var cardTitle = source.MatchedContentCards?
            .Select(static card => CleanSourceBackedOptionTitle(card.Title))
            .FirstOrDefault(IsStrictSourceBackedPlanningInventoryCandidateTitle);
        if (!string.IsNullOrWhiteSpace(cardTitle))
            return cardTitle;

        var section = CollapseWhitespace(source.SectionTitle ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(section))
            return section;

        var heading = CollapseWhitespace(source.HeadingPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(heading))
            return heading;

        var label = CollapseWhitespace(source.Label ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        var docName = string.IsNullOrWhiteSpace(source.DocName)
            ? Path.GetFileName(source.DocPath)
            : source.DocName;
        return string.IsNullOrWhiteSpace(docName)
            ? "source-backed page"
            : docName;
    }

    private static string BuildVisibleStructuredPlanningSourceRosterSupportCue(ToolMemory.SourceRef source)
    {
        var cardCue = source.MatchedContentCards?
            .Select(BuildVisibleStructuredPlanningContentCardSupportCue)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(cardCue))
            return TruncateForPrompt(cardCue, 220);

        var section = CollapseWhitespace(source.SectionTitle ?? source.HeadingPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(section))
            return TruncateForPrompt(section, 120);

        var role = CollapseWhitespace(source.SelectionHintEvidenceRole ?? source.ContentRole ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(role))
            return $"source role: {role}";

        return string.Empty;
    }

    private static string BuildVisibleStructuredPlanningContentCardSupportCue(ToolMemory.SourceContentCardRef card)
    {
        var fragments = new List<string>();
        var title = CollapseWhitespace(card.Title ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(title)
            && !LooksLikeGenericWriterEvidenceCueTitle(title)
            && !LooksLikeNoisyCandidateSupportCue(title))
        {
            fragments.Add(title);
        }

        var evidence = BuildVisibleStructuredPlanningCardEvidenceCue(card.Evidence);
        if (!string.IsNullOrWhiteSpace(evidence)
            && !LooksLikeNoisyCandidateSupportCue(evidence))
        {
            fragments.Add(evidence);
        }

        return TruncateForPrompt(
            string.Join(" | ", fragments.Distinct(StringComparer.OrdinalIgnoreCase)),
            220);
    }

    private static string BuildVisibleStructuredPlanningCardEvidenceCue(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } evidenceElement)
            return string.Empty;

        var fragments = new List<string>();
        AddVisibleStructuredPlanningEvidenceArrayCue(
            fragments,
            evidenceElement,
            maxItems: 2,
            "facts",
            "Facts");
        AddVisibleStructuredPlanningEvidenceArrayCue(
            fragments,
            evidenceElement,
            maxItems: 2,
            "quantityFacts",
            "quantity_facts",
            "QuantityFacts");

        return CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(
            string.Join(" | ", fragments.Distinct(StringComparer.OrdinalIgnoreCase)),
            maxLength: 220));
    }

    private static void AddVisibleStructuredPlanningEvidenceArrayCue(
        List<string> fragments,
        JsonElement evidenceElement,
        int maxItems,
        params string[] propertyNames)
    {
        if (fragments.Count >= 4)
            return;

        foreach (var propertyName in propertyNames)
        {
            if (!evidenceElement.TryGetProperty(propertyName, out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                var text = BuildVisibleStructuredPlanningEvidenceItemCue(item);
                if (!string.IsNullOrWhiteSpace(text)
                    && !LooksLikeNoisyCandidateSupportCue(text))
                {
                    fragments.Add(text);
                }

                if (fragments.Count >= maxItems + 2)
                    return;
            }
        }
    }

    private static string BuildVisibleStructuredPlanningEvidenceItemCue(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var sourceText = TryGetString(item, "sourceText")
            ?? TryGetString(item, "source_text")
            ?? TryGetString(item, "SourceText");
        if (!string.IsNullOrWhiteSpace(sourceText))
            return CollapseWhitespace(sourceText);

        var label = TryGetString(item, "label") ?? TryGetString(item, "Label");
        var value = TryGetString(item, "value") ?? TryGetString(item, "Value");
        var unit = TryGetString(item, "unit") ?? TryGetString(item, "Unit");
        return CollapseWhitespace(string.Join(
            ' ',
            new[] { label, value, unit }.Where(static part => !string.IsNullOrWhiteSpace(part))));
    }

    private static string EscapeEvidenceRosterAttribute(string? value)
        => CollapseWhitespace(value ?? string.Empty).Replace("\"", "'", StringComparison.Ordinal);

    private static void AddSourceBackedPlanningSourceRosterLine(
        List<string> lines,
        RagHitSummary hit,
        string? query,
        string language,
        string? forcedRole = null)
    {
        var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        if (string.IsNullOrWhiteSpace(source))
            source = hit.DocPath;

        var title = BuildSourceBackedPlanningRuntimeTraceTitle(hit, query);
        if (string.IsNullOrWhiteSpace(title))
            title = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 80);
        if (string.IsNullOrWhiteSpace(title))
            title = "source-backed context";

        var evidenceId = BuildWriterEvidenceItemId(lines.Count);
        var pageKey = BuildRagHitVisiblePageMergeKey(hit);
        var citation = BuildInlineSourceCitationForWriter(source, hit.PageStart, language);
        var retrievalQuery = TruncateForPrompt(CollapseWhitespace(hit.RetrievalQuery ?? string.Empty), 50);
        var slotMetadata = BuildSourceBackedPlanningSourcePageSlotMetadataForInventory(hit, title, query, language);
        var rejectionReason = ClassifyPlanningSourceRosterDiagnosticRejection(hit, title, query);
        var role = !string.IsNullOrWhiteSpace(forcedRole)
            ? forcedRole
            : ShouldMarkPlanningSourceRosterLineAsConcreteItem(hit, title, query)
            ? "item"
            : string.IsNullOrWhiteSpace(rejectionReason)
                ? "source_page"
                : "candidate_rejected";

        var candidateKey = BuildSourceBackedPlanningSourcePageCandidateKey(hit, title);
        var diagnosticSuffix = string.Equals(role, "candidate_rejected", StringComparison.Ordinal)
            ? $" codeDecision=\"rejected_by_code\" codeReason=\"{rejectionReason}\" instruction=\"diagnostic only; LLM may still use this source if the visible source evidence answers the user and the final item is cited\""
            : string.Empty;
        var supportCue = BuildSourceBackedPlanningSourceRosterSupportCue(hit, title, query);
        var supportSuffix = string.IsNullOrWhiteSpace(supportCue)
            ? string.Empty
            : $" evidence=\"{EscapeEvidenceRosterAttribute(supportCue)}\"";
        lines.Add(
            $"EVIDENCE_ITEM id=\"{evidenceId}\" use=\"[{evidenceId}]\" role=\"{role}\" title=\"{TruncateForPrompt(CollapseWhitespace(title), 70)}\" citation=\"{citation}\" source=\"{source}\" page=\"{Math.Max(1, hit.PageStart)}\" pageKey=\"{pageKey}\" candidateKey=\"{candidateKey}\" slotRoute=\"{slotMetadata.Route}\" slotFit=\"{slotMetadata.Fit}\" retrievalQuery=\"{retrievalQuery}\"{diagnosticSuffix}{supportSuffix}");
    }

    private static string BuildSourceBackedPlanningSourceRosterSupportCue(
        RagHitSummary hit,
        string? title,
        string? query)
    {
        if (LooksLikeDocumentaryPlanningContextSourcePage(hit, title, query))
        {
            var evidence = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 90));
            if (string.IsNullOrWhiteSpace(evidence) || LooksLikeNoisyCandidateSupportCue(evidence))
                evidence = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(hit.ContextualSnippet ?? string.Empty, maxLength: 90));
            if (!string.IsNullOrWhiteSpace(evidence)
                && !LooksLikeNoisyCandidateSupportCue(evidence))
            {
                return evidence;
            }
        }

        return BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 180);
    }

    private static bool ShouldMarkPlanningSourceRosterLineAsConcreteItem(RagHitSummary hit, string title, string? query)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;
        if (LooksLikeExplicitlyNonConcreteSourceEvidence(BuildPlanningSourceRosterNormalizedEvidence(hit, title)))
            return false;
        if (LooksLikePlanItemNoise(title)
            || LooksLikeWeakSourceBackedOptionTitle(title)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(title)
            || LooksLikeProcedureSentenceTitle(normalizedTitle)
            || LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle)
            || LooksLikeShortNumberedStrictPlanningFragmentTitle(normalizedTitle)
            || LooksLikeGenericRubricOrTaxonomyOnlySourceHit(hit, title)
            || LooksLikeRequestedPlanningSlotAxisLabelTitle(title, query)
            || normalizedTitle.Contains("semaine", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikeConcreteStructuredPlanningCandidateTitle(title))
            return true;

        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        return role == "actionable_item"
               && Math.Max(actionability, support) >= 40
               && HasConcretePageGroundedEvidence(hit);
    }

    private static string ClassifyPlanningSourceRosterDiagnosticRejection(RagHitSummary hit, string? title, string? query)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return "missing_title";
        if (LooksLikeExplicitlyNonConcreteSourceEvidence(BuildPlanningSourceRosterNormalizedEvidence(hit, title)))
            return "explicitly_non_concrete_source";
        if (LooksLikeDocumentaryPlanningContextSourcePage(hit, title, query))
            return string.Empty;
        if (LooksLikeRequestedPlanningSlotAxisLabelTitle(title, query))
            return "requested_axis_label";
        if (normalizedTitle.Contains("semaine", StringComparison.Ordinal))
        {
            return "generic_planning_context";
        }

        if (LooksLikeGenericRubricOrTaxonomyOnlySourceHit(hit, title))
            return "rubric_or_taxonomy_context";
        if (LooksLikePlanItemNoise(title ?? string.Empty))
            return "plan_item_noise";
        if (LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle)
            || LooksLikeShortNumberedStrictPlanningFragmentTitle(normalizedTitle))
            return "page_title_noise";
        if (LooksLikeWeakSourceBackedOptionTitle(title ?? string.Empty))
            return "weak_candidate_title";
        if (LooksLikeNoisyStructuredPlanningCandidateTitle(title ?? string.Empty))
            return "noisy_candidate_title";
        if (LooksLikeProcedureSentenceTitle(normalizedTitle))
            return "procedure_sentence_title";

        return string.Empty;
    }

    private static string BuildPlanningSourceRosterNormalizedEvidence(RagHitSummary hit, string? title)
        => NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[]
            {
                title,
                hit.SectionTitle,
                hit.HeadingPath,
                hit.Excerpt,
                hit.FullText,
                hit.ContextualSnippet,
                BuildPageLocalStructuredPlanningEvidenceText(hit)
            }.Where(static part => !string.IsNullOrWhiteSpace(part)))));

    private static bool LooksLikeRequestedPlanningSlotAxisLabelTitle(string? title, string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(title));
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var labels = DetectRequestedPlanningSlotAxisLabels(query, DetectRetrievalExpansionLanguage(query))
            .Concat(ExtractPlanningSlotRetrievalTerms(query))
            .SelectMany(ExpandPlanningSlotRetrievalTermVariants)
            .Select(NormalizeLexicalLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (labels.Count == 0)
            return false;

        if (labels.Contains(normalizedTitle))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return titleTerms.Length == 1
            && labels.Contains(titleTerms[0])
            && normalizedTitle.Length <= titleTerms[0].Length + 2;
    }

    private static (string Route, string Fit) BuildSourceBackedPlanningSourcePageSlotMetadataForInventory(
        RagHitSummary hit,
        string title,
        string? query,
        string language)
    {
        language = NormalizeLanguageCode(language);
        var labels = DetectRequestedPlanningSlotAxisLabels(query, language);
        if (labels.Count == 0)
            return (string.Empty, "none");

        var routeText = NormalizeLexicalLookup(string.Join(
            " ",
            hit.RetrievalQuery,
            title,
            hit.SectionTitle,
            hit.HeadingPath,
            BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 160)));
        foreach (var label in labels)
        {
            var normalizedLabel = NormalizeLexicalLookup(label);
            if (!string.IsNullOrWhiteSpace(normalizedLabel)
                && routeText.Contains(normalizedLabel, StringComparison.Ordinal))
            {
                return (label, "source_page");
            }
        }

        return (string.Empty, string.IsNullOrWhiteSpace(hit.RetrievalQuery) ? "unrouted" : "neutral");
    }

    private static string BuildSourceBackedPlanningSourcePageCandidateKey(RagHitSummary hit, string title)
    {
        var pageKey = BuildRagHitVisiblePageMergeKey(hit);
        var titleKey = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(pageKey))
            return titleKey;
        if (string.IsNullOrWhiteSpace(titleKey))
            return pageKey;
        return $"{pageKey}|{titleKey}";
    }

    private static void AddSourceBackedCandidateLeadLine(List<string> lines, string role, string title, RagHitSummary hit, string language)
    {
        var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        if (string.IsNullOrWhiteSpace(source))
            source = hit.DocPath;

        var supportCue = BuildSourceBackedCandidateSupportCue(hit);
        var contentRole = CollapseWhitespace(hit.SelectionHintRole ?? hit.ContentRole ?? string.Empty);
        var writingNote = BuildSourceBackedCandidateWritingNote(contentRole, hit, language);
        var supportSuffix = string.IsNullOrWhiteSpace(supportCue)
            ? string.Empty
            : $"; evidence=\"{CollapseWhitespace(supportCue)}\"";
        var pageKey = BuildRagHitVisiblePageMergeKey(hit);
        var citation = BuildInlineSourceCitationForWriter(source, hit.PageStart, language);
        var retrievalQuery = TruncateForPrompt(CollapseWhitespace(hit.RetrievalQuery ?? string.Empty), 90);
        var evidenceId = BuildWriterEvidenceItemId(lines.Count);
        lines.Add(
            $"EVIDENCE_ITEM id=\"{evidenceId}\" role=\"{role}\" title=\"{CollapseWhitespace(title)}\" source=\"{source}\" page=\"{Math.Max(1, hit.PageStart)}\" citation=\"{citation}\" pageKey=\"{pageKey}\" retrievalQuery=\"{retrievalQuery}\" instruction=\"{writingNote}\"{supportSuffix}");
    }

    private static void AddSourceBackedCandidateLeadLine(
        List<string> lines,
        string role,
        SourceBackedOptionCandidate candidate,
        string query,
        string language)
    {
        var hit = candidate.Hit;
        var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        if (string.IsNullOrWhiteSpace(source))
            source = hit.DocPath;

        var supportCue = BuildSourceBackedCandidateSupportCue(hit);
        var contentRole = CollapseWhitespace(hit.SelectionHintRole ?? hit.ContentRole ?? string.Empty);
        var writingNote = BuildSourceBackedCandidateWritingNote(contentRole, hit, language);
        var supportSuffix = string.IsNullOrWhiteSpace(supportCue)
            ? string.Empty
            : $"; evidence=\"{CollapseWhitespace(supportCue)}\"";
        var retrievalQuery = TruncateForPrompt(CollapseWhitespace(hit.RetrievalQuery ?? string.Empty), 90);
        var pageKey = BuildRagHitVisiblePageMergeKey(hit);
        var citation = BuildInlineSourceCitationForWriter(source, hit.PageStart, language);
        var candidateKey = BuildSourceBackedPlanningCandidateLeadKey(candidate);
        var slotMetadata = BuildSourceBackedCandidateSlotMetadataForInventory(candidate, query, language);
        var evidenceId = BuildWriterEvidenceItemId(lines.Count);
        lines.Add(
            $"EVIDENCE_ITEM id=\"{evidenceId}\" role=\"{role}\" title=\"{CollapseWhitespace(candidate.Title)}\" source=\"{source}\" page=\"{Math.Max(1, hit.PageStart)}\" citation=\"{citation}\" pageKey=\"{pageKey}\" candidateKey=\"{candidateKey}\" slotRoute=\"{slotMetadata.Route}\" slotFit=\"{slotMetadata.Fit}\" retrievalQuery=\"{retrievalQuery}\" instruction=\"{writingNote}\"{supportSuffix}");
    }

    private static void AddAnnotatedSourceBackedPlanningCandidateLeadLines(
        List<string> lines,
        ToolResults toolResults,
        string query,
        string language,
        ISet<string> acceptedCandidateKeys,
        int maxAnnotatedCandidates)
    {
        if (maxAnnotatedCandidates <= 0)
            return;

        var requireDirectPageEvidence = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var dominantTopLevelScope = requireDirectPageEvidence
            ? TryInferDominantTopLevelCategoryScope(toolResults, query)
            : null;
        var seen = new HashSet<string>(acceptedCandidateKeys, StringComparer.OrdinalIgnoreCase);
        var diagnosticHitLimit = Math.Clamp(maxAnnotatedCandidates * 4, maxAnnotatedCandidates, 32);
        var diagnosticHits = EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
            .OrderByDescending(static hit => BackendSelectionHintsPreferUsableEvidence(hit) ? 1 : 0)
            .ThenByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(hit))
            .ThenByDescending(static hit => hit.Score)
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(diagnosticHitLimit)
            .ToList();
        var annotated = diagnosticHits
            .Select(hit =>
            {
                var title = ExtractSourceBackedOptionTitle(hit, query);
                if (string.IsNullOrWhiteSpace(title))
                    title = ExtractReadablePartialPlanningLeadTitle(hit, query);
                if (string.IsNullOrWhiteSpace(title))
                    title = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 120);
                if (string.IsNullOrWhiteSpace(title))
                    title = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);

                var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                var candidate = new SourceBackedOptionCandidate(
                    hit,
                    title,
                    ComputeSourceBackedOptionHitScore(hit, title, query, requestedMaxMinutes: null, visibleMinutes),
                    visibleMinutes);
                var candidateKey = BuildSourceBackedPlanningCandidateKey(candidate);
                var rejectionReason = string.IsNullOrWhiteSpace(title)
                    ? "missing_candidate_title"
                    : ExplainSourceBackedPlanningCandidateRejection(
                        candidate,
                        query,
                        requireDirectPageEvidence,
                        requireStrictStructuredEvidence: false,
                        dominantTopLevelScope);
                return new
                {
                    Candidate = candidate,
                    CandidateKey = candidateKey,
                    RejectionReason = rejectionReason,
                    EvidenceRichness = ComputeSourceBackedEvidenceRichnessScore(hit)
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Candidate.Title))
            .Where(item => !string.IsNullOrWhiteSpace(item.CandidateKey) && seen.Add(item.CandidateKey))
            .OrderByDescending(static item => !string.IsNullOrWhiteSpace(item.RejectionReason) ? 1 : 0)
            .ThenByDescending(static item => item.EvidenceRichness)
            .ThenByDescending(static item => item.Candidate.Hit.Score)
            .Take(maxAnnotatedCandidates)
            .ToList();

        foreach (var item in annotated)
        {
            var rejected = !string.IsNullOrWhiteSpace(item.RejectionReason);
            AddAnnotatedSourceBackedCandidateLeadLine(
                lines,
                rejected ? "candidate_rejected" : "candidate_not_selected",
                item.Candidate,
                query,
                language,
                rejected ? "rejected_by_code" : "not_selected_by_code",
                rejected ? item.RejectionReason : "outside_strict_candidate_quota");
        }
    }

    private static void AddAnnotatedSourceBackedCandidateLeadLine(
        List<string> lines,
        string role,
        SourceBackedOptionCandidate candidate,
        string query,
        string language,
        string codeDecision,
        string codeReason)
    {
        var hit = candidate.Hit;
        var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        if (string.IsNullOrWhiteSpace(source))
            source = hit.DocPath;

        var supportCue = BuildSourceBackedCandidateSupportCue(hit);
        if (string.IsNullOrWhiteSpace(supportCue))
            supportCue = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 120);
        var supportSuffix = string.IsNullOrWhiteSpace(supportCue)
            ? string.Empty
            : $"; evidence=\"{CollapseWhitespace(supportCue)}\"";
        var retrievalQuery = TruncateForPrompt(CollapseWhitespace(hit.RetrievalQuery ?? string.Empty), 90);
        var pageKey = BuildRagHitVisiblePageMergeKey(hit);
        var citation = BuildInlineSourceCitationForWriter(source, hit.PageStart, language);
        var candidateKey = BuildSourceBackedPlanningCandidateLeadKey(candidate);
        var slotMetadata = BuildSourceBackedCandidateSlotMetadataForInventory(candidate, query, language);
        var evidenceId = BuildWriterEvidenceItemId(lines.Count);
        lines.Add(
            $"EVIDENCE_ITEM id=\"{evidenceId}\" role=\"{role}\" title=\"{CollapseWhitespace(candidate.Title)}\" source=\"{source}\" page=\"{Math.Max(1, hit.PageStart)}\" citation=\"{citation}\" pageKey=\"{pageKey}\" candidateKey=\"{candidateKey}\" slotRoute=\"{slotMetadata.Route}\" slotFit=\"{slotMetadata.Fit}\" retrievalQuery=\"{retrievalQuery}\" codeDecision=\"{codeDecision}\" codeReason=\"{codeReason}\" instruction=\"code diagnostic only; LLM must judge usefulness from source evidence before final use\"{supportSuffix}");
    }

    private static string BuildWriterEvidenceItemId(int existingLineCount)
        => $"E{Math.Clamp(existingLineCount + 1, 1, 999)}";
}
