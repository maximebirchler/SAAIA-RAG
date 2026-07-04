using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class RetrievalContentClassifier
{
    internal const string ContentRole = "content";
    internal const string NavigationRole = "navigation";
    internal const string MixedNavigationContentRole = "mixed_navigation_content";
    internal const string NavigationChunkType = "navigation_index_v1";

    public static RetrievalChunkClassification ClassifyChunk(string text, string chunkType)
    {
        var signal = AnalyzeChunk(text);
        if (!string.Equals(signal.ContentRole, NavigationRole, StringComparison.Ordinal))
        {
            return new RetrievalChunkClassification(
                signal.ContentRole,
                chunkType,
                signal.NavigationReason,
                OriginalChunkType: null,
                signal.NavigationScore,
                signal.ContentDensityScore);
        }

        return new RetrievalChunkClassification(
            NavigationRole,
            NavigationChunkType,
            signal.NavigationReason,
            chunkType,
            signal.NavigationScore,
            signal.ContentDensityScore);
    }

    public static bool IsNavigationChunkType(string? chunkType)
        => string.Equals(chunkType, NavigationChunkType, StringComparison.Ordinal);

    public static bool IsPredominantlyNavigationContent(
        string? contentRole,
        string? chunkType,
        double navigationScore,
        double contentDensityScore)
    {
        if (string.Equals(contentRole, NavigationRole, StringComparison.Ordinal)
            || IsNavigationChunkType(chunkType))
        {
            return true;
        }

        return string.Equals(contentRole, MixedNavigationContentRole, StringComparison.Ordinal)
               && navigationScore >= 0.70
               && contentDensityScore < 0.55;
    }

    internal static string? DetectNavigationReason(string? text)
    {
        var signal = AnalyzeChunk(text);
        return string.Equals(signal.ContentRole, ContentRole, StringComparison.Ordinal)
            ? null
            : signal.NavigationReason;
    }

    internal static RetrievalNavigationSignal AnalyzeChunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, 0.0);

        var folded = FoldDiacritics(text).ToLowerInvariant();
        var padded = $" {NormalizeForNavigationLookup(folded)} ";
        var hasStrongMarker = HasStrongNavigationMarker(folded, padded);
        var inlinePageNumberBoundaries = CountInlinePageNumberBoundaries(text);
        var shape = AnalyzeShape(text);
        var contentDensityScore = ComputeContentDensityScore(text, folded, shape);
        var looksLikeDenseIndexTermContent = LooksLikeDenseIndexTermContent(text, folded, padded, shape, contentDensityScore);
        var hasListShape = CountBulletMarkers(text) >= 8
            || inlinePageNumberBoundaries >= 5
            || CountShortNumberTokens(text) >= 8
            || shape.DotLeaderLineCount >= 3
            || shape.PageReferenceLineCount >= 5
            || (!looksLikeDenseIndexTermContent && LooksLikeCompactIndexCatalog(text, padded))
            || LooksLikeTitleListChunk(text);
        var hasLayoutIndexArtifact = ContainsLayoutIndexArtifact(folded);
        var hasDenseMeasuredContent = LooksLikeDenseMeasuredContent(text, folded, shape);
        var hasMeasuredSequentialContent = LooksLikeMeasuredSequentialContent(text, shape);
        var looksLikeTechnicalClauseTitleCatalog = LooksLikeTechnicalClauseTitleCatalog(text, folded, shape);
        if (LooksLikeContactDirectoryContent(text, shape))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.68));
        }

        if (LooksLikeTechnicalRevisionNoticeContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.60));
        }

        if (LooksLikeStandardFrontMatterContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.55));
        }

        if (!looksLikeTechnicalClauseTitleCatalog
            && LooksLikeNumberedTechnicalClauseBodyContent(text, folded, shape, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.62));
        }

        if (LooksLikeStandardComplianceReferenceContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.58));
        }

        if (LooksLikeTechnicalReferenceBodyContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.68));
        }

        if (LooksLikeTechnicalDefinitionOrParameterContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.62));
        }

        if (LooksLikeQuestionnaireOrFormExampleContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.60));
        }

        if (LooksLikeTechnicalTopicListContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.55));
        }

        if (LooksLikeTechnicalDiagramOrLegendContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.55));
        }

        if (LooksLikeTechnicalStandardsProseContent(text, folded, shape))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.66));
        }

        if (LooksLikeScheduleOrTimelineBodyContent(text, folded, shape, inlinePageNumberBoundaries, contentDensityScore))
        {
            return new RetrievalNavigationSignal(
                ContentRole,
                null,
                0.0,
                Math.Max(contentDensityScore, 0.60));
        }

        if (looksLikeTechnicalClauseTitleCatalog)
        {
            return new RetrievalNavigationSignal(
                NavigationRole,
                "technical_clause_title_catalog",
                0.84,
                Math.Min(contentDensityScore, 0.35));
        }

        string? reason = null;
        var navigationScore = 0.0;

        var hasExplicitTocMarker = ContainsExplicitTableOfContentsMarker(folded, padded);
        var hasShortTocMarker = ContainsShortTableOfContentsMarker(padded);
        if (hasShortTocMarker && IsStandaloneShortTableOfContentsMarker(padded))
        {
            return new RetrievalNavigationSignal(NavigationRole, "table_of_contents", 0.95, 0.0);
        }

        if (looksLikeDenseIndexTermContent)
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.70));

        if (hasExplicitTocMarker
            || (hasShortTocMarker
                && (inlinePageNumberBoundaries >= 3
                    || CountShortNumberTokens(text) >= 4
                    || shape.PageReferenceLineCount >= 2
                    || shape.DotLeaderLineCount >= 1
                    || hasListShape)))
        {
            reason = "table_of_contents";
            navigationScore = 0.95;
        }

        var looksStructured = LooksLikeStructuredContent(folded);
        if (hasDenseMeasuredContent
            && !hasExplicitTocMarker
            && shape.DotLeaderLineCount == 0
            && shape.PageReferenceLineCount < 2
            && contentDensityScore >= 0.65)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.72));
        }

        if (hasMeasuredSequentialContent
            && !hasExplicitTocMarker
            && !hasShortTocMarker
            && !hasStrongMarker
            && !hasLayoutIndexArtifact)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.70));
        }

        if (reason is null
            && looksStructured
            && !hasListShape
            && !folded.Contains("fiche-index", StringComparison.Ordinal)
            && !folded.Contains("fiche index", StringComparison.Ordinal))
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
        }

        if (reason is null
            && hasLayoutIndexArtifact
            && looksStructured
            && inlinePageNumberBoundaries < 5
            && shape.PageReferenceLineCount < 5)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
        }

        if (reason is null && hasLayoutIndexArtifact && !hasListShape)
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);

        if (reason is null
            && (hasStrongMarker
            || folded.Contains("fiche-index", StringComparison.Ordinal)
            || folded.Contains("fiche index", StringComparison.Ordinal)))
        {
            if (hasStrongMarker
                && !hasListShape
                && !hasExplicitTocMarker
                && !hasShortTocMarker
                && shape.DotLeaderLineCount == 0
                && shape.PageReferenceLineCount == 0
                && inlinePageNumberBoundaries < 7
                && contentDensityScore >= 0.65
                && CountWords(text) >= 28)
            {
                return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
            }

            reason = "explicit_index_marker";
            navigationScore = Math.Max(navigationScore, 0.88);
        }

        if (reason is null && padded.Contains(" index ", StringComparison.Ordinal))
        {
            if (hasListShape)
            {
                reason = "weak_index_marker_with_list_shape";
                navigationScore = Math.Max(navigationScore, 0.76);
            }

            if (reason is null)
                return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
        }

        if (reason is null && hasListShape && inlinePageNumberBoundaries >= 5)
        {
            reason = "inline_page_number_list";
            navigationScore = Math.Max(navigationScore, 0.82);
        }

        if (reason is null && hasListShape && CountShortNumberTokens(text) >= 8)
        {
            reason = "numeric_title_catalog";
            navigationScore = Math.Max(navigationScore, 0.76);
        }

        if (reason is null && shape.DotLeaderLineCount >= 3)
        {
            reason = "title_list_with_page_refs";
            navigationScore = Math.Max(navigationScore, 0.80);
        }

        if (reason is null
            && shape.DotLeaderLineCount >= 1
            && shape.PageReferenceLineCount >= 1
            && CountWords(text) <= 22
            && !hasDenseMeasuredContent
            && !looksStructured)
        {
            reason = "single_title_page_reference";
            navigationScore = Math.Max(navigationScore, 0.86);
        }

        if (reason is null && LooksLikeTitleListChunk(text))
        {
            reason = inlinePageNumberBoundaries >= 3
                ? "compact_title_catalog_with_page_refs"
                : "dense_title_catalog";
            navigationScore = Math.Max(navigationScore, inlinePageNumberBoundaries >= 3 ? 0.78 : 0.76);
        }

        if (reason is null && hasListShape)
        {
            reason = "title_list_shape";
            navigationScore = Math.Max(navigationScore, 0.64);
        }

        if (reason is null)
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);

        if ((hasDenseMeasuredContent || contentDensityScore >= 0.70)
            && navigationScore < 0.90
            && !hasExplicitTocMarker
            && !hasStrongMarker
            && shape.DotLeaderLineCount == 0
            && shape.PageReferenceLineCount < 2
            && inlinePageNumberBoundaries < 3)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.72));
        }

        if (shape.ShortLineRatio >= 0.65 && shape.PageReferenceLineCount >= 3)
            navigationScore = Math.Max(navigationScore, 0.82);
        if (shape.LongLineRatio >= 0.35 || looksStructured)
            contentDensityScore = Math.Max(contentDensityScore, looksStructured ? 0.70 : 0.50);
        if (!looksStructured
            && (inlinePageNumberBoundaries >= 5
                || (shape.LongLineRatio < 0.30
                    && (shape.DotLeaderLineCount >= 3 || shape.PageReferenceLineCount >= 5))))
        {
            contentDensityScore = Math.Min(contentDensityScore, 0.35);
        }
        if (contentDensityScore >= 0.55 && navigationScore < 0.90)
            navigationScore = Math.Min(navigationScore, 0.69);

        var role = navigationScore >= 0.90 || (navigationScore >= 0.72 && contentDensityScore < 0.50)
            ? NavigationRole
            : navigationScore >= 0.55
                ? MixedNavigationContentRole
                : ContentRole;

        return new RetrievalNavigationSignal(
            role,
            string.Equals(role, ContentRole, StringComparison.Ordinal) ? null : reason,
            Math.Clamp(navigationScore, 0.0, 1.0),
            Math.Clamp(contentDensityScore, 0.0, 1.0));
    }

    private static bool LooksLikeStructuredContent(string foldedText)
    {
        var hasItemizedSection = StructuredContentLexicon.ContainsItemizedCue(foldedText)
            || ContainsAny(foldedText, "resources", "ressources");
        var hasProcedureSection = StructuredContentLexicon.ContainsRetrievalProcedureCue(foldedText);
        var hasGovernanceSection = StructuredContentLexicon.ContainsGovernanceCue(foldedText);
        var hasCountOrSteps = CountOrStepMarkerRegex().IsMatch(foldedText)
            || CountNumberedSteps(foldedText) >= 2;

        return (hasItemizedSection && (hasProcedureSection || hasCountOrSteps))
            || (hasProcedureSection && hasCountOrSteps)
            || (hasGovernanceSection && hasCountOrSteps);
    }

    internal static bool LooksLikeMeasuredSequentialContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return LooksLikeMeasuredSequentialContent(text, AnalyzeShape(text));
    }

    private static bool LooksLikeMeasuredSequentialContent(
        string text,
        RetrievalNavigationShape shape)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 2)
            return false;

        if (CountWords(text) < 50)
            return false;

        if (CountMeasurementOrSpecificationTokens(text) < 4)
            return false;

        return CountInlineOrdinalBodyMarkers(text) >= 3;
    }

    private static bool LooksLikeTechnicalStandardsProseContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape)
    {
        if (shape.DotLeaderLineCount >= 3 || shape.PageReferenceLineCount >= 3)
            return false;

        if (CountWords(text) < 70)
            return false;

        var standardReferenceCount = TechnicalStandardReferenceRegex().Matches(text).Count;
        if (standardReferenceCount < 3)
            return false;

        var sentencePunctuationCount = text.Count(static ch => ch is '.' or '!' or '?' or ';');
        if (sentencePunctuationCount < 4)
            return false;

        var proseCueCount = TechnicalStandardsProseCueRegex().Matches(foldedText).Count;
        return proseCueCount >= 4;
    }

    private static bool LooksLikeTechnicalReferenceBodyContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount >= 3 || shape.PageReferenceLineCount >= 3)
            return false;

        var words = CountWords(text);
        if (words < 25)
            return false;

        var standardReferenceCount = TechnicalStandardReferenceRegex().Matches(text).Count;
        var technicalCueCount = TechnicalReferenceBodyCueRegex().Matches(foldedText).Count;
        if (technicalCueCount < 2 && standardReferenceCount < 2)
            return false;

        var punctuationCount = text.Count(static ch => ch is '.' or ',' or ';' or ':' or '(' or ')');
        var hasBodyShape = words >= 45 && punctuationCount >= 3;
        var hasDenseTechnicalReferences = standardReferenceCount >= 2 && technicalCueCount >= 2;
        var hasReferenceTableBody = standardReferenceCount >= 4 && technicalCueCount >= 1;
        var measurementCount = CountMeasurementOrSpecificationTokens(text);
        var hasShortTechnicalBody = words >= 24
            && technicalCueCount >= 5
            && punctuationCount >= 1
            && ContainsAny(
                foldedText,
                " anwendung",
                " zuganglichkeit",
                " nachweis",
                " pruefung",
                " prufung",
                " maximum ",
                " pressure ");
        if (standardReferenceCount == 0 && measurementCount < 2 && words < 45 && !hasShortTechnicalBody)
            return false;

        var hasTechnicalBody = technicalCueCount >= 5
            && (hasBodyShape
                || measurementCount >= 2
                || (contentDensityScore >= 0.55 && punctuationCount >= 2));

        return hasReferenceTableBody
            || hasShortTechnicalBody
            || hasTechnicalBody
            || (hasDenseTechnicalReferences
                && (hasBodyShape
                    || inlinePageNumberBoundaries >= 3
                    || measurementCount >= 1
                    || contentDensityScore >= 0.45));
    }

    private static bool LooksLikeQuestionnaireOrFormExampleContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 2 || inlinePageNumberBoundaries >= 3)
            return false;

        var words = CountWords(text);
        if (words < 20)
            return false;

        var cueCount = QuestionnaireOrFormExampleCueRegex().Matches(foldedText).Count;
        if (cueCount < 4)
            return false;

        return contentDensityScore >= 0.45
            || ContainsAny(
                foldedText,
                " context ",
                " participant ",
                " respondents ",
                " test administration ",
                " good answer ",
                " poor answer ");
    }

    private static bool LooksLikeTechnicalDefinitionOrParameterContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 3 || inlinePageNumberBoundaries >= 12)
            return false;

        var words = CountWords(text);
        if (words < 28)
            return false;

        var definitionLeadCount = TechnicalClauseDefinitionLeadRegex().Matches(text).Count;
        var definitionCueCount = TechnicalDefinitionCueRegex().Matches(foldedText).Count;
        var parameterCueCount = TechnicalParameterCueRegex().Matches(foldedText).Count;
        var hasCorrelationTable = (foldedText.StartsWith("correlation between ", StringComparison.Ordinal)
                || foldedText.Contains(" correlation between ", StringComparison.Ordinal))
            && definitionCueCount >= 5;
        if (definitionCueCount < 3 || (parameterCueCount < 4 && !hasCorrelationTable))
            return false;

        var punctuationCount = text.Count(static ch => ch is '.' or ',' or ';' or ':' or '(' or ')' or '-' or '\u2013' or '\u2014');
        var hasDefinitionShape = definitionLeadCount >= 1
            || foldedText.Contains(" maximum ", StringComparison.Ordinal)
            || foldedText.Contains(" minimum ", StringComparison.Ordinal)
            || foldedText.Contains(" examples of ", StringComparison.Ordinal)
            || foldedText.Contains(" extracted from ", StringComparison.Ordinal)
            || foldedText.Contains(" comparison is shown ", StringComparison.Ordinal)
            || foldedText.StartsWith("correlation between ", StringComparison.Ordinal)
            || foldedText.Contains(" correlation between ", StringComparison.Ordinal);

        return hasDefinitionShape
            && (punctuationCount >= 4
                || contentDensityScore >= 0.35
                || inlinePageNumberBoundaries >= 2);
    }

    private static bool LooksLikeTechnicalTopicListContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 2 || inlinePageNumberBoundaries >= 3)
            return false;

        var words = CountWords(text);
        if (words < 28)
            return false;

        var technicalCueCount = TechnicalReferenceBodyCueRegex().Matches(foldedText).Count;
        var topicCueCount = TechnicalTopicListCueRegex().Matches(foldedText).Count;
        if (technicalCueCount < 4 || topicCueCount < 4)
            return false;

        return contentDensityScore >= 0.30
            || ContainsAny(
                foldedText,
                " signal word ",
                " safety message ",
                " collateral materials ",
                " product safety ");
    }

    private static bool LooksLikeTechnicalDiagramOrLegendContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 2 || inlinePageNumberBoundaries >= 8)
            return false;

        var words = CountWords(text);
        if (words < 24)
            return false;

        var electricalLegendSupplementCueCount = TechnicalElectricalLegendSupplementCueRegex().Matches(foldedText).Count;
        var legendCueCount = TechnicalDiagramLegendCueRegex().Matches(foldedText).Count
            + electricalLegendSupplementCueCount;
        var combinedCueCount = TechnicalReferenceBodyCueRegex().Matches(foldedText).Count + legendCueCount;
        if (combinedCueCount < 5 || legendCueCount < 4)
            return false;

        var referenceCodeCount = TechnicalDiagramReferenceCodeRegex().Matches(text).Count;
        var hasFigureOrTableCue = ContainsAny(foldedText, " figure ", " fig ", " table ", " tableau ", " bild ");
        var hasCompactElectricalLegend = electricalLegendSupplementCueCount >= 3
            && ContainsAny(
                foldedText,
                " conductor ",
                " neutral ",
                " grounded ",
                " grounding ");
        if (contentDensityScore >= 0.70 && !hasFigureOrTableCue && !hasCompactElectricalLegend)
            return false;

        var hasDenseLegendVocabulary = legendCueCount >= 6
            && ContainsAny(
                foldedText,
                " control ",
                " switch ",
                " relay ",
                " circuit ",
                " enclosure ",
                " statement ",
                " hazard ",
                " symbol ",
                " resistance ");

        return referenceCodeCount >= 3
            || hasFigureOrTableCue
            || hasCompactElectricalLegend
            || hasDenseLegendVocabulary
            || contentDensityScore >= 0.45;
    }

    private static bool LooksLikeTechnicalClauseTitleCatalog(
        string text,
        string foldedText,
        RetrievalNavigationShape shape)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 2)
            return false;

        var words = CountWords(text);
        if (words < 18)
            return false;

        var clauseMarkerCount = CountTechnicalClauseCatalogLeadMarkers(text);
        if (clauseMarkerCount < 3)
            return false;

        if (ContainsAny(
            foldedText,
            " shall ",
            " must ",
            " required ",
            " requirements ",
            " exception ",
            " permitted ",
            " comply ",
            " complies ",
            " conform "))
        {
            return false;
        }

        var textWithoutMarkers = NumberedTechnicalClauseMarkerRegex().Replace(text, " ");
        var sentenceMarkers = textWithoutMarkers.Count(static ch => ch is '.' or '!' or '?' or ';' or ':');
        if (sentenceMarkers > 1)
            return false;

        var cueCount = TechnicalClauseBodyCueRegex().Matches(foldedText).Count;
        return clauseMarkerCount >= 4
            || (clauseMarkerCount >= 3 && words <= 72 && cueCount >= 4);
    }

    private static int CountTechnicalClauseCatalogLeadMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match match in NumberedTechnicalClauseMarkerRegex().Matches(text))
        {
            var after = match.Index + match.Length;
            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;

            if (after >= text.Length || !char.IsLetter(text[after]))
                continue;

            var beforeStart = Math.Max(0, match.Index - 12);
            var before = text.Substring(beforeStart, match.Index - beforeStart);
            var normalizedBefore = FoldDiacritics(before).ToLowerInvariant();
            if (ReferenceLeadBeforeClauseMarkerRegex().IsMatch(normalizedBefore))
                continue;

            count++;
        }

        return count;
    }

    private static bool LooksLikeScheduleOrTimelineBodyContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 3 || inlinePageNumberBoundaries >= 12)
            return false;

        var words = CountWords(text);
        if (words < 35)
            return false;

        var scheduleCueCount = ScheduleBodyCueRegex().Matches(foldedText).Count;
        if (scheduleCueCount < 4)
            return false;

        var dateCount = DateOrMonthCueRegex().Matches(text).Count;
        if (dateCount < 3)
            return false;

        var punctuationCount = text.Count(static ch => ch is '.' or ',' or ';' or ':' or '(' or ')');
        return punctuationCount >= 3
            || contentDensityScore >= 0.35
            || ContainsAny(foldedText, " due ", " deadline ", " completed ", " submitted ");
    }

    private static bool LooksLikeDenseMeasuredContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape)
    {
        var words = CountWords(text);
        if (words < 28)
            return false;

        var measurementCount = CountMeasurementOrSpecificationTokens(text);
        if (measurementCount < 4)
            return false;

        var hasContentCue = DenseMeasuredContentCueRegex().IsMatch(foldedText);
        var hasStructuredBodyShape = CountBulletMarkers(text) >= 4
            || shape.LongLineRatio >= 0.25
            || words >= 60;

        return hasContentCue || hasStructuredBodyShape;
    }

    private static bool HasStrongNavigationMarker(string foldedText, string paddedNormalizedText)
    {
        if (string.IsNullOrWhiteSpace(foldedText) || string.IsNullOrWhiteSpace(paddedNormalizedText))
            return false;

        if (foldedText.Contains("fiche-index", StringComparison.Ordinal)
            || foldedText.Contains("fiche index", StringComparison.Ordinal))
        {
            return true;
        }

        return StrongNavigationMarkerRegex().IsMatch(paddedNormalizedText);
    }

    private static bool ContainsExplicitTableOfContentsMarker(string foldedText, string paddedNormalizedText)
        => foldedText.Contains("table des matieres", StringComparison.Ordinal)
            || foldedText.Contains("table of contents", StringComparison.Ordinal)
            || foldedText.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice general ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de contenido ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de contenidos ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de materias ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice analitico ", StringComparison.Ordinal);

    private static bool ContainsShortTableOfContentsMarker(string paddedNormalizedText)
        => paddedNormalizedText.Contains(" sommaire ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" contents ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" sommario ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" sumario ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" toc ", StringComparison.Ordinal);

    private static bool IsStandaloneShortTableOfContentsMarker(string paddedNormalizedText)
        => paddedNormalizedText.Trim() is "sommaire"
            or "contents"
            or "sommario"
            or "sumario"
            or "indice"
            or "index"
            or "toc"
            or "inhaltsverzeichnis";

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static int CountInlinePageNumberBoundaries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i]))
                continue;

            var start = i;
            while (i < text.Length && char.IsDigit(text[i]))
                i++;

            var digitRun = i - start;
            if (digitRun is <= 0 or > 4)
                continue;

            var j = i;
            while (j < text.Length && (char.IsWhiteSpace(text[j]) || text[j] is '-' or '\u2013' or '\u2014' or '.' or ')'))
                j++;

            if (j < text.Length && char.IsLetter(text[j]) && char.IsUpper(text[j]))
                count++;

            i--;
        }

        return count;
    }

    private static bool LooksLikeCompactIndexCatalog(string text, string paddedNormalizedText)
        => (paddedNormalizedText.Contains(" index ", StringComparison.Ordinal)
            || paddedNormalizedText.TrimStart().StartsWith("index", StringComparison.Ordinal))
            && CountWords(text) >= 20
            && (CountInlinePageNumberBoundaries(text) >= 3 || CountShortNumberTokens(text) >= 5);

    private static bool LooksLikeTitleListChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;

        var words = 0;
        var capitalizedStarts = 0;
        var sentenceMarkers = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (ch is '.' or '!' or '?' or ';' or ':' or '\u2022')
                sentenceMarkers++;

            if (char.IsLetter(ch))
            {
                if (!inWord)
                {
                    words++;
                    if (char.IsUpper(ch))
                        capitalizedStarts++;
                }

                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return (words >= 24
                && capitalizedStarts >= Math.Max(12, words / 3)
                && sentenceMarkers <= 2)
            || (words >= 40
                && capitalizedStarts >= 20
                && sentenceMarkers <= 2)
            || (CountLowerToUpperTransitions(text) >= 8 && sentenceMarkers <= 3);
    }

    private static int CountShortNumberTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match _ in ShortNumberTokenRegex().Matches(text))
            count++;

        return count;
    }

    private static int CountMeasurementOrSpecificationTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match _ in MeasurementOrSpecificationRegex().Matches(text))
            count++;

        return count;
    }

    private static int CountInlineOrdinalBodyMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match _ in InlineOrdinalBodyMarkerRegex().Matches(text))
            count++;

        return count;
    }

    private static RetrievalNavigationShape AnalyzeShape(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            lines = [text.Trim()];

        var shortLines = 0;
        var longLines = 0;
        var pageReferenceLines = 0;
        var dotLeaderLines = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var words = CountWords(line);
            if (words is > 0 and <= 7)
                shortLines++;
            if (words >= 14 || line.Length >= 120)
                longLines++;
            if (LooksLikePageReferenceLine(trimmed))
                pageReferenceLines++;
            if (LooksLikeDotLeaderLine(trimmed))
                dotLeaderLines++;
        }

        return new RetrievalNavigationShape(
            LineCount: lines.Length,
            ShortLineRatio: lines.Length == 0 ? 0.0 : shortLines / (double)lines.Length,
            LongLineRatio: lines.Length == 0 ? 0.0 : longLines / (double)lines.Length,
            PageReferenceLineCount: pageReferenceLines,
            DotLeaderLineCount: dotLeaderLines);
    }

    private static bool LooksLikeContactDirectoryContent(string text, RetrievalNavigationShape shape)
    {
        if (shape.DotLeaderLineCount > 0)
            return false;

        if (LooksLikeCompactContactBlockContent(text, shape))
            return true;

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length < 3)
            return false;

        var contactLines = 0;
        foreach (var line in lines)
        {
            if (LooksLikeContactDirectoryLine(line))
                contactLines++;
        }

        return contactLines >= 3
            && contactLines >= Math.Ceiling(lines.Length * 0.55)
            && shape.DotLeaderLineCount == 0;
    }

    private static bool LooksLikeCompactContactBlockContent(string text, RetrievalNavigationShape shape)
    {
        if (shape.PageReferenceLineCount >= 3)
            return false;

        var words = CountWords(text);
        if (words < 12)
            return false;

        var folded = FoldDiacritics(text).ToLowerInvariant();
        if (!ContactBlockCueRegex().IsMatch(folded))
            return false;

        var locatorCount = ContactLocatorRegex().Matches(text).Count;
        if (locatorCount >= 2)
            return true;

        if (locatorCount >= 1
            && ContainsAny(
                folded,
                " contact ",
                " contacts ",
                " channel ",
                " channels ",
                " canal ",
                " canaux ",
                " hotline ",
                " support ",
                " speak up ",
                " whistleblowing ",
                " ethics ",
                " data protection officer ",
                " dpo "))
        {
            return true;
        }

        return locatorCount >= 1
            && ContainsAny(
                folded,
                " accuracy ",
                " institution ",
                " office ",
                " omission ",
                " publisher ",
                " responsibility ",
                " standards institution ",
                " translation ");
    }

    private static bool LooksLikeContactDirectoryLine(string line)
    {
        var words = CountWords(line);
        if (words is < 3 or > 34)
            return false;

        return !LooksLikePageReferenceLine(line)
            && ContactDirectoryCueRegex().IsMatch(line);
    }

    private static bool LooksLikeTechnicalRevisionNoticeContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 3 || inlinePageNumberBoundaries >= 10)
            return false;

        var words = CountWords(text);
        if (words < 24)
            return false;

        var cueCount = TechnicalRevisionNoticeCueRegex().Matches(foldedText).Count;
        if (cueCount < 4)
            return false;

        var standardReferenceCount = TechnicalStandardReferenceRegex().Matches(text).Count;
        var measurementCount = CountMeasurementOrSpecificationTokens(text);
        var locatorCount = ContactLocatorRegex().Matches(text).Count;
        return standardReferenceCount >= 1
            || measurementCount >= 1
            || locatorCount >= 1
            || contentDensityScore >= 0.35;
    }

    private static bool LooksLikeStandardFrontMatterContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 3 || inlinePageNumberBoundaries >= 8)
            return false;

        var words = CountWords(text);
        if (words < 20)
            return false;

        var cueCount = StandardFrontMatterCueRegex().Matches(foldedText).Count;
        if (cueCount < 3)
            return false;

        var standardReferenceCount = TechnicalStandardReferenceRegex().Matches(text).Count;
        return standardReferenceCount >= 1
            || foldedText.Contains(" standard ", StringComparison.Ordinal)
            || foldedText.Contains(" standards ", StringComparison.Ordinal)
            || foldedText.Contains(" specification ", StringComparison.Ordinal)
            || contentDensityScore >= 0.35;
    }

    private static bool LooksLikeNumberedTechnicalClauseBodyContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 3)
            return false;

        var words = CountWords(text);
        if (words < 22)
            return false;

        var clauseMarkerCount = NumberedTechnicalClauseMarkerRegex().Matches(text).Count;
        if (clauseMarkerCount < 1)
            return false;

        var cueCount = TechnicalClauseBodyCueRegex().Matches(foldedText).Count;
        if (cueCount < 4)
            return false;

        var hasNormativeOrActionCue = ContainsAny(
            foldedText,
            " shall ",
            " must ",
            " required ",
            " requirements ",
            " exception ",
            " marked ",
            " marking ",
            " comply ",
            " complies ",
            " conform ");
        var hasTechnicalPayload = CountMeasurementOrSpecificationTokens(text) >= 1
            || TechnicalStandardReferenceRegex().IsMatch(text)
            || cueCount >= 7;
        if (hasNormativeOrActionCue && hasTechnicalPayload)
            return true;

        var sentenceMarkers = text.Count(static ch => ch is '.' or ';' or ':');
        return sentenceMarkers >= 2
            && cueCount >= 8
            && contentDensityScore >= 0.45;
    }

    private static bool LooksLikeStandardComplianceReferenceContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape,
        int inlinePageNumberBoundaries,
        double contentDensityScore)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 3 || inlinePageNumberBoundaries >= 10)
            return false;

        var words = CountWords(text);
        if (words < 20)
            return false;

        var standardReferenceCount = TechnicalStandardReferenceRegex().Matches(text).Count;
        if (standardReferenceCount < 1)
            return false;

        var cueCount = StandardComplianceCueRegex().Matches(foldedText).Count;
        if (cueCount < 3)
            return false;

        var hasCompliancePhrase = ContainsAny(
            foldedText,
            " shall comply ",
            " must comply ",
            " complies with ",
            " comply with ",
            " conform to ",
            " conformance with ",
            " standard for ",
            " requirements for ");
        if (hasCompliancePhrase)
            return true;

        return standardReferenceCount >= 2
            && cueCount >= 6
            && contentDensityScore >= 0.25;
    }

    private static bool LooksLikeDenseIndexTermContent(
        string text,
        string foldedText,
        string paddedNormalizedText,
        RetrievalNavigationShape shape,
        double contentDensityScore)
    {
        if (!paddedNormalizedText.Contains(" index ", StringComparison.Ordinal)
            && !paddedNormalizedText.Contains(" indice ", StringComparison.Ordinal))
            return false;
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount > 0)
            return false;
        if (CountWords(text) < 28 || contentDensityScore < 0.65)
            return false;

        var textWithoutDotLeaders = DotLeaderSequenceRegex().Replace(text, " ");
        var sentenceMarkers = textWithoutDotLeaders.Count(static ch => ch is '.' or '!' or '?' or ';');
        var hasNarrativeCue = ContainsAny(
                paddedNormalizedText,
                " index identifies ",
                " index identify ",
                " index indicates ",
                " index indicate ",
                " index describes ",
                " index describe ",
                " index records ",
                " index tracks ",
                " index shows ",
                " index value ",
                " index status ",
                " index is used ",
                " index shall ",
                " index should ",
                " index must ",
                " indice identifie ",
                " indice indique ",
                " indice de ",
                " indice est ",
                " indice permet ")
            || IndexTermNarrativeCueRegex().IsMatch(foldedText);

        return sentenceMarkers >= 2
            && hasNarrativeCue;
    }

    private static bool LooksLikeDotLeaderLine(string line)
        => !string.IsNullOrWhiteSpace(line)
            && line.Contains("..", StringComparison.Ordinal)
            && (PageNumberAtLineEndRegex().IsMatch(line)
                || DotLeaderPageReferenceRegex().IsMatch(line));

    private static bool LooksLikePageReferenceLine(string line)
        => !string.IsNullOrWhiteSpace(line)
            && ((CountWords(line) <= 12 && PageNumberAtLineEndRegex().IsMatch(line))
                || (CountWords(line) <= 18 && DotLeaderPageReferenceRegex().IsMatch(line)));

    private static double ComputeContentDensityScore(string text, string foldedText, RetrievalNavigationShape shape)
    {
        var words = CountWords(text);
        if (words == 0)
            return 0.0;

        var textWithoutDotLeaders = DotLeaderSequenceRegex().Replace(text, " ");
        var sentenceMarkers = textWithoutDotLeaders.Count(static ch => ch is '.' or '!' or '?' or ';');
        var sentenceDensity = Math.Clamp(sentenceMarkers / Math.Max(1.0, words / 20.0), 0.0, 1.0);
        var longLineSignal = Math.Clamp(shape.LongLineRatio * 1.4, 0.0, 1.0);
        var structuredSignal = LooksLikeStructuredContent(foldedText) ? 1.0 : 0.0;

        return Math.Clamp(
            (sentenceDensity * 0.35)
            + (longLineSignal * 0.35)
            + (structuredSignal * 0.30),
            0.0,
            1.0);
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                    count++;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }

    private static bool ContainsLayoutIndexArtifact(string foldedText)
        => foldedText.Contains("[index", StringComparison.Ordinal)
            || foldedText.Contains(" index: ", StringComparison.Ordinal)
            || foldedText.EndsWith(" index:", StringComparison.Ordinal);

    private static int CountLowerToUpperTransitions(string text)
    {
        var count = 0;
        var previousWasLower = false;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch) && previousWasLower)
                count++;

            previousWasLower = char.IsLower(ch);
        }

        return count;
    }

    private static int CountNumberedSteps(string text)
    {
        var count = 0;
        foreach (Match _ in NumberedStepRegex().Matches(text))
            count++;

        return count;
    }

    private static string NormalizeForNavigationLookup(string text)
        => NavigationLookupRegex().Replace(text, " ").Trim();

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NavigationLookupRegex();

    [GeneratedRegex(@"\b(?:index|liste|list|catalogue|catalog|inventaire|inventory|indice\s+(?:general|de\s+contenidos?|de\s+materias?|analitico)|sumario|sommario|inhaltsverzeichnis)\s+(?:des?|de|du|d['\u2019]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:index|liste|list|catalogue|catalog|inventory|sumario|sommario)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StrongNavigationMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:pour|for|para|per)\s+\d+|(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CountOrStepMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{1,4}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNumberTokenRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d+(?:[,.]\d+)?\s*(?:%|°\s*[cfk]?|kg|g|mg|l|ml|cl|dl|m|cm|mm|km|h|min|mn|s|sec|w|kw|v|kv|a|ma|hz|khz|mhz|pa|kpa|bar|psi|nm|rpm|tr/min|chf|eur|usd|gb|mb|kb|tb)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MeasurementOrSpecificationRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d{1,2}\s+(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex InlineOrdinalBodyMarkerRegex();

    [GeneratedRegex(@"\b(?:preparation|preparacion|preparacao|preparazione|procedure|procedures|procedimiento|procedimento|procedura|instructions?|instruction|etapes?|steps?|passos?|schritte?|material|materiel|materials|materiaux|component|components|composant|composants|assembly|assemblage|montage|configuration|installation|maintenance|controle|control|verification|pruefung|prufung|pruefung|verificacion|verifica)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DenseMeasuredContentCueRegex();

    [GeneratedRegex(@"\b(?:ansi|asme|astm|bs|csa|din|dvs|en|iec|iso|nec|nfpa|sms|ul|vdi)\s*[a-z]?\s*\d{2,5}(?:[\.-]\d{1,5})*(?:-\d{2,4})?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalStandardReferenceRegex();

    [GeneratedRegex(@"\b(?:amendments?|application|approved|committee|edition|informative|mandatory|normative|published|purpose|requirements?|revised|standard|standards|subcommittee|symbols?|updated)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalStandardsProseCueRegex();

    [GeneratedRegex(@"\b(?:abmessungen|abschnitt|accidents?|anforderungen|anwendung|annex|anschweissen|appendix|application|behaelter|behalter|berechnung|bild|blockflansch(?:e|es)?|calculation|caution|cleanrooms?|clause|colour|colors?|control|damage|dichtung(?:en)?|dimensions?|druck|dvs|electrical|equipment|equation|essais?|exigences?|figure|fittings?|flansch(?:e|es)?|flussigkeit|fluessigkeit|formstuecke|formstucke|gasket|gestaltung(?:sgrundsaetze)?|gewoelbt|gleichung|grundsaetze|guete(?:anforderungen)?|guidelines?|harm|hart[-\s]?pvc|hazard|injur(?:y|ies)|installation|kegelig(?:en)?|kunststoff(?:e|en|s)?|maintenance|machinery|materials?|materiaux|matrices|matrix|mindestwer(?:t|te|tie)|naehte?|nahte?|nachweis|normen?|paragraph|pipe|polybuten|polyvinyl|pressure|probabilit(?:y|ies)|probe(?:n|koerper)?|probekorper|procedure|procedures|property\s+damage|pruef(?:en|ung)|pruf(?:en|ung)|pvc|requirements?|rechnerisch(?:en)?|richtlinien?|rohre?|safety|sample|samples|schlagzaeh|schlagzah|schwei(?:ss|b|\u00df)|schweibraupe|schweissnaehte?|schweissraupe|seal|section|severity|signal\s+words?|specification|specimen|standard|standards|stainless|stehende|stutzen|symbols?|table|tabelle|technical|technique|technische(?:r|n)?|tecnico|temperatur|temperature|tensile|test|testing|thermoplast(?:e|en|ic|ics)|tube|validation|verification|wanddicken|warning|weichmacherhaltige|werkstoff(?:e|en|s)?|zuganglichkeit|zugversuch)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalReferenceBodyCueRegex();

    [GeneratedRegex(@"\b(?:administration|answers?|booklets?|context|examples?|forms?|instructions?|participants?|questions?|questionnaires?|respondents?|responses?|samples?|symbols?|test|testing)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuestionnaireOrFormExampleCueRegex();

    [GeneratedRegex(@"\b(?:collateral|directive|embedded|examples?|information|messages?|panel|product|section|signal|supplemental)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTopicListCueRegex();

    [GeneratedRegex(@"\b(?:actuator|aging|apparatus|area|assembly|avoidance|branch|cam|chart|chroma|circuit|class|color|colour|component|conductor|conductors|consequence|control|controller|corrosion|crush|current|cut|device|diagram|disconnect|division|enclosure|enclosures|field|filter|fuse|gasket|ground(?:ed|ing)?|groups?|hazard|hydraulic|indicator|interlock|live|load|manual|motor|neutral|oil|operator|overcurrent|overload|parameters?|power|protect(?:ion|ive)?|relay|reset|resistance|selector|spacing|statement|switch(?:es)?|symbol|terminal|tolerance|transformer|voltage|window|wire|wiring|zone)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalDiagramLegendCueRegex();

    [GeneratedRegex(@"\b(?:electrode|phase|service)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalElectricalLegendSupplementCueRegex();

    [GeneratedRegex(@"\b(?:[A-Z]{1,6}\d{1,6}[A-Z0-9-]*|\d{2,6}[A-Z]{1,6}[A-Z0-9-]*|#\s*[A-Z]{1,6})\b", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalDiagramReferenceCodeRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:[A-Z]{1,4}\d+(?:\.\d+){1,6}|[A-Z]{1,4}\.\d+(?:\.\d+){1,6}|\d+(?:\.\d+){1,6}[A-Z]?)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedTechnicalClauseMarkerRegex();

    [GeneratedRegex(@"\b(?:see|section|sections|clause|clauses|chap|chapter|annex|appendix)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceLeadBeforeClauseMarkerRegex();

    [GeneratedRegex(@"\b(?:access|adjustable|annex|approved|assembly|automatic|barrier|bolt|branch|bus|clearance|comply|conductor|connectors?|control|current|device|disconnect|door|electrical|enclosure|equipment|exception|field|ferrules?|fuses?|ground(?:ed|ing)?|insulat(?:ed|ion|or)|label|load|marked|marking|nameplate|neutral|overcurrent|overload|panel|protect(?:ed|ion|ive)|rated|rating|required?|shall|short[-\s]?circuit|spacing|standard|standoff|supplement|supply|terminal|termination|voltage|washer|wire|wiring)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalClauseBodyCueRegex();

    [GeneratedRegex(@"\b(?:associated|assemblies|assembly|audio|automatic|certified|comply|complies|conform|conformance|equipment|electric|electrical|industrial|listed|power|recognized|requirements?|shall|speed|standard|standards|supplement|systems?|ul|video)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandardComplianceCueRegex();

    [GeneratedRegex(@"\b\d+(?:\.\d+){1,4}\s+[A-Z][A-Z0-9 /,()_\-\u2013\u2014]{3,90}\s*(?:[-\u2013\u2014]|—)", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalClauseDefinitionLeadRegex();

    [GeneratedRegex(@"\b(?:apparatus|components?|comparison|correlation|defined|definition|definitions|division|divisions?|enclosed|enclosure|examples?|explosion|explosionproof|external|field|flammable|groups?|hazardous|ignit(?:e|ing|ion)|installer|internal|make[-\s]?and[-\s]?break|maximum|minimum|nonincendive|normal\s+operation|parameters?|prescribed|terminations?|test|zone|zones?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalDefinitionCueRegex();

    [GeneratedRegex(@"\b(?:arc|breakers?|capacitance|capacitor|circuit|current|devices?|electrical|inductance|input|output|power|relay|relays|resistance|resistors?|ratio|servo|switch(?:es)?|thermal|voltage|wiring)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalParameterCueRegex();

    [GeneratedRegex(@"\b(?:ballot(?:ing)?|completed|deadline|deferred|drafts?|due|finali[sz]ed|form|issue|letter|proposals?|public\s+reviews?|publisher|revision|revisions|schedule|submit(?:ted)?|tentative)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ScheduleBodyCueRegex();

    [GeneratedRegex(@"\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+\d{1,2},\s+\d{4}\b|\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DateOrMonthCueRegex();

    [GeneratedRegex(@"\b(?:edition|language\s+version|maximum\s+working\s+pressure|new\s+sizes?|original\s+language|published|replaces|revision|standard|standards|supersedes|thread|title\s+unchanged|working\s+pressure)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalRevisionNoticeCueRegex();

    [GeneratedRegex(@"\b(?:amendments?|bodies|committee|copyright|drafting|edition|law|panels?|permission|permitted|published|represented|specification|standard|standards|subcommittees?|title)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandardFrontMatterCueRegex();

    [GeneratedRegex(@"\d{1,5}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberAtLineEndRegex();

    [GeneratedRegex(@"\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DotLeaderSequenceRegex();

    [GeneratedRegex(@"\.{2,}\s*(?:[ivxlcdm]{1,8}\s+)?\d{1,5}(?:\b|[^\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DotLeaderPageReferenceRegex();

    [GeneratedRegex(@"\b(?:tel(?:ephone)?|phone|fax|e-?mail|courriel|www\.|https?://|@)\b|\b\d{3,6}\s+[\p{Lu}][\p{L}'\u2019\.-]{2,}\b|(?:\b\d+\w?\b.{0,80}\b(?:rue|avenue|av\.?|boulevard|bd|route|chemin|impasse|place|quai|street|st\.?|road|rd\.?|lane|ln\.?|drive|dr\.?|court|ct\.?|strasse|stra\u00dfe|str\.?|weg|gasse|allee|platz|via|viale|piazza|calle|avenida|avda\.?)\b|\b(?:rue|avenue|av\.?|boulevard|bd|route|chemin|impasse|place|quai|street|st\.?|road|rd\.?|lane|ln\.?|drive|dr\.?|court|ct\.?|strasse|stra\u00dfe|str\.?|weg|gasse|allee|platz|via|viale|piazza|calle|avenida|avda\.?)\b.{0,80}\b\d+\w?\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContactDirectoryCueRegex();

    [GeneratedRegex(@"\b(?:contacts?|contact\s+channels?|canaux?|canal|channels?|hotline|support|whistleblowing|speak\s+up|ethics\s+channel|data\s+protection\s+officer|dpo|privacy\s+office)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContactBlockCueRegex();

    [GeneratedRegex(@"https?://|www\.|[\p{L}\p{N}._%+-]+@[\p{L}\p{N}.-]+\.[\p{L}]{2,}|\+?\d[\d\s()./-]{6,}\d|\b(?:tel(?:ephone)?|phone|fax|telex|e-?mail|courriel)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContactLocatorRegex();

    [GeneratedRegex(@"\b(?:index|indice)\b.{0,80}\b(?:identif|indicat|describe|means|refers|records|tracks|shows|used|utilis|sert|correspond|represente|represe?nte|shall|should|must|doit|permet)\b|\b(?:identif|indicat|describe|records|tracks|used|utilis|sert|correspond|represente|represe?nte)\b.{0,80}\b(?:index|indice)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IndexTermNarrativeCueRegex();
}

internal sealed record RetrievalChunkClassification(
    string ContentRole,
    string ChunkType,
    string? NavigationReason,
    string? OriginalChunkType,
    double NavigationScore,
    double ContentDensityScore);

internal sealed record RetrievalNavigationSignal(
    string ContentRole,
    string? NavigationReason,
    double NavigationScore,
    double ContentDensityScore);

internal sealed record RetrievalNavigationShape(
    int LineCount,
    double ShortLineRatio,
    double LongLineRatio,
    int PageReferenceLineCount,
    int DotLeaderLineCount);
