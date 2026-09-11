using System.Globalization;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static SourceBackedPlanningCoverage EvaluateSourceBackedPlanningCoverage(
        ToolResults toolResults,
        string? query,
        string language)
    {
        language = NormalizeLanguageCode(language);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
        var dayAxis = DetectRequestedDayAxisLabels(query, language);
        var periodAxis = DetectRequestedPlanningSlotAxisLabels(query, language);
        var hasStructuredAxes = dayAxis.Count > 0 && periodAxis.Count > 0;
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(query, targetSlots, hasStructuredAxes);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var candidatePoolSize = strictStructuredPlanning
            ? ResolveSourceBackedPlanningCandidatePoolSize(query, Math.Max(targetSlots, minimumCandidates))
            : Math.Max(20, targetSlots);
        ClientLog.Info(
            "ToolAgent planning coverage evaluate: trace_path=rag.planning.coverage|trace_step=source_inventory.start|stage=source_inventory.start"
            + $"|targetSlots={targetSlots}|minimumCandidates={minimumCandidates}|poolSize={candidatePoolSize}|strict={strictStructuredPlanning}|structuredAxes={hasStructuredAxes}");
        var coverageStopwatch = Stopwatch.StartNew();
        var sourcePages = SelectSourceBackedPlanningCoverageSourcePages(
                toolResults,
                query,
                candidatePoolSize)
            .ToList();
        ClientLog.Info(
            "ToolAgent planning coverage evaluate: trace_path=rag.planning.coverage|trace_step=source_inventory.selected|stage=source_inventory.selected"
            + $"|sourcePages={sourcePages.Count}|ms={coverageStopwatch.ElapsedMilliseconds}|topPages={string.Join("; ", sourcePages.Take(8).Select(hit => BuildSourceBackedPlanningRuntimeTraceTitle(hit, query)))}");
        ClientLog.Info(
            "ToolAgent planning coverage evaluate: trace_path=rag.planning.coverage|trace_step=source_inventory.end|stage=source_inventory.end"
            + $"|sourcePages={sourcePages.Count}|totalMs={coverageStopwatch.ElapsedMilliseconds}");

        var candidateSelectionLimit = Math.Max(1, Math.Max(targetSlots, minimumCandidates));
        var acceptedCandidates = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                candidateSelectionLimit,
                language)
            .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static candidate => candidate.Score)
                .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
                .ThenByDescending(static candidate => candidate.Hit.Score)
                .First())
            .ToList();
        ClientLog.Info(
            "ToolAgent planning coverage evaluate: trace_path=rag.planning.coverage|trace_step=candidate_bank.selected|stage=candidate_bank.selected"
            + $"|acceptedCandidates={acceptedCandidates.Count}|candidateLimit={candidateSelectionLimit}|ms={coverageStopwatch.ElapsedMilliseconds}|topCandidates={string.Join("; ", acceptedCandidates.Take(8).Select(static candidate => candidate.Title))}");

        var distinctCandidateCount = acceptedCandidates.Count;
        var distinctSourcePages = acceptedCandidates
            .Select(static candidate => BuildRagHitVisiblePageMergeKey(candidate.Hit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var requiredSourcePageCount = strictStructuredPlanning
            ? Math.Min(targetSlots, Math.Max(1, minimumCandidates))
            : RequiresFullyDistinctStructuredPlanningItems(query)
            ? Math.Min(3, minimumCandidates)
            : 1;
        var richEvidenceCount = acceptedCandidates.Count(static candidate => HasRichSourceBackedEvidence(candidate.Hit));
        var evidenceRichnessScore = acceptedCandidates.Sum(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit));
        var hasSourceInventory = sourcePages.Count > 0;
        var hasAcceptedCandidateInventory = distinctCandidateCount > 0 && distinctSourcePages > 0;
        var hasRequiredAnchor = HasSourceBackedPlanningAnchorCoverage(toolResults, query)
            || (strictStructuredPlanning && hasAcceptedCandidateInventory);
        var structuredSlotFit = StructuredPlanningSlotFitSummary.Empty;
        if (hasStructuredAxes)
        {
            _ = BuildStructuredSourceBackedSlotAwareGrid(
                acceptedCandidates,
                periodAxis,
                targetSlots,
                query,
                allowSourcedRotation: false,
                requireDistinctItems: true,
                out structuredSlotFit);
            ClientLog.Info(
                "ToolAgent planning coverage evaluate: trace_path=rag.planning.coverage|trace_step=slot_fit|stage=slot_fit"
                + $"|assignedSlots={structuredSlotFit.AssignedSlots}|requiredSlots={targetSlots}"
                + $"|routeEvidence={FormatPlanningTraceBool(structuredSlotFit.HasRouteEvidence)}"
                + $"|routedPool={structuredSlotFit.RoutedPool}|neutralPool={structuredSlotFit.NeutralPool}");
        }

        var requiresExplicitSlotEvidence = hasStructuredAxes && RequiresExplicitStructuredPlanningSlotEvidence(query);
        var hasStructuredSlotShortage = hasStructuredAxes
            && ((structuredSlotFit.HasRouteEvidence && structuredSlotFit.AssignedSlots < targetSlots)
                || (requiresExplicitSlotEvidence && !structuredSlotFit.HasRouteEvidence));
        var isAdequate = distinctCandidateCount >= minimumCandidates
            && distinctSourcePages >= requiredSourcePageCount
            && hasRequiredAnchor
            && (richEvidenceCount > 0 || evidenceRichnessScore > 0 || strictStructuredPlanning)
            && !hasStructuredSlotShortage;
        var score = Math.Min(distinctCandidateCount, minimumCandidates) * 10
            + Math.Min(distinctSourcePages, requiredSourcePageCount) * 4
            + (hasRequiredAnchor ? 8 : 0)
            + (richEvidenceCount * 3)
            + Math.Min(16, evidenceRichnessScore)
            + (isAdequate ? 20 : 0);

        return new SourceBackedPlanningCoverage(
            distinctCandidateCount,
            distinctSourcePages,
            minimumCandidates,
            targetSlots,
            hasRequiredAnchor,
            richEvidenceCount,
            evidenceRichnessScore,
            isAdequate,
            score);
    }

    private static IReadOnlyList<RagHitSummary> SelectSourceBackedPlanningCoverageSourcePages(
        ToolResults toolResults,
        string? query,
        int maxItems)
    {
        if (maxItems <= 0)
            return Array.Empty<RagHitSummary>();

        var normalizedQuery = query ?? string.Empty;
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        return EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, normalizedQuery))
            .Where(hit => !strictStructuredPlanning
                || ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, normalizedQuery))
            .OrderByDescending(static hit => BackendSelectionHintsPreferUsableEvidence(hit) ? 1 : 0)
            .ThenByDescending(ComputeSourceBackedEvidenceRichnessScore)
            .ThenByDescending(static hit => hit.Score)
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(maxItems)
            .ToArray();
    }

    private static bool ShouldExposeHitForStrictSourceBackedPlanningInventory(
        RagHitSummary hit,
        string? query)
    {
        var title = BuildSourceBackedPlanningRuntimeTraceTitle(hit);
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[]
            {
                title,
                hit.SectionTitle,
                hit.HeadingPath,
                hit.Excerpt,
                hit.FullText,
                hit.ContextualSnippet
            }.Where(static part => !string.IsNullOrWhiteSpace(part)))));
        if (hit.MatchedContentCards is not { Count: > 0 }
            && LooksLikeExplicitlyNonConcreteSourceEvidence(normalizedEvidence))
        {
            return false;
        }

        if (!StrictStructuredPlanningRuntimeTitleIsPageGrounded(hit, normalizedTitle)
            && !LooksLikeDocumentaryPlanningContextSourcePage(hit, title, query))
        {
            return HitHasPageGroundedStrictStructuredPlanningInventoryCandidate(hit, query);
        }

        var candidate = new SourceBackedOptionCandidate(
            hit,
            title,
            Score: 0,
            VisibleMinutes: ExtractBestVisibleDurationMinutes(hit));
        if (LooksLikeRequestedPlanningSlotAxisLabelCandidate(candidate, query))
            return HitHasStrictStructuredPlanningInventoryCandidate(hit, query);

        if (LooksLikeDocumentaryPlanningContextSourcePage(hit, title, query))
            return true;

        if (LooksLikeGenericRubricOrTaxonomyOnlySourceHit(hit, title))
            return false;

        if (LooksLikeNoisyStructuredPlanningCandidateTitle(title)
            || LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle))
        {
            return HitHasStrictStructuredPlanningInventoryCandidate(hit, query);
        }

        if (LooksLikeShortNumberedStrictPlanningFragmentTitle(normalizedTitle)
            && hit.MatchedContentCards is not { Count: > 0 })
        {
            return HitHasStrictStructuredPlanningInventoryCandidate(hit, query);
        }

        return true;
    }

    private static bool StrictStructuredPlanningRuntimeTitleIsPageGrounded(
        RagHitSummary hit,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true)
            return true;

        var primaryEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(' ', new[]
        {
            hit.SectionTitle,
            hit.HeadingPath,
            hit.Excerpt,
            hit.FullText,
            GetRagHitStructuredEvidenceText(hit)
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
        if (primaryEvidence.Length < 8)
            return false;

        if (NormalizedLookupContainsWholePhrase(primaryEvidence, normalizedTitle))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return titleTerms.Length >= 2
            && titleTerms.All(term => primaryEvidence.Contains(term, StringComparison.Ordinal));
    }

    private static bool HitHasStrictStructuredPlanningInventoryCandidate(
        RagHitSummary hit,
        string? query)
        => ExtractStrictSourceBackedOptionTitles(hit, query)
            .Any(IsStrictSourceBackedPlanningInventoryCandidateTitle);

    private static bool HitHasPageGroundedStrictStructuredPlanningInventoryCandidate(
        RagHitSummary hit,
        string? query)
        => ExtractStrictSourceBackedOptionTitles(hit, query)
            .Any(title =>
                IsStrictSourceBackedPlanningInventoryCandidateTitle(title)
                && StrictStructuredPlanningRuntimeTitleIsPageGrounded(hit, NormalizeLexicalLookup(title)));

    private static bool IsStrictSourceBackedPlanningInventoryCandidateTitle(string? title)
    {
        var cleaned = CleanSourceBackedOptionTitle(title);
        var normalizedTitle = NormalizeLexicalLookup(cleaned);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return !LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
            && !LooksLikePlanItemNoise(cleaned)
            && !LooksLikeWeakSourceBackedOptionTitle(cleaned)
            && !LooksLikeProcedureSentenceTitle(normalizedTitle)
            && !LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle);
    }

    private static bool LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (normalizedTitle is "ligne" or "line")
            return true;

        return Regex.IsMatch(
                normalizedTitle,
                @"^(?:\d+\s*/\s*)?(?:references?|bibliographie|bibliography|credits?|index|sommaire|contents?|table\s+des\s+matieres)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:les?\s+)?etapes?\s+de\s+la\s+(?:prepa|preparation)\b|^(?:au\s+final|finalement)\b.{0,48}\b(?:vaisselle|cleanup|cleaning|rangement)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:occupez|occuper|occupe)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:profil\s+documentaire|documentary\s+profile|a\s+breviations?|abreviations?|abbreviations?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:mention\s+isolee?s?|ligne\s+isolee?s?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:realisation|preparation)\b$",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^(?:collages?(?:\s+quelques\s+idees?)?|prepares?\s+de\s+temps\s+en\s+temps|on\s+se\s+lance|l\s+avance\s+et\s+sans\s+tracas|quelques\s+\p{L}{3,}\s+de)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedTitle,
                @"^\d+\s+\p{L}{2,12}\b$",
                RegexOptions.CultureInvariant)
            || LooksLikeStandaloneQuantityFragmentTitle(normalizedTitle);
    }

    private static bool LooksLikeDocumentaryPlanningContextSourcePage(
        RagHitSummary hit,
        string? title,
        string? query)
    {
        if (!LooksLikeAnyDocumentaryPlanningRequest(query))
            return false;

        if (hit.MatchedContentCards is { Count: > 0 })
            return false;

        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[]
            {
                title,
                hit.SectionTitle,
                hit.HeadingPath,
                hit.Excerpt,
                hit.FullText,
                hit.ContextualSnippet
            }.Where(static part => !string.IsNullOrWhiteSpace(part)))));
        if (normalizedEvidence.Length < 40)
            return false;

        if (LooksLikeExplicitlyNonConcreteSourceEvidence(normalizedEvidence)
            || LooksLikeGenericRubricOrTaxonomyOnlySourceHit(hit, title))
        {
            return false;
        }

        var titleNamesPlanningContext = Regex.IsMatch(
            normalizedTitle,
            @"\b(?:planning|planification|schedule|calendrier|selection\s+des\s+sources|source\s+selection|evidence\s+selection|source\s+review)\b",
            RegexOptions.CultureInvariant);
        var evidenceDescribesPlanningWork = Regex.IsMatch(
            normalizedEvidence,
            @"\b(?:planning|planification|schedule|organiser|organize|repartir|allocate|selectionner|select|contexte|context|sans\s+inventer|without\s+inventing)\b",
            RegexOptions.CultureInvariant);
        var evidenceMentionsSourceSupport = Regex.IsMatch(
            normalizedEvidence,
            @"\b(?:sources?|preuves?|evidence|contexte|context)\b",
            RegexOptions.CultureInvariant);

        return (titleNamesPlanningContext || evidenceDescribesPlanningWork)
            && evidenceMentionsSourceSupport;
    }

    private static bool LooksLikeGenericRubricOrTaxonomyOnlySourceHit(RagHitSummary hit, string? title)
    {
        if (hit.MatchedContentCards is { Count: > 0 })
            return false;

        var normalizedTitle = NormalizeLexicalLookup(title);
        var normalizedEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[] { title, hit.SectionTitle, hit.HeadingPath, hit.Excerpt, hit.FullText, hit.ContextualSnippet }
                .Where(static part => !string.IsNullOrWhiteSpace(part)))));
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(normalizedEvidence))
        {
            return false;
        }

        if (LooksLikeExplicitlyNonConcreteSourceEvidence(normalizedEvidence))
            return true;

        return Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:rubrique|section|categorie|category|taxonomy|taxonomie|glossaire|glossary|vocabulaire|vocabulary|index|bibliographie|bibliography|credits?|metadata|metadonnees|catalogage)\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:general|generale?|contexte|context|conseils?|advice|definitions?|description|administratif|administrative|catalogage|credits?)\b",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeExplicitlyNonConcreteSourceEvidence(string normalizedEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedEvidence))
            return false;

        if (Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:sans\s+inventer|without\s+inventing|not\s+invent|do\s+not\s+invent|ne\s+pas\s+inventer)\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:sources?|preuves?|evidence|contexte|context|planning|planification|schedule|grounded|documente(?:e|es|s)?|documented)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:pas|sans|not|without|no)\b.{0,100}\b(?:autonome|complete|complet|concrete|concret|utilisable|usable|option|candidate|candidat|preparation|procedure)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:insuffisant(?:e|es|s)?|insufficient|parasite|fragment\s+ocr|ocr\s+fragment|tronque(?:e|es|s)?|truncated|incomplete)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:not|without|no)\b.{0,100}\b(?:standalone|complete|concrete|usable|actionable|candidate|preparation|procedure)\b",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStandaloneQuantityFragmentTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return Regex.IsMatch(
            normalizedTitle,
            @"^\d+(?:[.,]\d+)?\s+(?:(?:petits?|petites?|grands?|grandes?|moyens?|moyennes?|grosses?|gros|small|large|medium)\s+)?[\p{L}\p{N}]{2,24}(?:\s+(?:de|d|du|des|a|au|aux|en|of|for|with)\b.{0,80})?$",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeShortNumberedStrictPlanningFragmentTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var match = Regex.Match(
            normalizedTitle,
            @"^(?<n>\d{1,2})(?:[.,]\d+)?\s+(?<tail>[\p{L}\p{N}]{2,24}(?:\s+[\p{L}\p{N}]{2,24}){0,2})$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 3)
        {
            return false;
        }

        var tail = NormalizeLexicalLookup(match.Groups["tail"].Value);
        if (string.IsNullOrWhiteSpace(tail))
            return false;

        return !Regex.IsMatch(
            tail,
            @"\b(?:procedure|process|inspection|controle|control|verification|validation|audit|test|review|plan|planning|programme|schedule)\b",
            RegexOptions.CultureInvariant);
    }
}
