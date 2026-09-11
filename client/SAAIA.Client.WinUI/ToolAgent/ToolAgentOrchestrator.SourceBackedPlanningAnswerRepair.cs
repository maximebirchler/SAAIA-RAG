using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ShouldRetryStructuredPlanningRepairAfterSourceGuardFailure(string? answer, string? query)
        => ShouldGateStructuredSourceBackedPlanningCoverage(query)
           && !string.IsNullOrWhiteSpace(answer)
           && !LooksLikeWriterControlLeak(answer)
           && !ShouldFallbackFromNoRagDataAnswer(answer);

    private static string BuildStructuredPlanningRepairFeedback(
        string? previousAnswer,
        ToolResults toolResults,
        string? query,
        string language)
    {
        var normalizedLanguage = NormalizeLanguageCode(language);
        var dayLabels = DetectRequestedDayAxisLabels(query, normalizedLanguage);
        var slotLabels = DetectRequestedPlanningSlotAxisLabels(query, normalizedLanguage);
        var sourcePool = BuildCanonicalStructuredPlanningSupportSourcePool(
            toolResults,
            query,
            normalizedLanguage);
        var availableDistinctSourcePages = sourcePool
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        var citedDistinctSourcePages = ReconcileRequiredVisibleSourcesWithFinalAnswer(
                previousAnswer ?? string.Empty,
                sourcePool,
                query)
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        var sb = new StringBuilder();
        sb.AppendLine("Your previous draft was rejected by mechanical source/structure guards. Rewrite from the tool results.");
        if (dayLabels.Count > 0)
            sb.AppendLine($"Requested day axis to mirror as primary sections: {string.Join(" | ", dayLabels)}.");
        if (slotLabels.Count > 0)
            sb.AppendLine($"Requested slot/type axis to show inside the day sections: {string.Join(" | ", slotLabels)}.");
        sb.AppendLine($"Previous draft visible day markers: {CountDistinctVisibleWeekdayMarkers(previousAnswer)}.");
        sb.AppendLine($"Previous draft concrete filled cells: {CountConcreteVisibleStructuredPlanningItems(previousAnswer)}.");
        sb.AppendLine($"Previous draft distinct cited source pages: {citedDistinctSourcePages}; distinct visible source pages available: {availableDistinctSourcePages}; requested places: {ResolveSourceBackedPlanningTargetItemCount(query)}.");
        if (RequiresCompleteStructuredPlanningGrid(query)
            && HasAllRequestedVisibleDayMarkers(previousAnswer, query)
            && CountConcreteVisibleStructuredPlanningItems(previousAnswer) < ResolveSourceBackedPlanningTargetItemCount(query))
        {
            sb.AppendLine("The draft shows the complete requested day axis but does not fill every requested day/slot with a concrete sourced item. If evidence is partial, keep the requested axes but use the exact placeholder \"a completer avec une source utile\" for unsupported cells.");
        }
        if (RequiresCompleteStructuredPlanningGrid(query)
            && HasAllRequestedVisibleDayMarkers(previousAnswer, query)
            && citedDistinctSourcePages < ResolveSourceBackedPlanningTargetItemCount(query))
        {
            sb.AppendLine("The draft shows the complete requested day axis but cites too few distinct source pages for a fully filled grid. Only filled concrete cells need citations; unsupported placeholder cells must stay uncited and non-concrete.");
        }
        if (ContainsStructuredPlanningCompletionPlaceholder(previousAnswer))
        {
            sb.AppendLine("Completion placeholders were detected. They are acceptable only for unsupported cells when the answer is explicitly partial; do not attach citations to placeholders and do not hide concrete items inside them.");
        }
        if (!HasEnoughConcreteVisibleStructuredPlanningItems(previousAnswer, query))
        {
            sb.AppendLine("Too few concrete filled cells were detected. Do not fill cells with generic fragments, bare quantities, isolated labels or only a citation; either write a concrete sourced item/action/value from the evidence or mark the cell to complete with a useful source.");
        }
        if (LooksLikeCitationOnlyStructuredPlanningAnswer(previousAnswer, query))
        {
            sb.AppendLine("Citation-only structured cells were detected. A citation is proof, not item text: invalid examples are \"Slot A : [E1]\" and \"Slot A : (source: file.pdf p.12)\". Rewrite each supported filled cell with a concrete item/action/value before the citation, for example \"Slot A : documented item [E1]\".");
        }
        if (ContainsUnresolvedStructuredPlanningEvidenceIds(previousAnswer))
        {
            sb.AppendLine("Unresolved private evidence ids such as [E20] were detected. Replace every [E#] with one valid id from the current EVIDENCE_ITEM roster or copy its exact visible citation; never leave an unresolved [E#] in the final answer.");
        }
        if (LooksLikeOverCitedStructuredPlanningAnswer(previousAnswer, query))
        {
            sb.AppendLine("Over-cited structured cells were detected. Each filled cell should carry one useful citation only. Do not append a repeated generic source/page after a specific item citation.");
        }
        if (LooksLikeUnsupportedStructuredPlanningRandomization(previousAnswer, query))
        {
            sb.AppendLine("The draft described the plan as random, illustrative, fictive or only a format example. Do not say or imply that: choose only sourced items, or mark unsupported cells as needing a useful source.");
        }
        var repairExamples = BuildStructuredPlanningRepairExamples(sourcePool, query, normalizedLanguage, 8);
        if (repairExamples.Count > 0)
        {
            sb.AppendLine("Concrete source-backed item examples available for repair:");
            foreach (var example in repairExamples)
                sb.AppendLine($"- {example}");
        }
        sb.AppendLine("For each filled structured-plan item, append either the EVIDENCE_ITEM id such as [E1] or copy its citation attribute exactly, for example: item text (source: file.pdf p.12). A filled item without [E#] or a visible citation will be rejected.");
        sb.AppendLine("Use distinct visible source/page items before repeating a file/page. Repeated citations to the same displayed file/page count once.");
        sb.AppendLine("If a place is unsupported, keep the requested day/slot label and mark it to complete with a useful source instead of moving everything under slot-type headings.");
        sb.AppendLine("Do not invent items, quantities, days, or sources. Do not expose private diagnostics.");

        if (!string.IsNullOrWhiteSpace(previousAnswer))
        {
            sb.AppendLine("Previous draft excerpt:");
            sb.AppendLine(TruncateForPrompt(RemoveTrailingModelEmittedSourceList(previousAnswer).Trim(), 1000));
        }

        return sb.ToString().Trim();
    }

    private static IReadOnlyList<string> BuildStructuredPlanningRepairExamples(
        IReadOnlyCollection<ToolMemory.SourceRef> sourcePool,
        string? query,
        string language,
        int limit)
        => sourcePool
            .Select(source => TryBuildVisibleSourceInventoryPlanningItemText(source, query, language, out var title)
                ? $"{title} {BuildVisibleSourceInventoryCitation(source, language)}"
                : string.Empty)
            .Where(static example => !string.IsNullOrWhiteSpace(example))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToArray();

    private static List<ToolMemory.SourceRef> BuildVisibleStructuredPlanningSourcePool(
        ToolResults toolResults,
        string? query)
    {
        var sourceLimit = Math.Clamp(
            Math.Max(ResolveSourceBackedPlanningTargetItemCount(query) * 4, 48),
            48,
            192);
        var sources = new List<ToolMemory.SourceRef>();

        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
        {
            sources.AddRange(EnumerateRagHitSummaries(toolResults)
                .Where(static hit => !string.IsNullOrWhiteSpace(hit.DocPath))
                .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
                .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
                .Where(hit => ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, query))
                .Select(BuildSourceRefFromRagHit));
        }
        else
        {
            sources.AddRange(DeriveSourcesFromRagHits(toolResults));
            if (!string.IsNullOrWhiteSpace(query))
                sources.AddRange(DeriveSourcesFromRankedRagHits(toolResults, query, sourceLimit));

            sources.AddRange(EnumerateRagHitSummaries(toolResults)
                .Where(static hit => !string.IsNullOrWhiteSpace(hit.DocPath))
                .Select(BuildSourceRefFromRagHit));
        }

        var normalizedSources = NormalizeVisibleSourceRefsForMemory(sources);
        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
        {
            normalizedSources = normalizedSources
                .Where(source => ShouldExposeVisibleStructuredPlanningSource(source, query))
                .ToList();
        }

        return normalizedSources.Take(sourceLimit).ToList();
    }

    private static List<ToolMemory.SourceRef> BuildCanonicalStructuredPlanningSupportSourcePool(
        ToolResults toolResults,
        string? query,
        string language)
    {
        var sourceLimit = Math.Clamp(
            Math.Max(ResolveSourceBackedPlanningTargetItemCount(query) * 4, 48),
            48,
            192);
        var sources = new List<ToolMemory.SourceRef>();
        sources.AddRange(BuildVisibleStructuredPlanningSourcePool(toolResults, query));
        if (!string.IsNullOrWhiteSpace(query))
        {
            sources.AddRange(BuildWriterEvidenceRosterSourcePoolForCitationMapping(
                toolResults,
                query,
                language));
        }

        return NormalizeVisibleSourceRefsForMemory(sources)
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(sourceLimit)
            .ToList();
    }

    private static SourceBackedPlanningDraft BuildVisibleSourceInventoryStructuredPlanningDraft(
        ToolResults toolResults,
        string language,
        string? query)
    {
        language = NormalizeLanguageCode(language);
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return SourceBackedPlanningDraft.Empty;

        var dayLabels = DetectRequestedDayAxisLabels(query, language);
        var slotLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
        if (dayLabels.Count == 0 || slotLabels.Count == 0)
            return SourceBackedPlanningDraft.Empty;

        var targetSlots = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(query));
        var visibleSources = BuildVisibleStructuredPlanningSourcePool(toolResults, query)
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(source => new
            {
                Source = source,
                Title = TryBuildVisibleSourceInventoryPlanningItemText(source, query, language, out var title)
                    ? title
                    : string.Empty
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Title))
            .Take(targetSlots)
            .ToList();
        if (visibleSources.Count == 0)
            return SourceBackedPlanningDraft.Empty;
        if (RequiresCompleteStructuredPlanningGrid(query)
            && visibleSources.Count < targetSlots)
        {
            return SourceBackedPlanningDraft.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(language switch
        {
            "en" => "Here is a source-backed structured planning draft:",
            "es" => "Aqui tienes un borrador de plan estructurado con fuentes:",
            "pt" => "Aqui esta um rascunho de plano estruturado com fontes:",
            "de" => "Hier ist ein quellenbasierter strukturierter Planentwurf:",
            "it" => "Ecco una bozza di piano strutturato con fonti:",
            _ => "Voici une proposition structuree appuyee sur les sources disponibles :"
        });
        sb.AppendLine();

        var sourceIndex = 0;
        foreach (var day in dayLabels)
        {
            sb.AppendLine($"{day} :");
            foreach (var slot in slotLabels)
            {
                sb.Append("- ");
                sb.Append(slot);
                sb.Append(" : ");
                if (sourceIndex < visibleSources.Count)
                {
                    var item = visibleSources[sourceIndex++];
                    sb.Append(item.Title);
                    sb.Append(' ');
                    sb.Append(BuildVisibleSourceInventoryCitation(item.Source, language));
                }
                else
                {
                    sb.Append(language switch
                    {
                        "en" => "to complete with a useful source",
                        "es" => "a completar con una fuente util",
                        "pt" => "a completar com uma fonte util",
                        "de" => "mit einer brauchbaren Quelle zu ergaenzen",
                        "it" => "da completare con una fonte utile",
                        _ => "a completer avec une source utile"
                    });
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        return new SourceBackedPlanningDraft(
            sb.ToString().TrimEnd(),
            Array.Empty<SourceBackedOptionCandidate>(),
            visibleSources.Select(static item => item.Source).ToArray());
    }

    private static bool TryBuildVisibleSourceInventoryPlanningItemText(
        ToolMemory.SourceRef source,
        string? query,
        string language,
        out string title)
    {
        title = string.Empty;
        var rawTitle = BuildVisibleStructuredPlanningSourceRosterTitle(source, query ?? string.Empty, language);
        var cleaned = CleanSourceBackedOptionTitle(rawTitle);
        var normalizedTitle = NormalizeLexicalLookup(cleaned);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (LooksLikeRequestedPlanningSlotAxisLabelTitle(cleaned, query)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
            || LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle)
            || LooksLikePlanItemNoise(cleaned)
            || LooksLikeWeakSourceBackedOptionTitle(cleaned)
            || LooksLikeProcedureSentenceTitle(normalizedTitle)
            || LooksLikeStandaloneQuantityFragmentTitle(normalizedTitle))
        {
            return false;
        }

        title = HumanizeSourceBackedDisplayTitle(cleaned);
        return !string.IsNullOrWhiteSpace(title);
    }

    private static string BuildVisibleSourceInventoryCitation(ToolMemory.SourceRef source, string language)
    {
        var visibleSource = string.IsNullOrWhiteSpace(source.DocName)
            ? Path.GetFileName(source.DocPath)
            : source.DocName;
        if (string.IsNullOrWhiteSpace(visibleSource))
            visibleSource = source.Label;
        if (string.IsNullOrWhiteSpace(visibleSource))
            visibleSource = source.DocPath;

        return BuildInlineSourceCitationForWriter(visibleSource, source.PageStart, language);
    }

    private static bool ShouldExposeVisibleStructuredPlanningSource(
        ToolMemory.SourceRef source,
        string? query)
    {
        if (source.MatchedContentCards?
                .Select(static card => CleanSourceBackedOptionTitle(card.Title))
                .Any(IsStrictSourceBackedPlanningInventoryCandidateTitle) == true)
        {
            return true;
        }

        var title = CollapseWhitespace(source.SectionTitle ?? source.HeadingPath ?? source.Label ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = CollapseWhitespace(string.IsNullOrWhiteSpace(source.DocName)
                ? Path.GetFileName(source.DocPath)
                : source.DocName);
        }

        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var supportCue = BuildVisibleStructuredPlanningSourceRosterSupportCue(source);
        var normalizedEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[]
            {
                title,
                source.SectionTitle,
                source.HeadingPath,
                source.Label,
                supportCue
            }.Where(static part => !string.IsNullOrWhiteSpace(part)))));
        if (LooksLikeExplicitlyNonConcreteSourceEvidence(normalizedEvidence))
            return false;

        return !LooksLikeRequestedPlanningSlotAxisLabelTitle(title, query)
            && !LooksLikeNoisyStructuredPlanningCandidateTitle(title)
            && !LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle)
            && !LooksLikePlanItemNoise(title)
            && !LooksLikeWeakSourceBackedOptionTitle(title)
            && !LooksLikeProcedureSentenceTitle(normalizedTitle);
    }

    private static PlanningAnswerSupportAnalysis BuildTrustedSourceBackedPlanningDraftAnalysis(
        SourceBackedPlanningDraft draft,
        string? query)
    {
        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        var itemCount = Math.Max(draft.Items.Count, targetItemCount);
        var supportedItemCount = Math.Max(draft.Items.Count, Math.Min(targetItemCount, draft.Sources.Count));
        supportedItemCount = Math.Clamp(supportedItemCount, 0, itemCount);

        return new PlanningAnswerSupportAnalysis(
            itemCount,
            supportedItemCount,
            itemCount - supportedItemCount,
            CandidateCount: Math.Max(draft.Items.Count, draft.Sources.Count),
            Sources: draft.Sources);
    }

    private static PlanningAnswerSupportAnalysis BuildTrustedPartialSourceBackedPlanningDraftAnalysis(SourceBackedPlanningDraft draft)
    {
        var itemCount = Math.Max(draft.Items.Count, draft.Sources.Count);
        var supportedItemCount = Math.Min(itemCount, draft.Sources.Count);
        return new PlanningAnswerSupportAnalysis(
            itemCount,
            supportedItemCount,
            itemCount - supportedItemCount,
            CandidateCount: Math.Max(itemCount, draft.Sources.Count),
            Sources: draft.Sources);
    }

    private static bool HasTrustedSourceBackedPlanningDraftCoverage(SourceBackedPlanningDraft draft, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return false;

        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        return !string.IsNullOrWhiteSpace(draft.Answer)
            && draft.Items.Count >= targetItemCount
            && draft.Sources.Count >= targetItemCount;
    }

    private static bool HasTrustedPartialSourceBackedPlanningDraftCoverage(
        SourceBackedPlanningDraft draft,
        SourceBackedPlanningCoverage coverage,
        string? query,
        bool searchWasBroadened,
        bool searchWasExpanded)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || coverage.IsAdequate
            || string.IsNullOrWhiteSpace(draft.Answer)
            || draft.Items.Count <= 0
            || draft.Sources.Count <= 0
            || draft.Sources.Count < Math.Min(3, draft.Items.Count))
        {
            return false;
        }

        return HasUsefulPartialSourceBackedPlanningCoverage(
            coverage,
            searchWasBroadened,
            searchWasExpanded);
    }

    private static bool HasEnoughPostRepairPartialStructuredPlanningDraftCoverage(
        SourceBackedPlanningDraft draft,
        string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(draft.Answer)
            || draft.Sources.Count <= 0)
        {
            return false;
        }

        var targetItemCount = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(query));
        var minimumPartialSources = Math.Min(
            targetItemCount,
            Math.Min(8, Math.Max(3, (int)Math.Ceiling(targetItemCount * 0.30d))));
        var distinctSourceCount = draft.Sources
            .Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var itemOrSourceCount = Math.Max(draft.Items.Count, draft.Sources.Count);
        if (RequiresCompleteStructuredPlanningGrid(query))
        {
            return itemOrSourceCount >= targetItemCount
                && draft.Sources.Count >= targetItemCount
                && distinctSourceCount >= targetItemCount
                && !ContainsStructuredPlanningCompletionPlaceholder(draft.Answer);
        }

        return itemOrSourceCount >= minimumPartialSources
            && draft.Sources.Count >= minimumPartialSources
            && distinctSourceCount >= minimumPartialSources;
    }

    private static bool RequiresCompleteStructuredPlanningGrid(string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return false;

        return HasDetectedPlanningAxes(query, "fr")
            || HasDetectedPlanningAxes(query, "en")
            || HasDetectedPlanningAxes(query, "es")
            || HasDetectedPlanningAxes(query, "pt")
            || HasDetectedPlanningAxes(query, "de")
            || HasDetectedPlanningAxes(query, "it");

        static bool HasDetectedPlanningAxes(string? text, string language)
            => DetectRequestedDayAxisLabels(text, language).Count > 0
               && DetectRequestedPlanningSlotAxisLabels(text, language).Count > 0;
    }

    private static bool ContainsStructuredPlanningCompletionPlaceholder(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:a\s+completer|a\s+compl[eÃ©]ter|to\s+complete|da\s+completare|a\s+completar|zu\s+ergaenzen|utile\s+source|source\s+utile|useful\s+source)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool TryBuildSupportedStructuredPlanningAnswer(
        ToolResults toolResults,
        string language,
        string? query,
        out string answer,
        out List<ToolMemory.SourceRef> sources,
        out PlanningAnswerSupportAnalysis analysis,
        bool allowPartialStructuredPlanningRebuild = false)
    {
        answer = string.Empty;
        sources = new List<ToolMemory.SourceRef>();
        analysis = PlanningAnswerSupportAnalysis.Empty;

        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return false;

        var draft = BuildSourceBackedPlanningDraft(
            toolResults,
            language,
            minItems: ResolveSourceBackedPlanningTargetItemCount(query),
            query: query);
        answer = draft.Answer;
        if (!string.IsNullOrWhiteSpace(answer) && draft.Sources.Count > 0)
        {
            if (HasTrustedSourceBackedPlanningDraftCoverage(draft, query))
            {
                sources = draft.Sources.ToList();
                analysis = BuildTrustedSourceBackedPlanningDraftAnalysis(draft, query);
                return true;
            }

            analysis = AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language);
            if (!ShouldRejectUnsupportedPlanningAnswerForFinal(analysis, query)
                && analysis.Sources.Count > 0)
            {
                sources = analysis.Sources.ToList();
                return true;
            }
        }

        if (!allowPartialStructuredPlanningRebuild)
            return false;

        var partialDraft = BuildSourceBackedPlanningDraft(
            toolResults,
            language,
            minItems: 1,
            query: query,
            allowPartialStructuredPlanningDraft: true);
        if (!HasEnoughPostRepairPartialStructuredPlanningDraftCoverage(partialDraft, query))
        {
            partialDraft = BuildVisibleSourceInventoryStructuredPlanningDraft(
                toolResults,
                language,
                query);
        }
        if (!HasEnoughPostRepairPartialStructuredPlanningDraftCoverage(partialDraft, query))
            return false;

        answer = partialDraft.Answer;
        sources = partialDraft.Sources.ToList();
        analysis = BuildTrustedPartialSourceBackedPlanningDraftAnalysis(partialDraft);
        return true;
    }

    private static bool TryFinalizeSourceBackedPlanningResponse(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language,
        out string finalAnswer,
        out List<ToolMemory.SourceRef> finalSources,
        out PlanningAnswerSupportAnalysis analysis,
        out string resolution,
        bool allowDeterministicStructuredPlanningRebuild = true)
    {
        finalAnswer = RemoveTrailingModelEmittedSourceList(answer ?? string.Empty).Trim();
        finalSources = new List<ToolMemory.SourceRef>();
        analysis = PlanningAnswerSupportAnalysis.Empty;
        resolution = "not_planning";

        if (string.IsNullOrWhiteSpace(query)
            || (!LooksLikeAnyDocumentaryPlanningRequest(query)
                && !ShouldGateStructuredSourceBackedPlanningCoverage(query)))
        {
            return false;
        }

        if (!toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(item.Result)))
            return false;

        var strictPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var documentaryPlanning = LooksLikeAnyDocumentaryPlanningRequest(query);
        if (strictPlanning)
        {
            var writerSupportSw = Stopwatch.StartNew();
            ClientLog.Info("ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=answer_support.start|stage=answer_support.start|strict=True|priority=writer");
            analysis = AnalyzeSourceBackedPlanningAnswerSupport(finalAnswer, toolResults, query, language);
            writerSupportSw.Stop();
            ClientLog.Info(
                "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=answer_support.end|stage=answer_support.end"
                + $"|strict=True|priority=writer|items={analysis.ItemCount}|supported={analysis.SupportedItemCount}|candidates={analysis.CandidateCount}|sources={analysis.Sources.Count}|ms={writerSupportSw.ElapsedMilliseconds}");
            if (analysis.Sources.Count > 0
                && CountVisibleSourceCitationMentionsForStructuredPlanning(finalAnswer) > 0
                && !ShouldRejectUnsupportedPlanningAnswerForFinal(analysis, query))
            {
                finalSources = analysis.Sources.ToList();
                resolution = "structured_planning_supported_writer";
                ClientLog.Info(
                    "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                    + $"|resolution={resolution}|accepted=True|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
                return true;
            }

            if (TryGetVisibleSourceCitedStructuredPlanningSources(
                    answer,
                    toolResults,
                    query,
                    out var visibleCitedSources,
                    language))
            {
                finalSources = visibleCitedSources;
                analysis = new PlanningAnswerSupportAnalysis(
                    Math.Max(ResolveSourceBackedPlanningTargetItemCount(query), ExtractConcretePlanningAnswerItems(finalAnswer).Count()),
                    SupportedItemCount: visibleCitedSources.Count,
                    UnsupportedItemCount: 0,
                    CandidateCount: Math.Max(analysis.CandidateCount, visibleCitedSources.Count),
                    Sources: visibleCitedSources);
                resolution = "structured_planning_visible_cited_writer";
                ClientLog.Info(
                    "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                    + $"|resolution={resolution}|accepted=True|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
                return true;
            }

            if (allowDeterministicStructuredPlanningRebuild)
            {
                var rebuildSw = Stopwatch.StartNew();
                ClientLog.Info("ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=supported_rebuild.start|stage=supported_rebuild.start|strict=True");
                if (TryBuildSupportedStructuredPlanningAnswer(
                        toolResults,
                        language,
                        query,
                        out var rebuiltAnswer,
                        out var rebuiltSources,
                        out var rebuiltAnalysis))
                {
                    rebuildSw.Stop();
                    ClientLog.Info(
                        "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=supported_rebuild.end|stage=supported_rebuild.end"
                        + $"|result=True|items={rebuiltAnalysis.ItemCount}|supported={rebuiltAnalysis.SupportedItemCount}|sources={rebuiltSources.Count}|ms={rebuildSw.ElapsedMilliseconds}");
                    finalAnswer = RemoveTrailingModelEmittedSourceList(rebuiltAnswer).Trim();
                    finalSources = rebuiltSources;
                    analysis = rebuiltAnalysis;
                    resolution = "structured_planning_supported_rebuild";
                    ClientLog.Info(
                        "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                        + $"|resolution={resolution}|accepted=True|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
                    return true;
                }

                rebuildSw.Stop();
                ClientLog.Info(
                    "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=supported_rebuild.end|stage=supported_rebuild.end"
                    + $"|result=False|ms={rebuildSw.ElapsedMilliseconds}");
                ClientLog.Info(
                    "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=partial_supported_rebuild.skipped|stage=partial_supported_rebuild.skipped|strict=True|reason=writer_must_decide_partial_plan");

            }
            else
            {
                ClientLog.Info(
                    "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=supported_rebuild.skipped|stage=supported_rebuild.skipped|strict=True|reason=writer_must_repair_final_answer");
            }

            finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query,
                query,
                analysis.CandidateCount,
                searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(toolResults));
            finalSources = new List<ToolMemory.SourceRef>();
            resolution = "structured_planning_rejected_unsupported";
            ClientLog.Info(
                "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                + $"|resolution={resolution}|accepted=False|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
            return true;
        }

        var nonStrictSupportSw = Stopwatch.StartNew();
        ClientLog.Info("ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=answer_support.start|stage=answer_support.start|strict=False");
        analysis = AnalyzeSourceBackedPlanningAnswerSupport(finalAnswer, toolResults, query, language);
        nonStrictSupportSw.Stop();
        ClientLog.Info(
            "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=answer_support.end|stage=answer_support.end"
            + $"|items={analysis.ItemCount}|supported={analysis.SupportedItemCount}|candidates={analysis.CandidateCount}|sources={analysis.Sources.Count}|ms={nonStrictSupportSw.ElapsedMilliseconds}");
        if (analysis.Sources.Count > 0 && !ShouldRejectUnsupportedPlanningAnswerForFinal(analysis, query))
        {
            finalSources = analysis.Sources.ToList();
            resolution = "planning_supported_final_answer";
            ClientLog.Info(
                "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                + $"|resolution={resolution}|accepted=True|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
            return true;
        }

        if (analysis.Sources.Count > 0 && !documentaryPlanning)
        {
            finalSources = analysis.Sources.ToList();
            resolution = "planning_partial_supported_sources";
            ClientLog.Info(
                "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                + $"|resolution={resolution}|accepted=True|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
            return true;
        }

        if (documentaryPlanning && (analysis.ItemCount > 0 || analysis.CandidateCount > 0))
        {
            finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query,
                query,
                analysis.CandidateCount,
                searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(toolResults));
            finalSources = new List<ToolMemory.SourceRef>();
            resolution = "planning_rejected_partial_supported_sources";
            ClientLog.Info(
                "ToolAgent planning finalizer: trace_path=rag.planning.finalizer|trace_step=result|stage=result"
                + $"|resolution={resolution}|accepted=False|sources={finalSources.Count}|supported={analysis.SupportedItemCount}|unsupported={analysis.UnsupportedItemCount}");
            return true;
        }

        return false;
    }
}
