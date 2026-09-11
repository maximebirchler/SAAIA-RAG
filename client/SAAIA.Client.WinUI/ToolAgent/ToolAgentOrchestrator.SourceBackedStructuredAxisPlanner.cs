using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> passes,
        string? query,
        string language,
        string? plannedCategoryScope,
        out string[] diagnosticPassLabels,
        out string[] diagnosticQuerySamples)
    {
        diagnosticPassLabels = Array.Empty<string>();
        diagnosticQuerySamples = Array.Empty<string>();
        if (passes.Count == 0 || !ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return passes;

        language = NormalizeLanguageCode(language);
        var dayAxis = DetectRequestedDayAxisLabels(query, language);
        var periodAxis = DetectRequestedPlanningSlotAxisLabels(query, language);
        var requestedSlotLabels = periodAxis
            .Concat(ExtractPlanningSlotRetrievalTerms(query))
            .Select(NormalizeLexicalLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (dayAxis.Count < 2 || requestedSlotLabels.Length == 0)
            return passes;

        var dayTerms = BuildStructuredAxisPlannerDayTerms(dayAxis, language)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var slotTerms = BuildStructuredAxisPlannerSlotTerms(requestedSlotLabels, query)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (dayTerms.Length == 0 || slotTerms.Length == 0)
            return passes;

        var slotTermGroups = BuildStructuredAxisPlannerSlotTermGroups(requestedSlotLabels, query);
        var genericInventoryTerms = BuildStructuredAxisPlannerGenericInventoryTerms(language, query)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var querySubjectTerms = ExtractStructuredAxisPlannerUnknownSignalTerms(
                NormalizeLexicalLookup(query),
                dayTerms,
                slotTerms,
                genericInventoryTerms)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var minimumDistinctSlotCoverage = Math.Min(2, Math.Max(1, requestedSlotLabels.Length));
        var kept = new List<SourceBackedEvidenceExplorationPass>(passes.Count);
        var diagnosticLabels = new List<string>();
        var diagnosticQueries = new List<string>();
        var changed = false;
        foreach (var pass in passes)
        {
            if (ShouldRejectLowQualityStructuredAxisLlmEvidenceExplorationPass(
                    pass,
                    dayTerms,
                    slotTerms,
                    slotTermGroups,
                    genericInventoryTerms,
                    querySubjectTerms,
                    minimumDistinctSlotCoverage))
            {
                diagnosticLabels.Add(pass.Label);
                diagnosticQueries.AddRange(pass.Queries.Take(4));
                changed = true;
            }

            var sanitizedQueries = SanitizeStructuredAxisLlmEvidenceExplorationQueries(
                pass.Queries,
                dayTerms,
                slotTerms,
                slotTermGroups,
                genericInventoryTerms,
                querySubjectTerms);

            if (sanitizedQueries.Length == 0)
            {
                changed = true;
                kept.Add(pass);
                continue;
            }

            if (sanitizedQueries.SequenceEqual(pass.Queries, StringComparer.Ordinal))
            {
                kept.Add(pass);
            }
            else
            {
                changed = true;
                kept.Add(pass with { Queries = sanitizedQueries });
            }
        }

        if (!changed && diagnosticLabels.Count == 0)
            return passes;

        diagnosticPassLabels = diagnosticLabels.ToArray();
        diagnosticQuerySamples = diagnosticQueries.ToArray();
        return kept;
    }

    private static bool ShouldRejectLowQualityStructuredAxisLlmEvidenceExplorationPass(
        SourceBackedEvidenceExplorationPass pass,
        IReadOnlyCollection<string> dayTerms,
        IReadOnlyCollection<string> slotTerms,
        IReadOnlyList<string[]> slotTermGroups,
        IReadOnlyCollection<string> genericInventoryTerms,
        IReadOnlyCollection<string> querySubjectTerms,
        int minimumDistinctSlotCoverage)
    {
        var queries = pass.Queries
            .Select(NormalizeLexicalLookup)
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .ToArray();
        if (queries.Length < 3)
            return false;

        var axisOnlyQueries = 0;
        var slotOrSpecificQueries = 0;
        var dayScaffoldQueries = 0;
        var specificQueries = 0;
        var distinctSlotTerms = new HashSet<string>(StringComparer.Ordinal);
        var distinctSlotGroups = new HashSet<int>();
        var genericOnlyQueries = 0;
        var decorativeGenericQueries = 0;
        var repeatedDayScaffoldSpecificTerms = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var normalizedQuery in queries)
        {
            var mentionsDayAxis = dayTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));
            var mentionsSlot = MentionsStructuredAxisSlotTerm(normalizedQuery, slotTerms);
            var mentionsGenericInventory = genericInventoryTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));
            var hasSpecificSignal = HasStructuredAxisPlannerSpecificSignal(
                normalizedQuery,
                dayTerms,
                slotTerms,
                genericInventoryTerms,
                querySubjectTerms);
            AddStructuredAxisPlannerSlotMatches(normalizedQuery, slotTerms, distinctSlotTerms);
            AddStructuredAxisPlannerSlotGroupMatches(normalizedQuery, slotTermGroups, distinctSlotGroups);
            if (!hasSpecificSignal && LooksLikeDecorativeStructuredAxisPlannerQuery(normalizedQuery))
                decorativeGenericQueries++;
            if (mentionsDayAxis && !mentionsSlot)
            {
                var specificTerms = ExtractStructuredAxisPlannerUnknownSignalTerms(
                        normalizedQuery,
                        dayTerms,
                        slotTerms,
                        genericInventoryTerms,
                        querySubjectTerms)
                    .Take(3)
                    .ToArray();
                if (specificTerms.Length is > 0 and <= 2)
                {
                    var key = string.Join(" ", specificTerms);
                    repeatedDayScaffoldSpecificTerms[key] = repeatedDayScaffoldSpecificTerms.TryGetValue(key, out var count)
                        ? count + 1
                        : 1;
                }
            }

            if (!mentionsDayAxis)
            {
                if (mentionsSlot || hasSpecificSignal)
                {
                    slotOrSpecificQueries++;
                    if (hasSpecificSignal)
                        specificQueries++;
                }

                if (!mentionsSlot && !hasSpecificSignal && mentionsGenericInventory)
                    genericOnlyQueries++;

                continue;
            }

            if (!mentionsSlot && !hasSpecificSignal)
            {
                axisOnlyQueries++;
                if (mentionsGenericInventory)
                    genericOnlyQueries++;
            }
            else
            {
                slotOrSpecificQueries++;
                if (hasSpecificSignal)
                    specificQueries++;
                else
                    dayScaffoldQueries++;
            }
        }

        var mostlyAxisOnly = axisOnlyQueries >= Math.Max(3, (queries.Length * 2 + 2) / 3);
        if (mostlyAxisOnly && slotOrSpecificQueries == 0)
            return true;

        var mostlyDayScaffold = (axisOnlyQueries + dayScaffoldQueries) >= Math.Max(3, (queries.Length * 2 + 2) / 3);
        if (mostlyDayScaffold
            && specificQueries == 0
            && CountStructuredAxisPlannerSlotCoverage(distinctSlotTerms, distinctSlotGroups) < minimumDistinctSlotCoverage)
        {
            return true;
        }

        var lowCoverageKnownTermLoop = queries.Length >= 6
            && specificQueries == 0
            && CountStructuredAxisPlannerSlotCoverage(distinctSlotTerms, distinctSlotGroups) < minimumDistinctSlotCoverage
            && (slotOrSpecificQueries + genericOnlyQueries) >= Math.Max(4, (queries.Length + 1) / 2);
        if (lowCoverageKnownTermLoop)
            return true;

        var repeatedDayScaffoldThreshold = Math.Max(3, Math.Min(5, (queries.Length + 1) / 2));
        if (CountStructuredAxisPlannerSlotCoverage(distinctSlotTerms, distinctSlotGroups) < minimumDistinctSlotCoverage
            && repeatedDayScaffoldSpecificTerms.Values.Any(count => count >= repeatedDayScaffoldThreshold))
        {
            return true;
        }

        return queries.Length >= 4
            && specificQueries == 0
            && decorativeGenericQueries >= Math.Max(4, (queries.Length * 2 + 2) / 3);
    }

    private static bool LooksLikeDecorativeStructuredAxisPlannerQuery(string normalizedQuery)
        => Regex.IsMatch(
            normalizedQuery,
            @"\b(?:exemples?|examples?|candidates?|candidats?|candidatos?|candidati|kandidaten|id[eÃ©]es?|ideas?|suggestions?|id[eÃ©]aux|ideals?|details?|detailed|d[eÃ©]taill[eÃ©]s?)\b",
            RegexOptions.CultureInvariant);

    private static string[] SanitizeStructuredAxisLlmEvidenceExplorationQueries(
        IReadOnlyList<string> queries,
        IReadOnlyCollection<string> dayTerms,
        IReadOnlyCollection<string> slotTerms,
        IReadOnlyList<string[]> slotTermGroups,
        IReadOnlyCollection<string> genericInventoryTerms,
        IReadOnlyCollection<string> querySubjectTerms)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            var normalizedQuery = NormalizeLexicalLookup(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
                continue;

            var mentionsDayAxis = dayTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));
            var mentionsSlot = MentionsStructuredAxisSlotTerm(normalizedQuery, slotTerms);
            var mentionsGenericInventory = genericInventoryTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));
            var mentionsCandidateInventory = MentionsStructuredAxisCandidateInventoryTerm(normalizedQuery, genericInventoryTerms);
            var hasSpecificSignal = HasStructuredAxisPlannerSpecificSignal(
                normalizedQuery,
                dayTerms,
                slotTerms,
                genericInventoryTerms,
                querySubjectTerms);
            var unknownSignalTerms = ExtractStructuredAxisPlannerUnknownSignalTerms(
                normalizedQuery,
                dayTerms,
                slotTerms,
                genericInventoryTerms,
                querySubjectTerms);
            var inventoryReplacements = BuildStructuredAxisInventoryReplacementsForAbstractLlmQuery(
                normalizedQuery,
                slotTerms,
                genericInventoryTerms,
                mentionsSlot,
                mentionsCandidateInventory);
            if (inventoryReplacements.Length > 0)
            {
                foreach (var replacement in inventoryReplacements)
                {
                    var replacementKey = NormalizeLexicalLookup(replacement);
                    if (!string.IsNullOrWhiteSpace(replacementKey) && seen.Add(replacementKey))
                        results.Add(replacement);
                }

                continue;
            }

            if (LooksLikeStructuredAxisPlanningScaffoldQuery(normalizedQuery)
                && !mentionsSlot
                && !mentionsCandidateInventory
                && unknownSignalTerms.Length <= 1)
            {
                continue;
            }

            if (LooksLikeDecorativeStructuredAxisPlannerQuery(normalizedQuery)
                && unknownSignalTerms.Length <= 1)
            {
                continue;
            }

            var sanitized = mentionsDayAxis && !hasSpecificSignal
                ? RemoveStructuredAxisPlannerTermsFromQuery(normalizedQuery, dayTerms)
                : CollapseWhitespace(query);
            sanitized = CleanupStructuredAxisPlannerQuery(sanitized);
            if (string.IsNullOrWhiteSpace(sanitized))
                continue;

            var key = NormalizeLexicalLookup(sanitized);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            results.Add(sanitized);
        }

        CompleteStructuredAxisLlmEvidenceExplorationQueries(
            results,
            seen,
            slotTermGroups,
            genericInventoryTerms);

        return results.ToArray();
    }

    private static void CompleteStructuredAxisLlmEvidenceExplorationQueries(
        List<string> results,
        HashSet<string> seen,
        IReadOnlyList<string[]> slotTermGroups,
        IReadOnlyCollection<string> genericInventoryTerms)
    {
        if (slotTermGroups.Count == 0)
            return;

        var inventoryTerm = genericInventoryTerms
            .Where(IsStructuredAxisCandidateInventoryTerm)
            .Select(CleanupStructuredAxisPlannerQuery)
            .FirstOrDefault(static term => !string.IsNullOrWhiteSpace(term));
        if (string.IsNullOrWhiteSpace(inventoryTerm))
            inventoryTerm = "options";

        var normalizedQueries = results
            .Select(NormalizeLexicalLookup)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .ToArray();
        foreach (var group in slotTermGroups)
        {
            var groupTerms = group
                .Select(NormalizeLexicalLookup)
                .Where(static term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (groupTerms.Length == 0)
                continue;

            if (normalizedQueries.Any(query => groupTerms.Any(term => ContainsStructuredAxisPlannerTerm(query, term))))
                continue;

            var preferredSlot = groupTerms
                .Where(static term => term.Length >= 4)
                .OrderBy(static term => term.Length)
                .FirstOrDefault() ?? groupTerms[0];
            AddCompletedQuery($"{preferredSlot} {inventoryTerm}");
            AddCompletedQuery($"{inventoryTerm} {preferredSlot}");
            normalizedQueries = results
                .Select(NormalizeLexicalLookup)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .ToArray();
        }

        void AddCompletedQuery(string value)
        {
            value = CleanupStructuredAxisPlannerQuery(value);
            var key = NormalizeLexicalLookup(value);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                results.Add(value);
        }
    }

    private static string RemoveStructuredAxisPlannerTermsFromQuery(
        string normalizedQuery,
        IReadOnlyCollection<string> axisTerms)
    {
        var result = normalizedQuery;
        foreach (var term in axisTerms.OrderByDescending(static term => term.Length))
        {
            if (string.IsNullOrWhiteSpace(term))
                continue;

            var pattern = @"\b" + Regex.Escape(term).Replace("\\ ", @"\s+") + @"\b";
            result = Regex.Replace(result, pattern, " ", RegexOptions.CultureInvariant);
        }

        return result;
    }

    private static string CleanupStructuredAxisPlannerQuery(string query)
        => Regex.Replace(query ?? string.Empty, @"[\s_/,;:()]+", " ").Trim();

    private static string[] BuildStructuredAxisInventoryReplacementsForAbstractLlmQuery(
        string normalizedQuery,
        IReadOnlyCollection<string> slotTerms,
        IReadOnlyCollection<string> genericInventoryTerms,
        bool mentionsSlot,
        bool mentionsCandidateInventory)
    {
        if (!mentionsSlot
            || mentionsCandidateInventory
            || !LooksLikeStructuredAxisPlanningScaffoldQuery(normalizedQuery))
        {
            return Array.Empty<string>();
        }

        var inventoryTerm = genericInventoryTerms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Where(static term => !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(term)))
            .DefaultIfEmpty("options")
            .First();
        var replacements = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slotTerms.Where(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term)))
        {
            Add($"{slot} {inventoryTerm}");
            Add($"{inventoryTerm} {slot}");
        }

        return replacements.Take(8).ToArray();

        void Add(string value)
        {
            value = CleanupStructuredAxisPlannerQuery(value);
            var key = NormalizeLexicalLookup(value);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                replacements.Add(value);
        }
    }

    private static bool LooksLikeStructuredAxisPlanningScaffoldQuery(string normalizedQuery)
        => Regex.IsMatch(
            normalizedQuery,
            @"\b(?:plan|planning|programme|schedule|agenda|calendrier|semaine|hebdomadaire|week|weekly|semana|semanal|woche|wochenplan|settimana|settimanale)\b",
            RegexOptions.CultureInvariant);

    private static bool MentionsStructuredAxisCandidateInventoryTerm(
        string normalizedQuery,
        IReadOnlyCollection<string> genericInventoryTerms)
        => genericInventoryTerms
            .Where(IsStructuredAxisCandidateInventoryTerm)
            .Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));

    private static bool IsStructuredAxisCandidateInventoryTerm(string? term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeDecorativeStructuredAxisPlannerQuery(normalized))
            return false;

        return normalized is not (
            "plan" or "planning" or "programme" or "schedule" or "calendar" or "calendrier" or
            "detail" or "details" or "detailed" or "detaille" or "detailles" or
            "idee" or "idees" or "idea" or "ideas" or "ideal" or "ideals" or "ideaux" or
            "suggestion" or "suggestions");
    }

    private static IEnumerable<string> BuildStructuredAxisPlannerDayTerms(
        IReadOnlyCollection<string> requestedDayLabels,
        string language)
    {
        foreach (var label in requestedDayLabels.Concat(LocalizedWeekdayLabels(language)))
        {
            var normalized = NormalizeLexicalLookup(label);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }

        var weekdayAliases = new[]
        {
            "lundi", "mardi", "mercredi", "jeudi", "vendredi", "vrendredi", "samedi", "dimanche",
            "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
            "lunes", "martes", "miercoles", "jueves", "viernes", "sabado", "domingo",
            "segunda", "terca", "quarta", "quinta", "sexta",
            "montag", "dienstag", "mittwoch", "donnerstag", "freitag", "samstag", "sonntag",
            "lunedi", "martedi", "mercoledi", "giovedi", "venerdi", "sabato", "domenica"
        };
        foreach (var alias in weekdayAliases)
            yield return alias;
    }

    private static IEnumerable<string> BuildStructuredAxisPlannerSlotTerms(
        IReadOnlyCollection<string> requestedPeriodLabels,
        string? query)
    {
        foreach (var label in requestedPeriodLabels)
        {
            foreach (var variant in ExpandPlanningSlotRetrievalTermVariants(label))
            {
                var normalized = NormalizeLexicalLookup(variant);
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }

        foreach (var term in ExtractPlanningSlotRetrievalTerms(query))
        {
            foreach (var variant in ExpandPlanningSlotRetrievalTermVariants(term))
            {
                var normalized = NormalizeLexicalLookup(variant);
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }
    }

    private static IReadOnlyList<string[]> BuildStructuredAxisPlannerSlotTermGroups(
        IReadOnlyCollection<string> requestedPeriodLabels,
        string? query)
    {
        var groups = new List<HashSet<string>>();
        var normalizedQuery = NormalizeLexicalLookup(query);

        void AddGroup(string? label)
        {
            var variants = ExpandPlanningSlotRetrievalTermVariants(label)
                .Select(NormalizeLexicalLookup)
                .Where(static variant => !string.IsNullOrWhiteSpace(variant))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (variants.Length == 0)
                return;

            var existing = groups.FirstOrDefault(group => variants.Any(group.Contains)
                || (!string.IsNullOrWhiteSpace(normalizedQuery)
                    && group.Any(existingTerm => variants.Any(variant =>
                        PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedQuery, existingTerm, variant)
                        || PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedQuery, variant, existingTerm)))));
            if (existing is null)
            {
                groups.Add(new HashSet<string>(variants, StringComparer.Ordinal));
                return;
            }

            foreach (var variant in variants)
                existing.Add(variant);
        }

        foreach (var label in requestedPeriodLabels)
            AddGroup(label);

        foreach (var term in ExtractPlanningSlotRetrievalTerms(query))
            AddGroup(term);

        return groups
            .Select(static group => group.OrderBy(static term => term, StringComparer.Ordinal).ToArray())
            .ToArray();
    }

    private static IEnumerable<string> BuildStructuredAxisPlannerGenericInventoryTerms(string language, string? query)
    {
        foreach (var term in BuildGenericStructuredPlanningInventoryTerms(language, query))
        {
            var normalized = NormalizeLexicalLookup(term);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }

        var fallbackTerms = new[]
        {
            "option", "options", "candidate", "candidates", "candidat", "candidats", "example", "examples",
            "exemple", "exemples", "proposal", "proposals", "proposition", "propositions", "preparation",
            "preparations", "plan", "planning", "programme", "schedule", "calendar", "calendrier",
            "item", "items", "element", "elements", "rubrique", "rubriques", "entry", "entries",
            "options", "option", "options", "detail", "details", "detailed", "detaille", "detailles",
            "idee", "idees", "idea", "ideas", "ideal", "ideals", "ideaux",
            "suggestion", "suggestions"
        };
        foreach (var term in fallbackTerms)
            yield return term;
    }

    private static bool MentionsStructuredAxisSlotTerm(
        string normalizedQuery,
        IReadOnlyCollection<string> slotTerms)
        => slotTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));

    private static void AddStructuredAxisPlannerSlotMatches(
        string normalizedQuery,
        IReadOnlyCollection<string> slotTerms,
        HashSet<string> matches)
    {
        foreach (var term in slotTerms)
        {
            if (ContainsStructuredAxisPlannerTerm(normalizedQuery, term))
                matches.Add(term);
        }
    }

    private static void AddStructuredAxisPlannerSlotGroupMatches(
        string normalizedQuery,
        IReadOnlyList<string[]> slotTermGroups,
        HashSet<int> matches)
    {
        for (var i = 0; i < slotTermGroups.Count; i++)
        {
            if (slotTermGroups[i].Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term)))
                matches.Add(i);
        }
    }

    private static int CountStructuredAxisPlannerSlotCoverage(
        IReadOnlyCollection<string> distinctSlotTerms,
        IReadOnlyCollection<int> distinctSlotGroups)
        => distinctSlotGroups.Count > 0 ? distinctSlotGroups.Count : distinctSlotTerms.Count;

    private static bool HasStructuredAxisPlannerSpecificSignal(
        string normalizedQuery,
        params IReadOnlyCollection<string>[] knownTermGroups)
        => ExtractStructuredAxisPlannerUnknownSignalTerms(normalizedQuery, knownTermGroups)
            .Length > 0;

    private static string[] ExtractStructuredAxisPlannerUnknownSignalTerms(
        string normalizedQuery,
        params IReadOnlyCollection<string>[] knownTermGroups)
        => ExtractQuerySignalTerms(normalizedQuery)
            .Where(term => !IsGenericPlanningCoverageTerm(term))
            .Where(term => knownTermGroups.All(group => !StructuredAxisPlannerTermBelongsToKnownTerm(term, group)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool StructuredAxisPlannerTermBelongsToKnownTerm(
        string term,
        IReadOnlyCollection<string> knownTerms)
        => knownTerms.Any(knownTerm =>
            string.Equals(term, knownTerm, StringComparison.Ordinal)
            || ContainsStructuredAxisPlannerTerm(knownTerm, term));

    private static bool ContainsStructuredAxisPlannerTerm(string normalizedText, string normalizedTerm)
    {
        if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedTerm))
            return false;

        var text = Regex.Replace(normalizedText, @"[\s\-_]+", " ");
        var term = Regex.Replace(normalizedTerm, @"[\s\-_]+", " ");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;

        var pattern = @"\b" + Regex.Escape(term).Replace("\\ ", @"\s+") + @"\b";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);
    }

}
