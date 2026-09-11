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

    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> ParseSourceBackedLlmEvidenceExplorationPasses(
        string? rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return Array.Empty<SourceBackedEvidenceExplorationPass>();

        if (!TryExtractJsonObject(rawJson, out var json))
            json = rawJson;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<SourceBackedEvidenceExplorationPass>();

            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (alreadyTriedQueries is not null)
            {
                foreach (var query in alreadyTriedQueries)
                {
                    var normalized = NormalizeGeneratedSourceBackedExplorationQueryForDedup(query);
                    if (!string.IsNullOrWhiteSpace(normalized))
                        emitted.Add(normalized);
                }
            }

            var passes = new List<SourceBackedEvidenceExplorationPass>();
            if (doc.RootElement.TryGetProperty("passes", out var passesElement) && passesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var passElement in passesElement.EnumerateArray())
                {
                    if (passElement.ValueKind != JsonValueKind.Object)
                        continue;

                    var label = NormalizeSourceBackedLlmExplorationLabel(TryGetString(passElement, "label"));
                    var purpose = CollapseWhitespace(TryGetString(passElement, "purpose") ?? string.Empty);
                    var queries = ExtractSanitizedSourceBackedLlmExplorationQueries(passElement, emitted);
                    if (queries.Length == 0)
                        continue;

                    passes.Add(new SourceBackedEvidenceExplorationPass(
                        label,
                        string.IsNullOrWhiteSpace(purpose) ? "LLM-planned bounded evidence search." : purpose,
                        queries,
                        SanitizeSourceBackedLlmExplorationCategory(
                            TryGetString(passElement, "categoryScope")
                            ?? TryGetString(passElement, "category")
                            ?? TryGetString(passElement, "categoryPath")
                            ?? TryGetString(passElement, "categoryRef")),
                        NullIfWhiteSpace(TryGetString(passElement, "docId") ?? TryGetString(passElement, "documentId")),
                        NullIfWhiteSpace(TryGetString(passElement, "docPath") ?? TryGetString(passElement, "documentPath")),
                        ReadSourceBackedNavigationTargetPageStart(passElement),
                        ReadSourceBackedNavigationTargetPageEnd(
                            passElement,
                            ReadSourceBackedNavigationTargetPageStart(passElement)),
                        "llm_planner"));
                    if (passes.Count >= MaxSourceBackedLlmEvidenceExplorationPasses)
                        break;
                }
            }

            if (passes.Count == 0)
            {
                passes.AddRange(ExtractSourceBackedLlmToolCallExplorationPasses(doc.RootElement, emitted));
            }

            if (passes.Count == 0)
            {
                var queries = ExtractSanitizedSourceBackedLlmExplorationQueries(doc.RootElement, emitted);
                if (queries.Length > 0)
                {
                    passes.Add(new SourceBackedEvidenceExplorationPass(
                        "llm_strategy",
                        "LLM-planned bounded evidence search.",
                        queries,
                        SanitizeSourceBackedLlmExplorationCategory(
                            TryGetString(doc.RootElement, "categoryScope")
                            ?? TryGetString(doc.RootElement, "category")
                            ?? TryGetString(doc.RootElement, "categoryPath")
                            ?? TryGetString(doc.RootElement, "categoryRef")),
                        NullIfWhiteSpace(TryGetString(doc.RootElement, "docId") ?? TryGetString(doc.RootElement, "documentId")),
                        NullIfWhiteSpace(TryGetString(doc.RootElement, "docPath") ?? TryGetString(doc.RootElement, "documentPath")),
                        ReadSourceBackedNavigationTargetPageStart(doc.RootElement),
                        ReadSourceBackedNavigationTargetPageEnd(
                            doc.RootElement,
                            ReadSourceBackedNavigationTargetPageStart(doc.RootElement)),
                        "llm_planner"));
                }
            }

            return passes;
        }
    }

    private static SourceBackedLlmCategoryScopeDecision ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return new SourceBackedLlmCategoryScopeDecision(null, null, null, null);

        if (!TryExtractJsonObject(rawJson, out var json))
            json = rawJson;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new SourceBackedLlmCategoryScopeDecision(null, null, null, null);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new SourceBackedLlmCategoryScopeDecision(null, null, null, null);

            var element = doc.RootElement;
            if (TryGetObjectProperty(doc.RootElement, "categoryDecision", out var categoryDecision)
                || TryGetObjectProperty(doc.RootElement, "categoryScopeDecision", out categoryDecision)
                || TryGetObjectProperty(doc.RootElement, "scopeDecision", out categoryDecision))
            {
                element = categoryDecision;
            }

            var categoryScope = SanitizeSourceBackedLlmExplorationCategory(
                TryGetString(element, "categoryScope")
                ?? TryGetString(element, "category")
                ?? TryGetString(element, "categoryPath")
                ?? TryGetString(element, "categoryRef")
                ?? TryGetString(element, "scopePath")
                ?? TryGetString(element, "scope"));
            if (LooksLikeNullSourceBackedLlmCategoryScopeDecision(categoryScope))
                categoryScope = null;

            var decision = CollapseWhitespace(
                TryGetString(element, "decision")
                ?? TryGetString(element, "action")
                ?? TryGetString(element, "scopeDecision")
                ?? string.Empty);
            var confidence = CollapseWhitespace(TryGetString(element, "confidence") ?? string.Empty);
            var reason = CollapseWhitespace(TryGetString(element, "reason") ?? TryGetString(element, "rationale") ?? string.Empty);

            return new SourceBackedLlmCategoryScopeDecision(
                categoryScope,
                string.IsNullOrWhiteSpace(decision) ? null : decision,
                string.IsNullOrWhiteSpace(confidence) ? null : confidence,
                string.IsNullOrWhiteSpace(reason) ? null : reason);
        }
    }

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool LooksLikeNullSourceBackedLlmCategoryScopeDecision(string? value)
    {
        var normalized = NormalizeLooseLookup(value);
        return string.IsNullOrWhiteSpace(normalized)
               || string.Equals(normalized, "none", StringComparison.Ordinal)
               || string.Equals(normalized, "null", StringComparison.Ordinal)
               || string.Equals(normalized, "na", StringComparison.Ordinal)
               || string.Equals(normalized, "n a", StringComparison.Ordinal)
               || string.Equals(normalized, "aucun", StringComparison.Ordinal)
               || string.Equals(normalized, "aucune", StringComparison.Ordinal)
               || string.Equals(normalized, "pas de categorie", StringComparison.Ordinal)
               || string.Equals(normalized, "no category", StringComparison.Ordinal)
               || string.Equals(normalized, "no scope", StringComparison.Ordinal);
    }

    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> ApplySourceBackedLlmCategoryScopeDecision(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> passes,
        string? categoryScope,
        out int updatedPassCount)
    {
        updatedPassCount = 0;
        var normalizedCategoryScope = SanitizeSourceBackedLlmExplorationCategory(categoryScope);
        if (passes.Count == 0 || string.IsNullOrWhiteSpace(normalizedCategoryScope))
            return passes;

        var updated = new List<SourceBackedEvidenceExplorationPass>(passes.Count);
        foreach (var pass in passes)
        {
            if (CanApplySourceBackedLlmCategoryScopeDecision(pass))
            {
                updated.Add(pass with { CategoryScope = normalizedCategoryScope });
                updatedPassCount++;
            }
            else
            {
                updated.Add(pass);
            }
        }

        return updatedPassCount == 0 ? passes : updated;
    }

    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> ApplySourceBackedLlmCategoryScopeDecisionOrCreateScopeOnlyPass(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> passes,
        string? categoryScope,
        string? origin,
        out int updatedPassCount,
        out bool addedScopeOnlyPass)
    {
        addedScopeOnlyPass = false;
        var updated = ApplySourceBackedLlmCategoryScopeDecision(passes, categoryScope, out updatedPassCount);
        if (passes.Count > 0 || updatedPassCount > 0)
            return updated;

        var normalizedCategoryScope = SanitizeSourceBackedLlmExplorationCategory(categoryScope);
        if (string.IsNullOrWhiteSpace(normalizedCategoryScope))
            return updated;

        addedScopeOnlyPass = true;
        return new[]
        {
            new SourceBackedEvidenceExplorationPass(
                "llm_category_scope",
                "LLM-selected catalog scope preserved after query filtering.",
                Array.Empty<string>(),
                normalizedCategoryScope,
                Origin: string.IsNullOrWhiteSpace(origin) ? "llm_planner" : origin)
        };
    }

    private static bool CanApplySourceBackedLlmCategoryScopeDecision(SourceBackedEvidenceExplorationPass pass)
        => string.IsNullOrWhiteSpace(pass.CategoryScope)
           && string.IsNullOrWhiteSpace(pass.DocId)
           && string.IsNullOrWhiteSpace(pass.DocPath);

    private static bool HasSourceBackedLlmCategoryHints(string? categoryHints)
        => !string.IsNullOrWhiteSpace(categoryHints)
           && !string.Equals(CollapseWhitespace(categoryHints), "none", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRunLlmSourceBackedCategoryScopeAdjudication(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> passes,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string? query,
        string language,
        string? categoryHints)
    {
        if (!HasSourceBackedLlmCategoryHints(categoryHints) || passes.Count == 0)
            return false;

        var nonDocumentPasses = passes
            .Where(static pass => string.IsNullOrWhiteSpace(pass.DocId) && string.IsNullOrWhiteSpace(pass.DocPath))
            .ToArray();
        if (nonDocumentPasses.Length == 0)
            return false;

        if (nonDocumentPasses.Any(static pass => !string.IsNullOrWhiteSpace(pass.CategoryScope)))
            return false;

        var normalizedLanguage = NormalizeLanguageCode(language);
        var needsBroadDecision =
            LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
            || ShouldOfferBroadenedSourceSearch(query);

        var needsStructuredDecision =
            DetectRequestedDayAxisLabels(query, normalizedLanguage).Count > 1
            && DetectRequestedPlanningSlotAxisLabels(query, normalizedLanguage).Count > 0;

        return needsBroadDecision
               || needsStructuredDecision
               || currentAnalysis.UsableHitCount == 0
               || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount;
    }
}
