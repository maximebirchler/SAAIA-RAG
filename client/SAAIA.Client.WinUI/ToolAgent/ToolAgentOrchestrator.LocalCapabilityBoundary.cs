using System;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int AdvancedStructuredAnswerUnitThreshold = 6;
    private const int AdvancedMultiItemAnswerUnitThreshold = 5;
    private const int LocalNamedDocumentExtractionAnswerUnitLimit = 6;

    private sealed record LocalCapabilityBoundaryDecision(
        bool RequiresAdvancedAnalysis,
        string ReasonCode,
        string PlanKind,
        int AnswerUnitCount);

    private static LocalCapabilityBoundaryDecision EvaluateLocalCapabilityBoundary(
        RouterPlan? plan,
        string? userMessage = null,
        bool hasResolvedPriorSources = false)
    {
        if (LooksLikeComparativeDocumentaryRequest(userMessage)
            && (!LooksLikeUnresolvedComparativeDocumentReference(userMessage)
                || hasResolvedPriorSources))
        {
            var comparativeAnswerUnits = Math.Max(
                2,
                plan?.SourceBackedMission?.AtomicEvidenceCount ?? 0);
            return new LocalCapabilityBoundaryDecision(
                true,
                "explicit_documentary_comparison_outside_local_envelope",
                "comparison",
                comparativeAnswerUnits);
        }

        if (plan is null || plan.NeedClarification || plan.SourceBackedMission is null)
        {
            return new LocalCapabilityBoundaryDecision(
                false,
                "within_local_boundary",
                string.Empty,
                0);
        }

        var mission = plan.SourceBackedMission;
        var planKind = (mission.PlanKind ?? string.Empty).Trim().ToLowerInvariant();
        var atomicUnits = Math.Max(1, mission.AtomicEvidenceCount);
        var gridUnits = SafeAnswerUnitProduct(mission.RowCount, mission.ColumnCount);
        var answerUnits = string.Equals(planKind, "structured_layout", StringComparison.Ordinal)
            || mission.StructuredLayout
                ? Math.Max(atomicUnits, gridUnits)
                : atomicUnits;
        var boundedNamedDocumentExtraction =
            !mission.StructuredLayout
            && !string.Equals(
                planKind,
                "structured_layout",
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(mission.RequestedDocumentName)
            && answerUnits <= LocalNamedDocumentExtractionAnswerUnitLimit;

        if ((string.Equals(planKind, "structured_layout", StringComparison.Ordinal)
                || mission.StructuredLayout)
            && answerUnits >= AdvancedStructuredAnswerUnitThreshold)
        {
            return new LocalCapabilityBoundaryDecision(
                true,
                $"structured_answer_units_at_or_above_{AdvancedStructuredAnswerUnitThreshold}",
                "structured_layout",
                answerUnits);
        }

        if (string.Equals(planKind, "multi_item", StringComparison.Ordinal)
            && answerUnits >= AdvancedMultiItemAnswerUnitThreshold
            && !boundedNamedDocumentExtraction)
        {
            return new LocalCapabilityBoundaryDecision(
                true,
                "multi_item_answer_units_at_or_above_5",
                planKind,
                answerUnits);
        }

        return new LocalCapabilityBoundaryDecision(
            false,
            "within_local_boundary",
            planKind,
            answerUnits);
    }

    private static bool LooksLikeUnresolvedComparativeDocumentReference(string? userMessage)
    {
        var normalized = NormalizeLexicalLookup(userMessage);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!Regex.IsMatch(
            normalized,
            @"\b(?:ces|ceux|celles|these|those|estos|estas|esses|essas|diese|diesen|questi|queste)\b.{0,50}\b(?:documents?|sources?|fichiers?|files?|documentos?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
            RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (ExtractExplicitDocumentFileReferenceQueries(userMessage)
                .Take(2)
                .Count() >= 2)
        {
            return false;
        }

        return Regex.Matches(
                normalized,
                @"\b(?:iso|iec|astm|ansi|nfpa|din|en|fd\s+cen\s+tr|cen\s+tr)\s*[-:]?\s*\d[\p{L}\d./:-]*\b",
                RegexOptions.CultureInvariant)
            .Count < 2;
    }

    private static bool ShouldRequestUnresolvedComparativeDocumentReferences(
        string? userMessage,
        bool hasResolvedPriorSources)
        => !hasResolvedPriorSources
           && LooksLikeComparativeDocumentaryRequest(userMessage)
           && LooksLikeUnresolvedComparativeDocumentReference(userMessage);

    private static string BuildUnresolvedComparativeDocumentReferenceClarification(
        string? language)
        => SourceBackedLabel(
            NormalizeLanguageCode(language),
            "Quels sont les deux fichiers ou documents à comparer ? Donne leurs titres ou références exactes.",
            "Which two files or documents should I compare? Give their exact titles or references.",
            "¿Qué dos archivos o documentos debo comparar? Indica sus títulos o referencias exactos.",
            "Que dois arquivos ou documentos devo comparar? Indica os títulos ou referências exatos.",
            "Welche zwei Dateien oder Dokumente soll ich vergleichen? Nenne ihre genauen Titel oder Referenzen.",
            "Quali due file o documenti devo confrontare? Indica i titoli o riferimenti esatti.");

    private static int SafeAnswerUnitProduct(int rows, int columns)
    {
        var normalizedRows = Math.Max(1, rows);
        var normalizedColumns = Math.Max(1, columns);
        var product = (long)normalizedRows * normalizedColumns;
        return product >= int.MaxValue ? int.MaxValue : (int)product;
    }

    private static bool HasCompleteExplicitStructuredGridAxes(
        string? userMessage,
        string? language)
        => LooksLikeAnyDocumentaryPlanningRequest(userMessage)
           && DetectRequestedDayAxisLabels(
                   userMessage,
                   NormalizeLanguageCode(language))
               .Count >= 2
           && DetectRequestedPlanningSlotAxisLabels(
                   userMessage,
                   NormalizeLanguageCode(language))
               .Count >= 2;
}
