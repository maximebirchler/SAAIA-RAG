using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string? NormalizeSourceBackedCandidateAdjudicationJsonForWriter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !TryExtractJsonObject(raw, out var json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("decision", out _)
                && !doc.RootElement.TryGetProperty("items", out _))
            {
                return null;
            }

            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractSourceBackedCandidateAdjudicationDecision(string? normalizedJson)
    {
        if (string.IsNullOrWhiteSpace(normalizedJson))
            return "none";

        try
        {
            using var doc = JsonDocument.Parse(normalizedJson);
            return TryGetString(doc.RootElement, "decision") ?? "unknown";
        }
        catch (JsonException)
        {
            return "invalid_json";
        }
    }

    private sealed record SourceBackedCandidateAdjudicationSignal(
        string Decision,
        int UsefulCandidateCount,
        int MissingCount,
        IReadOnlyList<string> MissingSlots,
        bool RequestsMoreRetrieval);

    private static bool TryBuildSourceBackedCandidateAdjudicationSignal(
        string? normalizedJson,
        string query,
        string language,
        out SourceBackedCandidateAdjudicationSignal signal)
    {
        signal = new SourceBackedCandidateAdjudicationSignal(
            "none",
            0,
            0,
            Array.Empty<string>(),
            false);
        if (string.IsNullOrWhiteSpace(normalizedJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(normalizedJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var usefulCandidates = CountLlmAdjudicatedUsefulCandidates(doc.RootElement);
            var missingSlots = NormalizeLlmAdjudicatedMissingSlotsForRequest(
                    ExtractLlmAdjudicatedMissingSlots(doc.RootElement),
                    query,
                    language)
                .ToArray();
            var decision = NormalizeSourceBackedCandidateAdjudicationDecision(
                TryGetString(doc.RootElement, "decision"),
                usefulCandidates,
                missingSlots.Length);
            var requestsMoreRetrieval =
                ShouldUseLlmCandidateAdjudicationForRetrievalExpansion(query, language)
                && ((string.Equals(decision, "insufficient", StringComparison.OrdinalIgnoreCase)
                        && (missingSlots.Length > 0 || usefulCandidates <= 0))
                    || (string.Equals(decision, "partial", StringComparison.OrdinalIgnoreCase)
                        && missingSlots.Length > 0));
            signal = new SourceBackedCandidateAdjudicationSignal(
                decision,
                usefulCandidates,
                missingSlots.Length,
                missingSlots,
                requestsMoreRetrieval);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ShouldUseLlmCandidateAdjudicationForRetrievalExpansion(
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return ShouldGateStructuredSourceBackedPlanningCoverage(query)
               || LooksLikeAnyDocumentaryPlanningRequest(query)
               || LooksLikeBroadSourceBackedCompositionRequest(query)
               || LooksLikeMultipleCandidateSynthesisRequest(query)
               || ShouldOfferBroadenedSourceSearch(query)
               || DetectRequestedPlanningSlotAxisLabels(query, language).Count > 0;
    }

    private static string NormalizeSourceBackedCandidateAdjudicationDecision(
        string? rawDecision,
        int usefulCandidateCount,
        int missingCount)
    {
        var normalized = NormalizeLooseLookup(rawDecision);
        if (string.IsNullOrWhiteSpace(normalized))
            return InferSourceBackedCandidateAdjudicationDecision(usefulCandidateCount, missingCount);

        var looksLikeSchemaLiteral =
            normalized.Contains("|", StringComparison.Ordinal)
            || normalized.Contains(" or ", StringComparison.Ordinal)
            || (normalized.Contains("use candidates", StringComparison.Ordinal)
                && normalized.Contains("partial", StringComparison.Ordinal)
                && normalized.Contains("insufficient", StringComparison.Ordinal));
        if (looksLikeSchemaLiteral)
            return "unknown";

        if (normalized.Contains("insufficient", StringComparison.Ordinal)
            || normalized.Contains("not enough", StringComparison.Ordinal)
            || normalized.Contains("pas assez", StringComparison.Ordinal))
        {
            return "insufficient";
        }

        if (normalized.Contains("partial", StringComparison.Ordinal)
            || normalized.Contains("partiel", StringComparison.Ordinal)
            || normalized.Contains("partielle", StringComparison.Ordinal))
        {
            return "partial";
        }

        if (normalized.Contains("use candidates", StringComparison.Ordinal)
            || normalized.Contains("use candidate", StringComparison.Ordinal)
            || normalized.Contains("use_candidates", StringComparison.Ordinal)
            || normalized.Contains("usable", StringComparison.Ordinal))
        {
            return "use_candidates";
        }

        return InferSourceBackedCandidateAdjudicationDecision(usefulCandidateCount, missingCount);
    }

    private static string InferSourceBackedCandidateAdjudicationDecision(
        int usefulCandidateCount,
        int missingCount)
    {
        if (missingCount > 0)
            return usefulCandidateCount > 0 ? "partial" : "insufficient";

        return usefulCandidateCount > 0 ? "use_candidates" : "unknown";
    }

    private static int CountLlmAdjudicatedUsefulCandidates(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return 0;

        var usefulKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usefulCount = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (!JsonBoolEquals(item, "valid", true)
                || !JsonBoolEquals(item, "sourceUseful", true)
                || !string.IsNullOrWhiteSpace(TryGetString(item, "duplicateOf")))
            {
                continue;
            }

            var key = TryGetString(item, "candidateKey")
                ?? TryGetString(item, "pageKey")
                ?? TryGetString(item, "title")
                ?? usefulCount.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key) || usefulKeys.Add(key))
                usefulCount++;
        }

        return usefulCount;
    }

    private static IEnumerable<string> ExtractLlmAdjudicatedMissingSlots(JsonElement root)
    {
        if (!root.TryGetProperty("missing", out var missing) || missing.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in missing.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = CollapseWhitespace(item.GetString() ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var slot = CollapseWhitespace(
                TryGetString(item, "slotOrCriterion")
                ?? TryGetString(item, "slot")
                ?? TryGetString(item, "criterion")
                ?? TryGetString(item, "label")
                ?? string.Empty);
            var reason = CollapseWhitespace(TryGetString(item, "reason") ?? string.Empty);
            var missingText = string.IsNullOrWhiteSpace(reason)
                ? slot
                : string.IsNullOrWhiteSpace(slot)
                    ? reason
                    : $"{slot}: {reason}";
            if (!string.IsNullOrWhiteSpace(missingText))
                yield return missingText;
        }
    }

    private static IEnumerable<string> NormalizeLlmAdjudicatedMissingSlotsForRequest(
        IEnumerable<string> rawMissingSlots,
        string? query,
        string language)
    {
        var rawSlots = rawMissingSlots
            .Select(static slot => CollapseWhitespace(slot ?? string.Empty))
            .Where(static slot => !string.IsNullOrWhiteSpace(slot))
            .ToArray();
        if (rawSlots.Length == 0)
            yield break;

        var requestedAxes = DetectRequestedPlanningSlotAxisLabels(query, language)
            .Select(SelectPreferredPlanningSlotRetrievalTerm)
            .Where(static axis => !string.IsNullOrWhiteSpace(axis))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedAxes.Length == 0)
        {
            foreach (var slot in rawSlots.Distinct(StringComparer.OrdinalIgnoreCase))
                yield return slot;
            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in rawSlots)
        {
            var slotLabel = ExtractLlmAdjudicatedMissingSlotLabel(slot);
            foreach (var axis in requestedAxes)
            {
                if (!LlmAdjudicatedMissingSlotMatchesRequestedAxis(slotLabel, axis))
                    continue;

                if (emitted.Add(axis))
                    yield return axis;
                break;
            }
        }
    }

    private static string ExtractLlmAdjudicatedMissingSlotLabel(string rawSlot)
    {
        var collapsed = CollapseWhitespace(rawSlot ?? string.Empty);
        if (string.IsNullOrWhiteSpace(collapsed))
            return string.Empty;

        var separatorIndex = collapsed.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex > 0
            ? CollapseWhitespace(collapsed[..separatorIndex])
            : collapsed;
    }

    private static bool LlmAdjudicatedMissingSlotMatchesRequestedAxis(string? missingSlotLabel, string? requestedAxis)
    {
        var normalizedMissing = NormalizeLexicalLookup(missingSlotLabel);
        var normalizedAxis = NormalizeLexicalLookup(requestedAxis);
        if (string.IsNullOrWhiteSpace(normalizedMissing) || string.IsNullOrWhiteSpace(normalizedAxis))
            return false;

        var missingVariants = ExpandLlmAdjudicatedPlanningAxisVariants(normalizedMissing).ToArray();
        var requestedVariants = ExpandLlmAdjudicatedPlanningAxisVariants(normalizedAxis).ToArray();
        return missingVariants.Any(missing => requestedVariants.Any(requested =>
            string.Equals(missing, requested, StringComparison.Ordinal)
            || NormalizedTextContainsTerm(missing, requested)));
    }

    private static IEnumerable<string> ExpandLlmAdjudicatedPlanningAxisVariants(string? axis)
    {
        var normalized = NormalizeLexicalLookup(axis);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;
        foreach (var term in ExtractPlanningAnswerSupportTerms(normalized)
                     .Where(static term => term.Length >= 2)
                     .Distinct(StringComparer.Ordinal)
                     .Take(8))
        {
            yield return term;
        }
    }

    private static bool NormalizedTextContainsTerm(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;

        var pattern = BuildFlexibleNormalizedPlanningTermPattern(term);
        return !string.IsNullOrWhiteSpace(pattern)
               && Regex.IsMatch(text, $@"\b(?:{pattern})\b", RegexOptions.CultureInvariant);
    }

    private static bool JsonBoolEquals(JsonElement element, string propertyName, bool expected)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            return value.GetBoolean() == expected;

        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed == expected;
        }

        return false;
    }

}
