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

    private string BuildSourceBackedLlmWorkingNotesForPrompt(
        string effectiveUserMessage,
        string language,
        int maxLines)
    {
        var notes = _mem.ResearchWorkingNotes;
        if (notes.Count == 0)
            return "none";

        var topicKey = BuildSourceBackedResearchTopicKey(effectiveUserMessage, language);
        var shapeKey = BuildSourceBackedResearchShapeKey(effectiveUserMessage);
        var relevant = notes
            .Where(note =>
                string.Equals(note.TopicKey, topicKey, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(note.RequestShape, shapeKey, StringComparison.OrdinalIgnoreCase)
                    && SharesSourceBackedResearchTopicTerms(note.TopicKey, topicKey)))
            .OrderByDescending(static note => note.CreatedAtUtc)
            .Take(Math.Max(1, maxLines))
            .Select(FormatSourceBackedResearchWorkingNoteForPrompt)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return relevant.Length == 0
            ? "none"
            : FormatPromptList(relevant, maxLines, maxItemLength: 360);
    }

    private static string FormatSourceBackedResearchWorkingNoteForPrompt(ToolMemory.ResearchWorkingNote note)
    {
        var queries = string.Join("; ", note.Queries.Take(4).Select(static query => TruncateForPrompt(query, 90)));
        if (string.IsNullOrWhiteSpace(queries))
            return string.Empty;

        var scopeParts = new[]
        {
            string.IsNullOrWhiteSpace(note.CategoryScope) ? null : $"category={note.CategoryScope}",
            string.IsNullOrWhiteSpace(note.DocPath) ? null : $"doc={note.DocPath}",
            note.PageStart is null ? null : $"pageStart={note.PageStart}",
            note.PageEnd is null ? null : $"pageEnd={note.PageEnd}"
        }.Where(static part => !string.IsNullOrWhiteSpace(part));
        var scope = string.Join(",", scopeParts);
        if (string.IsNullOrWhiteSpace(scope))
            scope = "none";

        var gainParts = new[]
        {
            note.CandidateDelta is null ? null : $"candidates={FormatSignedDelta(note.CandidateDelta.Value)}",
            note.DistinctPageDelta is null ? null : $"pages={FormatSignedDelta(note.DistinctPageDelta.Value)}",
            note.UsableHitDelta is null ? null : $"hits={FormatSignedDelta(note.UsableHitDelta.Value)}"
        }.Where(static part => !string.IsNullOrWhiteSpace(part));
        var gain = string.Join(",", gainParts);
        if (string.IsNullOrWhiteSpace(gain))
            gain = "unknown";

        var reason = note.Accepted
            ? note.ReasonAfter ?? note.ReasonBefore
            : note.RejectReason ?? note.ReasonAfter ?? note.ReasonBefore;
        var guidance = ResolveSourceBackedResearchWorkingNoteGuidance(note);

        return "outcome="
               + FormatPlanningTraceValue(note.Outcome)
               + "|label=" + FormatPlanningTraceValue(note.Label)
               + "|origin=" + FormatPlanningTraceValue(note.Origin)
               + "|queries=" + FormatPlanningTraceValue(queries)
               + "|scope=" + FormatPlanningTraceValue(scope)
               + "|gain=" + FormatPlanningTraceValue(gain)
               + "|reason=" + FormatPlanningTraceValue(reason)
               + "|guidance=" + guidance;
    }

    private static string ResolveSourceBackedResearchWorkingNoteGuidance(ToolMemory.ResearchWorkingNote note)
    {
        if (note.Accepted)
            return "continue_related_pivot_with_new_facets";

        return note.Outcome switch
        {
            "rejected_no_hits" => "avoid_repeat_without_new_terms_or_scope",
            "rejected_no_gain" => "change_axis_scope_or_concrete_label_before_retrying",
            "rejected_regression" => "do_not_reuse_as_primary_route",
            "error" => "retry_only_if_still_necessary_with_narrower_scope",
            _ => "treat_as_low_priority_unless_new_evidence_requires_it"
        };
    }

    private static string FormatSignedDelta(int value)
        => value > 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static bool SharesSourceBackedResearchTopicTerms(string? left, string? right)
    {
        var leftTerms = ExtractSourceBackedResearchTopicKeyTerms(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTerms.Count == 0)
            return false;

        foreach (var term in ExtractSourceBackedResearchTopicKeyTerms(right))
        {
            if (leftTerms.Contains(term))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractSourceBackedResearchTopicKeyTerms(string? topicKey)
    {
        if (string.IsNullOrWhiteSpace(topicKey))
            yield break;

        var parts = topicKey.Split('|');
        if (parts.Length == 0)
            yield break;

        foreach (var term in parts[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(term))
                yield return term;
        }
    }

    private static string BuildSourceBackedResearchTopicKey(string? effectiveUserMessage, string? language)
    {
        var terms = ExtractSourceBackedResearchTopicTerms(effectiveUserMessage)
            .Take(12)
            .ToArray();
        if (terms.Length == 0)
            return string.Empty;

        return string.Join(
            "|",
            NormalizeLanguageCode(language),
            BuildSourceBackedResearchShapeKey(effectiveUserMessage),
            string.Join(" ", terms));
    }

    private static string BuildSourceBackedResearchShapeKey(string? effectiveUserMessage)
    {
        var message = effectiveUserMessage ?? string.Empty;
        var flags = new List<string>();
        if (LooksLikeAnyDocumentaryPlanningRequest(message))
            flags.Add("planning");
        if (LooksLikeGenericCollectionOrListRequest(message))
            flags.Add("collection");
        if (LooksLikeMultipleCandidateSynthesisRequest(message))
            flags.Add("multi_candidate");
        if (LooksLikeSoftChoiceRecommendationRequest(message))
            flags.Add("recommendation");
        if (LooksLikeSourceBackedPairingRecommendationRequest(message))
            flags.Add("pairing");
        if (LooksLikeBroadSourceBackedCompositionRequest(message))
            flags.Add("composition");
        if (LooksLikeUserNeedsSynthesizedDecisionOrPlan(message))
            flags.Add("synthesis");
        if (DetectRequestedPlanningSlotAxisLabels(message, "fr").Count > 0
            || DetectRequestedPlanningSlotAxisLabels(message, "en").Count > 0)
        {
            flags.Add("slot_axes");
        }
        if (DetectRequestedDayAxisLabels(message, "fr").Count > 0
            || DetectRequestedDayAxisLabels(message, "en").Count > 0)
        {
            flags.Add("day_axes");
        }

        return flags.Count == 0
            ? "targeted"
            : string.Join(",", flags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractSourceBackedResearchTopicTerms(string? effectiveUserMessage)
    {
        var normalized = NormalizeLexicalLookup(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(normalized, @"[a-z0-9]{3,}", RegexOptions.CultureInvariant))
        {
            var term = match.Value;
            if (IsSourceBackedResearchTopicStopword(term))
                continue;

            if (seen.Add(term))
                yield return term;
        }
    }

    private static bool IsSourceBackedResearchTopicStopword(string term)
        => term is
            "avec" or "sans" or "pour" or "dans" or "que" or "qui" or "quoi" or "dont" or "des" or "les" or "une" or "sur"
            or "this" or "that" or "with" or "from" or "into" or "about" or "please" or "need" or "want"
            or "besoin" or "fasse" or "fasses" or "mettre" or "mets" or "chaque" or "seulement" or "vraiment"
            or "clear" or "clair" or "claire" or "format" or "utile" or "utiles" or "source" or "sources"
            or "lundi" or "mardi" or "mercredi" or "jeudi" or "vendredi" or "samedi" or "dimanche"
            or "monday" or "tuesday" or "wednesday" or "thursday" or "friday" or "saturday" or "sunday";
}
