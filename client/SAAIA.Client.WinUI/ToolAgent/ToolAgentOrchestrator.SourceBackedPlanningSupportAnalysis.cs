using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ShouldRejectUnsupportedPlanningAnswerForFinal(
        PlanningAnswerSupportAnalysis analysis,
        string? query)
    {
        if (analysis.ItemCount <= 0)
            return false;

        var structuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        if (analysis.ItemCount < 2 && !structuredPlanning)
            return false;

        if (analysis.CandidateCount == 0)
            return true;

        if (analysis.UnsupportedItemCount <= 0)
            return false;

        if (structuredPlanning)
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
            return true;

        return analysis.UnsupportedItemCount >= 2
            || analysis.UnsupportedItemCount * 2 >= analysis.ItemCount;
    }

    private static PlanningAnswerSupportAnalysis AnalyzeSourceBackedPlanningAnswerSupport(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || string.IsNullOrWhiteSpace(query)
            || (!LooksLikeAnyDocumentaryPlanningRequest(query)
                && !ShouldGateStructuredSourceBackedPlanningCoverage(query)))
        {
            return PlanningAnswerSupportAnalysis.Empty;
        }

        var structuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var resolvedTargetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        var answerItems = ExtractConcretePlanningAnswerItems(answer).ToList();
        if (answerItems.Count == 0)
        {
            if (!structuredPlanning || !LooksLikeConcreteStructuredPlanningAnswer(answer))
                return PlanningAnswerSupportAnalysis.Empty;

            var requiredItemCount = Math.Max(1, resolvedTargetItemCount);
            return new PlanningAnswerSupportAnalysis(
                requiredItemCount,
                SupportedItemCount: 0,
                UnsupportedItemCount: requiredItemCount,
                CandidateCount: 0,
                Sources: Array.Empty<ToolMemory.SourceRef>());
        }

        var targetItemCount = Math.Max(resolvedTargetItemCount, answerItems.Count);
        var supportCandidateLimit = Math.Clamp(Math.Max(targetItemCount, 24), 8, 64);
        var canonicalSourcePool = structuredPlanning
            ? BuildCanonicalStructuredPlanningSupportSourcePool(toolResults, query, language)
            : new List<ToolMemory.SourceRef>();
        var strictCitationSupportSourcePool = structuredPlanning
            ? !string.IsNullOrWhiteSpace(query)
                ? BuildWriterEvidenceRosterSourcePoolForCitationMapping(
                    toolResults,
                    query,
                    language,
                    allowVisibleSourceFallback: false,
                    allowDiagnosticRows: false)
                : new List<ToolMemory.SourceRef>()
            : new List<ToolMemory.SourceRef>();
        var candidates = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                supportCandidateLimit,
                language)
            .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static candidate => candidate.Score)
                .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
                .ThenByDescending(static candidate => candidate.Hit.Score)
                .First())
            .ToList();
        if (structuredPlanning && strictCitationSupportSourcePool.Count > candidates.Count)
        {
            candidates = AddVisibleStructuredPlanningSupportCandidates(
                candidates,
                toolResults,
                strictCitationSupportSourcePool,
                query,
                language,
                supportCandidateLimit);
        }
        var candidateCount = structuredPlanning
            ? Math.Max(candidates.Count, canonicalSourcePool.Count)
            : candidates.Count;
        (int SupportedItemCount, IReadOnlyList<ToolMemory.SourceRef> Sources) citationSupport = structuredPlanning
            ? CollectVisibleCitedStructuredPlanningItemSupport(answer, strictCitationSupportSourcePool, query)
            : (0, Array.Empty<ToolMemory.SourceRef>());

        if (candidates.Count == 0 && citationSupport.SupportedItemCount == 0)
        {
            return new PlanningAnswerSupportAnalysis(
                answerItems.Count,
                SupportedItemCount: 0,
                UnsupportedItemCount: answerItems.Count,
                CandidateCount: candidateCount,
                Sources: Array.Empty<ToolMemory.SourceRef>());
        }

        var supportedHits = new List<RagHitSummary>();
        var candidateSupportedItemCount = 0;
        if (candidates.Count > 0)
        {
            foreach (var item in answerItems)
            {
                var normalizedItem = NormalizeLexicalLookup(item);
                var itemTerms = ExtractPlanningAnswerSupportTerms(normalizedItem).ToArray();
                if (itemTerms.Length == 0)
                    continue;

                var best = candidates
                    .Where(candidate => PlanningAnswerItemIsSupportedByCandidate(normalizedItem, itemTerms, candidate, requireCandidateTitleMatch: structuredPlanning))
                    .OrderByDescending(static candidate => candidate.Score)
                    .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
                    .ThenByDescending(static candidate => candidate.Hit.Score)
                    .FirstOrDefault();
                if (best is not null)
                {
                    candidateSupportedItemCount++;
                    supportedHits.Add(best.Hit);
                }
            }
        }

        var usableCitationSupport = citationSupport.SupportedItemCount > 0
            && (!structuredPlanning
                || HasEnoughVisibleCitedSourceDiversityForStructuredPlanning(answer ?? string.Empty, query, citationSupport.Sources));
        var citationSupportedItemCount = usableCitationSupport
            ? citationSupport.SupportedItemCount
            : 0;
        var citationSupportedSources = usableCitationSupport
            ? citationSupport.Sources
            : Array.Empty<ToolMemory.SourceRef>();
        var supportedItemCount = Math.Max(
            candidateSupportedItemCount,
            Math.Min(citationSupportedItemCount, answerItems.Count));
        var sources = MergeSourceRefsByPagePreservingOrder(supportedHits
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(BuildSourceRefFromRagHit)
            .Concat(citationSupportedSources))
            .Take(structuredPlanning ? Math.Max(8, targetItemCount) : 8)
            .ToArray();

        return new PlanningAnswerSupportAnalysis(
            answerItems.Count,
            supportedItemCount,
            answerItems.Count - supportedItemCount,
            candidateCount,
            sources);
    }

    private static (int SupportedItemCount, IReadOnlyList<ToolMemory.SourceRef> Sources) CollectVisibleCitedStructuredPlanningItemSupport(
        string? answer,
        IReadOnlyList<ToolMemory.SourceRef> sourcePool,
        string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer)
            || sourcePool.Count == 0)
        {
            return (0, Array.Empty<ToolMemory.SourceRef>());
        }

        var supportedItemCount = 0;
        var sources = new List<ToolMemory.SourceRef>();
        var candidateAnswer = RemoveTrailingModelEmittedSourceList(answer).Trim();
        foreach (var rawLine in SplitStructuredPlanningCellCandidateLines(candidateAnswer))
        {
            var line = CollapseWhitespace(rawLine);
            if (!LooksLikeStructuredPlanningCellLine(line)
                || !StructuredPlanningLineContainsCitationHandle(line))
            {
                continue;
            }

            var content = StripStructuredPlanningCellLabelsAndCitations(line);
            var normalizedContent = NormalizeLexicalLookup(content);
            if (string.IsNullOrWhiteSpace(normalizedContent)
                || Regex.IsMatch(normalizedContent, @"^(?:a completer|to complete|por completar|zu erganzen|da completare|non source|not sourced|sans source|aucune source)", RegexOptions.CultureInvariant)
                || LooksLikeCitationOnlyStructuredPlanningCellContent(content)
                || !ExtractPlanningAnswerSupportTerms(normalizedContent).Any())
            {
                continue;
            }

            var lineSources = ReconcileRequiredVisibleSourcesWithFinalAnswer(line, sourcePool, query)
                .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();
            if (lineSources.Count == 0)
                continue;

            supportedItemCount++;
            sources.AddRange(lineSources);
        }

        return (
            supportedItemCount,
            MergeSourceRefsByPagePreservingOrder(sources).ToArray());
    }

    private static List<SourceBackedOptionCandidate> AddVisibleStructuredPlanningSupportCandidates(
        IReadOnlyList<SourceBackedOptionCandidate> candidates,
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef> canonicalSourcePool,
        string? query,
        string language,
        int maxItems)
    {
        var merged = candidates.ToList();
        if (canonicalSourcePool.Count == 0 || merged.Count >= maxItems)
            return merged;

        var hitByVisibleSourceKey = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !string.IsNullOrWhiteSpace(hit.DocPath))
            .Select(static hit => new
            {
                Key = BuildVisibleSourceCitationDiversityKey(BuildSourceRefFromRagHit(hit)),
                Hit = hit
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static item => ComputeSourceBackedEvidenceRichnessScore(item.Hit))
                    .ThenByDescending(static item => item.Hit.Score)
                    .First()
                    .Hit,
                StringComparer.OrdinalIgnoreCase);

        var existingKeys = merged
            .Select(BuildSourceBackedPlanningCandidateKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingLeadKeys = merged
            .Select(BuildSourceBackedPlanningCandidateLeadKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in canonicalSourcePool)
        {
            if (merged.Count >= maxItems)
                break;

            var sourceKey = BuildVisibleSourceCitationDiversityKey(source);
            if (string.IsNullOrWhiteSpace(sourceKey)
                || !hitByVisibleSourceKey.TryGetValue(sourceKey, out var hit)
                || !TryBuildVisibleSourceInventoryPlanningItemText(source, query, language, out var title))
            {
                continue;
            }

            var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
            var candidate = new SourceBackedOptionCandidate(
                hit,
                title,
                ComputeSourceBackedOptionHitScore(hit, title, query, requestedMaxMinutes: null, visibleMinutes),
                visibleMinutes);
            var candidateKey = BuildSourceBackedPlanningCandidateKey(candidate);
            var leadKey = BuildSourceBackedPlanningCandidateLeadKey(candidate);
            if ((!string.IsNullOrWhiteSpace(candidateKey) && !existingKeys.Add(candidateKey))
                || (!string.IsNullOrWhiteSpace(leadKey) && !existingLeadKeys.Add(leadKey)))
            {
                continue;
            }

            merged.Add(candidate);
        }

        return merged;
    }

    internal static List<ToolMemory.SourceRef> DeriveSourcesFromSupportedPlanningAnswerItemsForTests(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
        => DeriveSourcesFromSupportedPlanningAnswerItems(answer, toolResults, query, language);

    private sealed record PlanningAnswerSupportAnalysis(
        int ItemCount,
        int SupportedItemCount,
        int UnsupportedItemCount,
        int CandidateCount,
        IReadOnlyList<ToolMemory.SourceRef> Sources)
    {
        public static PlanningAnswerSupportAnalysis Empty { get; } = new(
            ItemCount: 0,
            SupportedItemCount: 0,
            UnsupportedItemCount: 0,
            CandidateCount: 0,
            Sources: Array.Empty<ToolMemory.SourceRef>());
    }

    private static IReadOnlyList<string> ExtractConcretePlanningAnswerItems(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return Array.Empty<string>();

        var items = new List<string>();
        var withoutSourceBlock = RemoveTrailingModelEmittedSourceList(answer);
        foreach (var rawLine in withoutSourceBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length < 8
                || line.EndsWith(':')
                || Regex.IsMatch(line, @"^(?:source|sources|note|notes|references?|refs?|fuentes?|fontes?|quellen?|fonti)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || IsPlanningDayAxisOnlyLine(line))
            {
                continue;
            }

            var normalizedLine = NormalizeLexicalLookup(line);
            if (Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:voici|here\s+is|aqui|ecco|hier\s+ist)\b.*\b(?:proposition|proposal|plan|piano|vorschlag)\b",
                    RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:organisation|organization|organizacion|organizacao|organizzazione)\b.*\b(?:assistant|assistante?)\b",
                    RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:tourner|rotate|rotacion|rotacao|alterno|alternance)\b.*\b(?:invent|invente|inventar|erfinden)\b",
                    RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:trouve|found|encontr|gefunden|trov)\b.*\b(?:option|options|element|elements|item|items|candidat|candidates?)\b.*\b(?:creneaux|slots|huecos|espacos|plaetze|spazi|demand)\b",
                    RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:avant|before|antes|prima|vor)\b.*\b(?:verifie|verifier|check|revisa|verifica|prufe)\b.*\b(?:page|pages|source|sources)\b",
                    RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:avant|before|antes|prima|vor)\b.*\b(?:page|pages|source|sources|quantit|timing|horair|constraint|contrainte|restri|remplacement|substitution|alternativ)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (line.EndsWith(':')
                && Regex.IsMatch(
                    normalizedLine,
                    @"\b(?:plan|planning|proposal|proposition|propuesta|proposta|vorschlag|piano|sources?|documents?|fuentes?|fontes?|quellen|fonti)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            line = Regex.Replace(line, @"^\s*(?:[-*\u2022\u25E6]|\d+[.)])\s*", string.Empty, RegexOptions.CultureInvariant).Trim();
            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex >= 0 && colonIndex < Math.Min(32, line.Length - 1))
                line = line[(colonIndex + 1)..].Trim();

            line = Regex.Replace(line, @"\[\[open\|[^\]]+\]\]", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            line = Regex.Replace(line, @"\(\s*\)", string.Empty, RegexOptions.CultureInvariant);
            line = Regex.Replace(line, @"\((?:source|src|ref|referencia|quelle|fonte)\s*:[^)]+\)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            line = Regex.Replace(line, @"\([^)]*\b(?:p\.?|page)\s*\d+[^)]*\)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            line = Regex.Replace(line, @"[*_`#>|]+", string.Empty, RegexOptions.CultureInvariant);
            line = CollapseWhitespace(line.Trim(' ', '.', ';', ':', '-', '\u2013', '\u2014'));

            foreach (var item in SplitConcretePlanningAnswerLine(line))
            {
                var normalized = NormalizeLexicalLookup(item);
                if (normalized.Length < 8
                    || Regex.IsMatch(normalized, @"^(?:a completer|to complete|por completar|zu erganzen|da completare|non source|not sourced|sans source|aucune source)", RegexOptions.CultureInvariant))
                {
                    continue;
                }

                var terms = ExtractPlanningAnswerSupportTerms(normalized).ToArray();
                if (terms.Length == 0)
                    continue;

                items.Add(item);
            }
        }

        return items
            .Take(32)
            .ToArray();
    }

    private static IReadOnlyList<string> SplitConcretePlanningAnswerLine(string line)
    {
        line = CollapseWhitespace(line);
        if (string.IsNullOrWhiteSpace(line))
            return Array.Empty<string>();

        var delimiterCount = Regex.Matches(line, @"\||/|;|\s+-\s+", RegexOptions.CultureInvariant).Count;
        var hasDelimiter = line.Contains('|', StringComparison.Ordinal)
            || line.Contains(" / ", StringComparison.Ordinal)
            || line.Contains(" ; ", StringComparison.Ordinal)
            || line.Contains(" - ", StringComparison.Ordinal);
        var looksLikeCompactPlanLine = IsPlanningDayAxisPrefixedLine(line) || delimiterCount >= 2;
        if (!looksLikeCompactPlanLine || !hasDelimiter)
            return new[] { line };

        var fragments = Regex
            .Split(line, @"\s*(?:\||/|;|\s+-\s+)\s*", RegexOptions.CultureInvariant)
            .Select(CollapseWhitespace)
            .Select(static fragment => fragment.Trim(' ', '.', ';', ':', '-', '\u2013', '\u2014'))
            .Where(static fragment => !string.IsNullOrWhiteSpace(fragment))
            .Where(static fragment => !IsPlanningDayAxisOnlyLine(fragment))
            .ToArray();

        return fragments.Length > 1 ? fragments : new[] { line };
    }

    private static bool IsPlanningDayAxisPrefixedLine(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        return !string.IsNullOrWhiteSpace(normalized)
            && Regex.IsMatch(normalized, @"^(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|lunes|martes|miercoles|jueves|viernes|sabado|domingo|segunda|terca|quarta|quinta|sexta|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|lunedi|martedi|mercoledi|giovedi|venerdi|sabato|domenica)\b", RegexOptions.CultureInvariant);
    }

    private static bool IsPlanningDayAxisOnlyLine(string? value)
    {
        var normalized = NormalizeLexicalLookup(value)?.Trim(' ', ':', '-', '.', ';');
        return !string.IsNullOrWhiteSpace(normalized)
            && Regex.IsMatch(normalized, @"^(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|lunes|martes|miercoles|jueves|viernes|sabado|domingo|segunda|terca|quarta|quinta|sexta|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|lunedi|martedi|mercoledi|giovedi|venerdi|sabato|domenica)$", RegexOptions.CultureInvariant);
    }
    private static bool PlanningAnswerItemIsSupportedByAnyCandidate(
        string item,
        IReadOnlyList<SourceBackedOptionCandidate> candidates)
    {
        var normalizedItem = NormalizeLexicalLookup(item);
        if (string.IsNullOrWhiteSpace(normalizedItem))
            return false;

        var itemTerms = ExtractPlanningAnswerSupportTerms(normalizedItem).ToArray();
        if (itemTerms.Length == 0)
            return false;

        foreach (var candidate in candidates)
        {
            if (PlanningAnswerItemIsSupportedByCandidate(normalizedItem, itemTerms, candidate))
                return true;
        }

        return false;
    }

    private static bool PlanningAnswerItemIsSupportedByCandidate(
        string normalizedItem,
        IReadOnlyList<string> itemTerms,
        SourceBackedOptionCandidate candidate,
        bool requireCandidateTitleMatch = false)
    {
        var title = NormalizeLexicalLookup(candidate.Title);
        if (requireCandidateTitleMatch)
        {
            return StructuredPlanningAnswerItemMatchesCandidateTitle(
                    normalizedItem,
                    itemTerms,
                    title)
                && HasStrictStructuredPlanningCandidateEvidence(candidate)
                && StructuredPlanningItemTermsAreFullySupported(normalizedItem, itemTerms, candidate);
        }

        if (title.Length >= 6
            && string.Equals(normalizedItem, title, StringComparison.Ordinal))
        {
            return StructuredPlanningItemTermsAreFullySupported(normalizedItem, itemTerms, candidate);
        }

        if (title.Length >= 6
            && normalizedItem.Contains(title, StringComparison.Ordinal))
        {
            return StructuredPlanningItemTermsAreFullySupported(normalizedItem, itemTerms, candidate);
        }

        var supportText = NormalizeLexicalLookup(string.Join(' ', EnumeratePlanningCandidateSupportTexts(candidate)));
        if (supportText.Length < 8)
            return false;

        var supportTerms = ExtractPlanningAnswerSupportTerms(supportText).ToHashSet(StringComparer.Ordinal);
        if (supportTerms.Count == 0)
            return false;

        var distinctItemTerms = itemTerms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctItemTerms.Length == 0)
            return false;

        var matchedTerms = distinctItemTerms.Count(supportTerms.Contains);
        if (matchedTerms == 0)
            return false;

        var missingTerms = distinctItemTerms.Length - matchedTerms;
        if (distinctItemTerms.Length <= 6)
            return missingTerms == 0;

        var ratio = matchedTerms / (double)distinctItemTerms.Length;
        if (matchedTerms >= 5 && ratio >= 0.80 && missingTerms <= 2)
            return true;

        return distinctItemTerms.Length == 1
            && distinctItemTerms[0].Length >= 10
            && supportText.Contains(distinctItemTerms[0], StringComparison.Ordinal);
    }

    private static bool StructuredPlanningAnswerItemMatchesCandidateTitle(
        string normalizedItem,
        IReadOnlyList<string> itemTerms,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedItem) || normalizedTitle.Length < 6)
            return false;

        if (string.Equals(normalizedItem, normalizedTitle, StringComparison.Ordinal))
            return true;

        if (normalizedItem.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length == 0)
            return false;

        var itemTermSet = itemTerms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (itemTermSet.Count == 0)
            return false;

        return titleTerms.All(term =>
            itemTermSet.Contains(term)
            || normalizedItem.Contains(term, StringComparison.Ordinal));
    }
}
