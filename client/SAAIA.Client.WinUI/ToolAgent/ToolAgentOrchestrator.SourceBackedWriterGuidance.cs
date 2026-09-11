using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildAnswerShapeGuidanceForWriter(string? query, string language)
    {
        var shape = ResolveRequestedAnswerShape(query);
        var targetLanguage = NormalizeLanguageCode(language);
        var requestedStructure = BuildRequestedStructureGuidanceForWriter(query, targetLanguage);

        var shapeSpecific = shape switch
        {
            "schedule_or_plan" => """
- The user asks for a plan, schedule, program or organized proposal. Build a usable structure that matches the requested granularity when possible: days, slots, phases, options or rotation.
- Every concrete item/action/value must come from the tool results. The organization layer may be yours, but label it as a proposed organization based on the available documented items.
- If the documents do not cover every slot, still provide a useful partial structure and mark missing/uncertain slots as to complete/validate. Do not answer with a raw list of excerpts.
- If the user explicitly gives axes or slots such as weekdays, time periods, phases, roles, priorities or criteria, mirror those axes in the answer. Prefer grouped sections or a compact structured list over one bullet per source.
- If there are fewer distinct documented items than requested places, do not fill the structure by repeating weak items. Place the sourced items where they fit and mark the remaining places as missing/to validate.
- If the available evidence is clearly too small for the requested grid, do not fill the whole grid by repetition. Return a readable partial proposal and explain that more documented items are needed for a complete varied plan.
- If the available evidence remains too small after retrieval, ask one concise question offering to broaden the search/corpus instead of fabricating missing places.
- Infer whether each sourced item fits each requested place from the item title and local evidence. A retrieval/search route is only a hint; it is not enough by itself to force an item into an incompatible place.
- When an item is sourced but does not naturally fit any requested place, keep it as an optional nearby idea or omit it; do not bend the requested structure around it.
- Do not use Markdown pipe tables for plans. In this UI they can appear as raw text; use compact day/slot sections or bullets instead.
- Never fill plan cells with generic background, constraints, document summaries, navigation labels, table-of-contents entries or repeated fragments. A filled place needs a concrete sourced item/action/value that actually fits the place.
- If the sources only contain general advice or context, write a short "usable context" note and leave the requested places to complete/validate instead of turning that context into fake plan entries.
- Do not expose internal wording such as candidate(s), slot(s), coverage or evidence role. Translate that into natural user-facing language.
- Do not repeat the user request. Start with the useful proposal, then add a short source-limit note only where the available evidence is partial.
- Avoid opening with "I can build..." or "the sources do not prove..."; that reads like a refusal instead of a helpful answer.
- If the source set is partial, still write the useful partial proposal first. Do not answer with diagnostics, a source inventory, or a refusal unless there are no usable page-grounded items.
""",
            "comparison" => """
- The user asks to compare. Separate the compared items/sources clearly, then give common points, differences, and limits.
- Do not merge obligations, values or procedures across sources unless the answer explicitly says it is a synthesis.
""",
            "procedure" => """
- The user asks for a method or steps. Present short ordered steps only when the steps are present in the tool results.
- If the retrieved text is partial, give the supported steps first and state what is missing before execution.
""",
            "recommendation" => """
- The user asks for a choice or recommendation. Give a direct recommendation when one candidate is better supported, then explain why from the sources and list alternatives only if useful.
- If the evidence is partial, say the recommendation is documented but not a certified compatibility decision.
""",
            "document_list" => """
- The user asks which documents/sources mention a topic. Return a clean source list with a one-line reason for each source, not a narrative answer.
""",
            "summary" => """
- The user asks for a summary. Synthesize the main points in a readable structure, preserving uncertainty and source limits.
""",
            _ => """
- Adapt the structure to the user's request. Prefer a short useful synthesis over copied excerpts.
"""
        };

        return $"""
Detected response shape: {shape}
Target answer language code: {targetLanguage}
Generic output contract:
- Answer directly in the target language with natural spelling, accents and punctuation.
- Correct obvious OCR/text-extraction damage, missing accents, broken spacing and malformed words when doing so does not change the source facts.
- Do not repeat the user's full question in the opening sentence.
- Do not dump raw excerpts or write bullets whose main content is "document p.N: copied passage".
- Use the retrieved material as evidence, not as prose to paste. Rewrite, group and translate it into a user-friendly answer while keeping concrete facts source-backed.
- Treat headings, table-of-contents entries, profile hints and navigation anchors as private orientation only. They can guide what to say or search next, but they are not enough by themselves to become a proposed item, step or conclusion.
- Do not copy PRIVATE_SOURCE_* or SOURCE_BACKED_* control wording. It is there to guide drafting, not to appear in the final answer.
- Treat private adjudication, codeDecision/codeReason and coverage diagnostics as advisory diagnostic hints, not final vetoes. Your final choice should come from the visible source title/evidence and a concrete citation.
- Avoid mechanical diagnostic phrasing such as "X candidate(s) for Y slot(s)" unless the user explicitly asks for diagnostics.
- Use source names/pages as short references after readable points.
- Source alignment is mandatory for broad plans, recommendations, comparisons and lists: every concrete item, action, value, timing or choice must carry a nearby short reference to the page that supports it, for example "(source: file.pdf p.12)". Use only a source/page that directly supports that item.
- Use the exact visible file/page citation inline for each concrete item, for example "(source: file.pdf p.12)". Do not leave item-level grounding only to the application's final source cards.
- If an item cannot be tied to a concrete page, do not present it as a recommendation. Mark that place as missing/to validate, or explain that the available sources are too limited.
- Do not add a final "Source:" section; clickable source cards are added by the application.
- Do not use Markdown pipe tables. Prefer headings and bullets because the client may display pipe tables as raw text.
- For planning requests, start with the requested structure or proposal. Put source limits after the useful draft, not as the first sentence.
- When source coverage is partial, write a helpful partial draft first, then explain the limit in one short sentence. Do not lead with retrieval diagnostics.
- Normalize obvious extracted titles into natural casing and wording when safe; do not paste all-caps or OCR-damaged headings as-is.
- Preserve source grounding: do not invent concrete facts, items, steps, values, quantities, dates or citations absent from the tool results.
- You may reformulate, group, prioritize and organize sourced evidence so the result is useful to a non-technical user.
{requestedStructure}
{shapeSpecific}
""";
    }

    private static string BuildSourceBackedCoverageHintsForWriter(ToolResults toolResults, string? query, string language)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return "No source-backed coverage hint for this turn.";
        }

        language = NormalizeLanguageCode(language);
        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, query, language);
            var dayAxis = DetectRequestedDayAxisLabels(query, language);
            var periodAxis = DetectRequestedPlanningSlotAxisLabels(query, language);
            var hasExplicitGrid = dayAxis.Count > 0 && periodAxis.Count > 0;

            var sb = new StringBuilder();
            sb.AppendLine($"Private drafting note: source coverage is {(coverage.IsAdequate ? "usable" : "partial")} for this requested structure.");
            sb.AppendLine($"Usable distinct items found: {coverage.CandidateCount}; distinct source pages: {coverage.DistinctSourcePages}; requested cells/items: {coverage.TargetSlots}; hard uniqueness requested: {(RequiresFullyDistinctStructuredPlanningItems(query) ? "yes" : "no")}.");
            if (hasExplicitGrid)
                sb.AppendLine($"Detected requested grid: {dayAxis.Count} day row(s) x {periodAxis.Count} column(s).");
            if (coverage.TargetSlots > coverage.CandidateCount)
            {
                sb.AppendLine("Important: the corpus did not return one distinct item for every requested cell. Decide from the user's wording whether a sourced rotation, a partial proposal, or one clarification question is the most useful answer.");
                sb.AppendLine("Never invent missing concrete items. If you rotate sourced items, say naturally that it is your organization of the available sourced options.");
            }
            else if (!coverage.IsAdequate)
            {
                sb.AppendLine("Important: evidence is partial. Be useful, but keep limits explicit and avoid certifying completeness.");
            }

            return sb.ToString().TrimEnd();
        }

        var broad = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (broad.UsableHitCount == 0)
            return "No usable RAG hit is available. Ask for a narrower scope or offer an expanded search instead of inventing.";

        return $"""
Private drafting note: source coverage is {(broad.IsAdequate ? "usable" : "partial")} for this broad request.
Usable hits: {broad.UsableHitCount}; distinct documents: {broad.DistinctDocumentCount}; distinct source pages: {broad.DistinctSourcePageCount}; rich evidence hits: {broad.RichEvidenceCount}.
If evidence is partial, write the best useful sourced answer possible and state clear limits instead of overclaiming.
""";
    }

    private static string BuildSourceBackedWritingBriefForWriter(ToolResults toolResults, string? query, string language)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return "No source-backed writing brief for this turn.";
        }

        language = NormalizeLanguageCode(language);
        var shape = ResolveRequestedAnswerShape(query);
        var sb = new StringBuilder();
        sb.AppendLine($"Private drafting objective: write a polished user-facing {shape} in language '{language}'.");
        sb.AppendLine("Use the evidence as a fact inventory, not as prose to copy. Rewrite, group, translate/paraphrase and prioritize while keeping every concrete item tied to a source.");
        sb.AppendLine("Do not expose internal control wording: candidate(s), slot(s), coverage, evidenceRole, writerEvidence, tool result, broad synthesis, retrieval, documented lead, candidate bank or option bank.");

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, query, language);
            var dayAxis = DetectRequestedDayAxisLabels(query, language);
            var periodAxis = DetectRequestedPlanningSlotAxisLabels(query, language);
            if (dayAxis.Count > 0 && periodAxis.Count > 0)
            {
                sb.AppendLine("The user requested an explicit grid. Decide whether to fill it with a sourced rotation, provide a partial proposal, or ask one clarifying question. The decision should follow the user's wording, not a fixed source count.");
                sb.AppendLine("For each grid place, use your own judgment from the visible source title and local evidence. Search routes, retrieval labels and code diagnostics are discovery hints, not final proof or vetoes.");
                sb.AppendLine("If a source gives a concrete usable item, you may place it in a reasonable slot or rotation when that helps the user. Leave a place incomplete only when you cannot cite any real source for the item you would write there.");
                sb.AppendLine("When the evidence inventory provides a citation attribute, copy that exact citation after the corresponding item.");
            }
            else
            {
                sb.AppendLine("The user wants an organized proposal. Prefer useful sections or a short ranked option list over one bullet per source.");
            }

            if (!coverage.IsAdequate)
            {
                sb.AppendLine("Evidence is partial: be helpful but do not invent missing concrete items. If repeating sourced options is acceptable for the user's intent, make the rotation explicit; otherwise keep the answer partial or ask one concise clarification.");
                sb.AppendLine("Start with a useful partial proposal from the sourced items, not with a diagnostic. Then add one short source-limit note and offer to broaden the search only if a complete answer is needed.");
            }
            else
            {
                sb.AppendLine("Evidence coverage is acceptable: write the requested structure directly, then mention limits only if extraction quality or source scope requires it.");
            }

            return sb.ToString().TrimEnd();
        }

        var broad = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (!broad.IsAdequate)
        {
            sb.AppendLine("Evidence is partial: write the best source-backed answer possible, then state exactly what remains uncertain. Do not turn the answer into a raw evidence list or a retrieval diagnostic.");
        }
        else
        {
            sb.AppendLine("Evidence coverage is acceptable: synthesize naturally and cite sources only as short references.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSourceBackedResearchMapForWriter(
        ToolResults rawToolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "none";

        var hasOrientationSurface = rawToolResults.Items.Any(static item =>
            (item.ToolName is "documents.tree" or "documents.navigation" or "summary.search")
            && string.IsNullOrWhiteSpace(item.Error));
        var hasPreviousSources = lastSourcesUsed is { Count: > 0 };
        if (!hasOrientationSurface && !hasPreviousSources)
            return "none";

        var hints = BuildSourceBackedStructureHintsForPrompt(rawToolResults, lastSourcesUsed, query, language);
        if (string.IsNullOrWhiteSpace(hints) || string.Equals(hints, "none", StringComparison.OrdinalIgnoreCase))
            return "none";

        var lines = hints
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => CollapseWhitespace(line))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(FormatSourceBackedResearchMapLineForWriter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .ToArray();
        if (lines.Length == 0)
            return "none";

        var sb = new StringBuilder();
        sb.AppendLine("Private research map. Do not expose this section or its labels to the user.");
        sb.AppendLine("Use it only to understand folders, stored summaries, headings, indexes, tables of contents or title anchors discovered during retrieval.");
        sb.AppendLine("Do not cite or present a map item as a fact unless the same item is also supported by page-grounded TOOL_RESULTS evidence.");
        foreach (var line in lines)
        {
            sb.Append("- ");
            sb.AppendLine(TruncateForPrompt(line, 260));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSourceBackedResearchMapLineForWriter(string line)
    {
        var cleaned = CollapseWhitespace(line);
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        var surfaceType = "orientation";
        var sourceScope = "retrieval_hint";
        var value = cleaned;
        var match = Regex.Match(
            cleaned,
            @"^(?<prefix>navigationOnly|orientationOnly)\s+(?<kind>[A-Za-z0-9_.-]+)\s*:\s*(?<value>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            surfaceType = NormalizeSourceBackedResearchMapSurfaceType(match.Groups["kind"].Value);
            sourceScope = ResolveSourceBackedResearchMapScope(surfaceType);
            value = CollapseWhitespace(match.Groups["value"].Value);
        }

        return $"surfaceType={surfaceType}; sourceScope={sourceScope}; isFinalEvidence=false; requiresConcreteRetrieval=true; {value}";
    }

    private static string NormalizeSourceBackedResearchMapSurfaceType(string? kind)
    {
        var normalized = NormalizeLexicalLookup(kind);
        if (string.IsNullOrWhiteSpace(normalized))
            return "orientation";

        if (normalized.Contains("summary", StringComparison.Ordinal))
            return "stored_summary";
        if (normalized.Contains("documentnavigation", StringComparison.Ordinal)
            || normalized.Contains("navigation", StringComparison.Ordinal))
            return "document_navigation";
        if (normalized.Contains("tree", StringComparison.Ordinal))
            return "document_tree";
        if (normalized.Contains("previous", StringComparison.Ordinal))
            return "previous_source";
        if (normalized.Contains("profile", StringComparison.Ordinal))
            return "document_profile";

        return "orientation";
    }

    private static string ResolveSourceBackedResearchMapScope(string surfaceType)
        => surfaceType switch
        {
            "stored_summary" => "document_profile",
            "document_navigation" => "toc_or_index",
            "document_tree" => "category_tree",
            "previous_source" => "conversation_source",
            "document_profile" => "document_profile",
            _ => "retrieval_hint"
        };

    private static string BuildRequestedStructureGuidanceForWriter(string? query, string language)
    {
        var dayLabels = DetectRequestedDayAxisLabels(query, language);
        var slotLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
        if (dayLabels.Count == 0 && slotLabels.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Detected explicit structure from the user message:");
        if (dayLabels.Count > 0)
            sb.AppendLine($"- Day axis requested: {string.Join(" | ", dayLabels)}");
        if (slotLabels.Count > 0)
            sb.AppendLine($"- Slot/type axis requested: {string.Join(" | ", slotLabels)}");

        if (dayLabels.Count > 0 && slotLabels.Count > 0)
        {
            sb.AppendLine("- Use compact day sections with the requested slots inside each section when that is readable.");
            sb.AppendLine("- Fill places only with concrete sourced candidates; if a place cannot be supported, write a short 'to validate/complete' marker instead of hiding the gap.");
        }
        else
        {
            sb.AppendLine("- Mirror this detected axis explicitly in the answer instead of returning a loose list.");
        }

        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> DetectRequestedDayAxisLabels(string? query, string language)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var labels = LocalizedWeekdayLabels(language);
        var requestedDayCount = TryResolveExplicitRequestedDayCount(normalized);
        if (requestedDayCount is >= 2)
            return labels.Take(Math.Clamp(requestedDayCount.Value, 2, labels.Length)).ToArray();

        if (Regex.IsMatch(
                normalized,
                @"\b(?:lundi\s+(?:a|au|jusqu(?:a| au)?)\s+(?:vendredi|vrendredi)|monday\s+(?:to|through|-)\s+friday|lunes\s+(?:a|hasta|-)\s+viernes|segunda\s+(?:a|ate|-)\s+sexta|montag\s+(?:bis|-)\s+freitag|lunedi\s+(?:a|fino a|-)\s+venerdi)\b",
                RegexOptions.CultureInvariant))
        {
            return labels.Take(5).ToArray();
        }

        var aliases = new[]
        {
            new[] { "lundi", "monday", "lunes", "segunda", "montag", "lunedi" },
            new[] { "mardi", "tuesday", "martes", "terca", "dienstag", "martedi" },
            new[] { "mercredi", "wednesday", "miercoles", "quarta", "mittwoch", "mercoledi" },
            new[] { "jeudi", "thursday", "jueves", "quinta", "donnerstag", "giovedi" },
            new[] { "vendredi", "vrendredi", "friday", "viernes", "sexta", "freitag", "venerdi" },
            new[] { "samedi", "saturday", "sabado", "samstag", "sabato" },
            new[] { "dimanche", "sunday", "domingo", "sonntag", "domenica" }
        };

        var found = new List<int>();
        for (var i = 0; i < aliases.Length; i++)
        {
            if (aliases[i].Any(alias => Regex.IsMatch(normalized, @"\b" + Regex.Escape(alias) + @"\b", RegexOptions.CultureInvariant)))
                found.Add(i);
        }

        return found.Count >= 2
            ? found.Distinct().OrderBy(static index => index).Select(index => labels[index]).ToArray()
            : Array.Empty<string>();
    }

    private static int? TryResolveExplicitRequestedDayCount(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var numericMatch = Regex.Match(
            normalized,
            @"\b(?<n>[2-7])\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b",
            RegexOptions.CultureInvariant);
        if (numericMatch.Success
            && int.TryParse(numericMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericCount))
        {
            return numericCount;
        }

        var wordCounts = new (string Pattern, int Count)[]
        {
            (@"\b(?:deux|two|dos|dois|duas|zwei|due)\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b", 2),
            (@"\b(?:trois|three|tres|trÃªs|drei|tre)\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b", 3),
            (@"\b(?:quatre|four|cuatro|quatro|vier|quattro)\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b", 4),
            (@"\b(?:cinq|five|cinco|funf|fÃ¼nf|cinque)\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b", 5),
            (@"\b(?:six|seis|sechs|sei)\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b", 6),
            (@"\b(?:sept|seven|siete|sete|sieben|sette)\s+(?:jours?|days?|dias?|tagen?|tage|giorni)\b", 7)
        };

        foreach (var (pattern, count) in wordCounts)
        {
            if (Regex.IsMatch(normalized, pattern, RegexOptions.CultureInvariant))
                return count;
        }

        return null;
    }

    private static IReadOnlyList<string> DetectRequestedPlanningSlotAxisLabels(string? query, string language)
    {
        var labels = ExtractPlanningSlotRetrievalTerms(query)
            .Select(NormalizeLexicalLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return CollapseExplicitAlternativePlanningSlotAxisLabels(query, labels);
    }

    private static IReadOnlyList<string> CollapseExplicitAlternativePlanningSlotAxisLabels(
        string? query,
        IReadOnlyList<string> labels)
    {
        if (labels.Count <= 1)
            return labels;

        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return labels;

        var removed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < labels.Count; i++)
        {
            var left = labels[i];
            if (string.IsNullOrWhiteSpace(left) || removed.Contains(left))
                continue;

            for (var j = 0; j < labels.Count; j++)
            {
                if (i == j)
                    continue;

                var right = labels[j];
                if (string.IsNullOrWhiteSpace(right) || removed.Contains(right))
                    continue;

                if (PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedQuery, left, right))
                {
                    removed.Add(left);
                    break;
                }
            }
        }

        return labels
            .Where(label => !removed.Contains(label))
            .ToArray();
    }

    private static bool PlanningSlotAxisLabelsAreExplicitAlternatives(
        string normalizedQuery,
        string left,
        string right)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)
            || string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right)
            || string.Equals(left, right, StringComparison.Ordinal))
        {
            return false;
        }

        var leftPattern = BuildFlexibleNormalizedPlanningTermPattern(left);
        var rightPattern = BuildFlexibleNormalizedPlanningTermPattern(right);
        if (string.IsNullOrWhiteSpace(leftPattern) || string.IsNullOrWhiteSpace(rightPattern))
            return false;

        return Regex.IsMatch(
            normalizedQuery,
            $@"\b(?:{leftPattern})\b\s*(?:/|\bou\b|\bor\b)\s*\b(?:{rightPattern})\b",
            RegexOptions.CultureInvariant);
    }

    private static string BuildFlexibleNormalizedPlanningTermPattern(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return Regex.Escape(normalized)
            .Replace("\\ ", @"\s+", StringComparison.Ordinal)
            .Replace("\\-", @"[-\s]+", StringComparison.Ordinal);
    }

    private static string[] LocalizedWeekdayLabels(string language)
        => NormalizeLanguageCode(language) switch
        {
            "en" => new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
            "es" => new[] { "Lunes", "Martes", "MiÃ©rcoles", "Jueves", "Viernes", "SÃ¡bado", "Domingo" },
            "pt" => new[] { "Segunda", "TerÃ§a", "Quarta", "Quinta", "Sexta", "SÃ¡bado", "Domingo" },
            "de" => new[] { "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag" },
            "it" => new[] { "LunedÃ¬", "MartedÃ¬", "MercoledÃ¬", "GiovedÃ¬", "VenerdÃ¬", "Sabato", "Domenica" },
            _ => new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi", "Dimanche" }
        };
}
