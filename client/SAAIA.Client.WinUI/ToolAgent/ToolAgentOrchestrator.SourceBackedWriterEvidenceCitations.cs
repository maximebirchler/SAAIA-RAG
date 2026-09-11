using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string ReplaceWriterEvidenceIdReferencesWithCitations(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language,
        bool stripExistingInlineSourceCitations = false,
        bool removeUnresolvedEvidenceIds = false)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return string.Empty;

        var citationById = BuildWriterEvidenceCitationMap(toolResults, query ?? string.Empty, language);
        if (citationById.Count == 0)
            return answer.Trim();

        var normalized = stripExistingInlineSourceCitations
            ? StripInlineSourceCitationsForEvidenceIdMapping(answer)
            : answer.Trim();
        foreach (var pair in citationById)
        {
            var escapedId = Regex.Escape(pair.Key);
            var pattern = $@"(?<![A-Za-z0-9])(?:\[{escapedId}\]|\({escapedId}\)|source\s*:\s*{escapedId}|citation\s*:\s*{escapedId}|{escapedId})(?![A-Za-z0-9])";
            normalized = Regex.Replace(
                normalized,
                pattern,
                pair.Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (removeUnresolvedEvidenceIds)
            normalized = RemoveUnresolvedWriterEvidenceIds(normalized);

        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
            normalized = NormalizeStructuredPlanningCitationOrderAfterEvidenceMapping(normalized, query);

        return normalized.Trim();
    }

    private static string NormalizeStructuredPlanningCitationOrderAfterEvidenceMapping(string answer, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer))
        {
            return answer.Trim();
        }

        var normalizedNewlines = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalizedNewlines.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = MoveLeadingStructuredPlanningCitationAfterItemText(lines[i]);
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string MoveLeadingStructuredPlanningCitationAfterItemText(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;

        var compact = CollapseWhitespace(line);
        if (!LooksLikeStructuredPlanningCellLine(compact))
            return line;

        var match = Regex.Match(
            line,
            @"^(?<prefix>\s*(?:[-*\u2022\u25E6]|\d+[.)])?\s*[^:\r\n]{1,140}:\s*)(?<citation>\(\s*sources?\s*:\s*[^)\r\n]{1,180}\bp\.?\s*\d+(?:\s*[-\u2013]\s*\d+)?[^)\r\n]*\))\s*(?<tail>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return line;

        var itemText = CleanStructuredPlanningCitationLeadingTail(match.Groups["tail"].Value);
        if (string.IsNullOrWhiteSpace(itemText))
            return line;

        return $"{match.Groups["prefix"].Value}{itemText} {match.Groups["citation"].Value}";
    }

    private static string CleanStructuredPlanningCitationLeadingTail(string value)
    {
        var cleaned = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '-', '\u2013', '\u2014', ':', ';', '.', ',');
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        if (cleaned.StartsWith('(') && cleaned.EndsWith(')') && cleaned.Length > 2)
            cleaned = CollapseWhitespace(cleaned[1..^1]);
        if (cleaned.StartsWith('[') && cleaned.EndsWith(']') && cleaned.Length > 2)
            cleaned = CollapseWhitespace(cleaned[1..^1]);

        if (string.IsNullOrWhiteSpace(cleaned)
            || Regex.IsMatch(cleaned, @"^(?:sources?|source|file|fichier|document|doc)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || StructuredPlanningLineContainsCitationHandle(cleaned)
            || LooksLikeCitationOnlyStructuredPlanningCellContent(cleaned))
        {
            return string.Empty;
        }

        return cleaned;
    }

    private static string StripInlineSourceCitationsForEvidenceIdMapping(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return string.Empty;

        var stripped = Regex.Replace(
            answer.Trim(),
            @"\s*\(\s*sources?\s*:\s*[^)\r\n]{1,180}\bp\.?\s*\d+(?:\s*[-â€“]\s*\d+)?[^)\r\n]*\)",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        stripped = Regex.Replace(
            stripped,
            @"\s*\[\s*sources?\s*:\s*[^\]\r\n]{1,180}\bp\.?\s*\d+(?:\s*[-â€“]\s*\d+)?[^\]\r\n]*\]",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(stripped, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string RemoveUnresolvedWriterEvidenceIds(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return string.Empty;

        var withoutIds = Regex.Replace(
            answer,
            @"(?<![A-Za-z0-9])(?:\[(?:E\d{1,3})\]|\((?:E\d{1,3})\)|source\s*:\s*E\d{1,3}|citation\s*:\s*E\d{1,3})(?![A-Za-z0-9])",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(withoutIds, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static IReadOnlyDictionary<string, string> BuildWriterEvidenceCitationMap(
        ToolResults toolResults,
        string query,
        string language)
    {
        var inventory = BuildWriterEvidenceRosterForCitationMapping(toolResults, query, language);
        if (string.IsNullOrWhiteSpace(inventory))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in inventory.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var id = Regex.Match(line, @"\bid=""(?<value>E\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Groups["value"].Value;
            var citation = Regex.Match(line, @"\bcitation=""(?<value>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(id)
                && !string.IsNullOrWhiteSpace(citation)
                && !map.ContainsKey(id))
            {
                map[id] = citation;
            }
        }

        return map;
    }

    private static List<ToolMemory.SourceRef> BuildWriterEvidenceRosterSourcePoolForCitationMapping(
        ToolResults toolResults,
        string query,
        string language,
        bool allowVisibleSourceFallback = true,
        bool allowDiagnosticRows = true)
    {
        var inventory = BuildWriterEvidenceRosterForCitationMapping(
            toolResults,
            query,
            language,
            allowVisibleSourceFallback,
            allowDiagnosticRows);
        if (!ContainsWriterEvidenceItems(inventory))
            return new List<ToolMemory.SourceRef>();

        var sourceLimit = Math.Clamp(
            Math.Max(ResolveSourceBackedPlanningTargetItemCount(query) * 4, 48),
            48,
            192);
        var rawSources = new List<ToolMemory.SourceRef>();
        rawSources.AddRange(DeriveSourcesFromRagHits(toolResults));
        if (!string.IsNullOrWhiteSpace(query))
            rawSources.AddRange(DeriveSourcesFromRankedRagHits(toolResults, query, sourceLimit));
        rawSources.AddRange(EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !string.IsNullOrWhiteSpace(hit.DocPath))
            .Select(BuildSourceRefFromRagHit));

        var normalizedRawSources = NormalizeVisibleSourceRefsForMemory(rawSources);
        var mappedSources = new List<ToolMemory.SourceRef>();
        foreach (var rawLine in inventory.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceName = TryGetEvidenceRosterAttribute(line, "source");
            var citation = TryGetEvidenceRosterAttribute(line, "citation");
            var title = TryGetEvidenceRosterAttribute(line, "title");
            var evidence = TryGetEvidenceRosterAttribute(line, "evidence");
            if (!allowDiagnosticRows
                && (string.IsNullOrWhiteSpace(evidence)
                    || EvidenceRosterEvidenceOnlyRepeatsTitle(evidence, title)))
            {
                continue;
            }

            var page = TryParseEvidenceRosterPage(TryGetEvidenceRosterAttribute(line, "page"));
            if (page <= 0)
                page = TryExtractPageNumberFromVisibleCitation(citation);
            if (page <= 0)
                continue;

            var citationProbe = !string.IsNullOrWhiteSpace(citation)
                ? citation
                : BuildInlineSourceCitationForWriter(sourceName, page, language);
            var matchedSource = normalizedRawSources.FirstOrDefault(source =>
                    IsSourceRefCitedInAnswer(citationProbe, source))
                ?? normalizedRawSources.FirstOrDefault(source =>
                    EvidenceRosterSourceMatchesSourceRef(sourceName, page, source));

            if (matchedSource is not null)
            {
                mappedSources.Add(matchedSource);
                continue;
            }

            if (string.IsNullOrWhiteSpace(sourceName))
                continue;

            var visibleName = Path.GetFileName(sourceName);
            if (string.IsNullOrWhiteSpace(visibleName))
                visibleName = sourceName;
            mappedSources.Add(new ToolMemory.SourceRef
            {
                DocPath = sourceName,
                DocName = visibleName,
                Label = visibleName,
                PageStart = page,
                PageEnd = page
            });
        }

        return NormalizeVisibleSourceRefsForMemory(mappedSources)
            .Take(sourceLimit)
            .ToList();
    }

    private static bool EvidenceRosterEvidenceOnlyRepeatsTitle(string? evidence, string? title)
    {
        var normalizedEvidence = NormalizeLexicalLookup(evidence);
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedEvidence)
            || string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        if (string.Equals(normalizedEvidence, normalizedTitle, StringComparison.Ordinal))
            return true;

        var withoutCardPrefix = Regex.Replace(
            normalizedEvidence,
            @"^(?:card\s+title|titre\s+carte)\s*:\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        return string.Equals(withoutCardPrefix, normalizedTitle, StringComparison.Ordinal);
    }

    private static string TryGetEvidenceRosterAttribute(string line, string attributeName)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(attributeName))
            return string.Empty;

        var match = Regex.Match(
            line,
            $@"\b{Regex.Escape(attributeName)}=""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? CollapseWhitespace(match.Groups["value"].Value)
            : string.Empty;
    }

    private static int TryParseEvidenceRosterPage(string? page)
        => int.TryParse(CollapseWhitespace(page ?? string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(1, value)
            : 0;

    private static int TryExtractPageNumberFromVisibleCitation(string? citation)
    {
        if (string.IsNullOrWhiteSpace(citation))
            return 0;

        var match = Regex.Match(
            citation,
            @"\bp(?:age)?\.?\s*(?<page>\d{1,5})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? TryParseEvidenceRosterPage(match.Groups["page"].Value)
            : 0;
    }

    private static bool EvidenceRosterSourceMatchesSourceRef(
        string? rosterSource,
        int page,
        ToolMemory.SourceRef source)
    {
        if (string.IsNullOrWhiteSpace(rosterSource) || page <= 0)
            return false;

        var pageStart = Math.Max(1, source.PageStart);
        var pageEnd = Math.Max(pageStart, source.PageEnd);
        if (page < pageStart || page > pageEnd)
            return false;

        var rosterLookup = NormalizeVisibleSourceCitationLookup(rosterSource);
        if (string.IsNullOrWhiteSpace(rosterLookup))
            return false;

        var rosterPath = NormalizeVisibleSourcePathIdentity(rosterSource);
        var sourcePath = NormalizeVisibleSourcePathIdentity(source.DocPath);
        if (!string.IsNullOrWhiteSpace(rosterPath)
            && !string.IsNullOrWhiteSpace(sourcePath)
            && (string.Equals(rosterPath, sourcePath, StringComparison.Ordinal)
                || sourcePath.EndsWith("/" + rosterPath, StringComparison.Ordinal)
                || rosterPath.EndsWith("/" + sourcePath, StringComparison.Ordinal)))
        {
            return true;
        }

        return BuildSourceCitationIdentities(source).Any(identity =>
            string.Equals(identity, rosterLookup, StringComparison.Ordinal)
            || identity.EndsWith("/" + rosterLookup, StringComparison.Ordinal)
            || rosterLookup.EndsWith("/" + identity, StringComparison.Ordinal));
    }

    private static string BuildInlineSourceCitationForWriter(string? source, int pageStart, string language)
    {
        var visibleSource = CollapseWhitespace(source ?? string.Empty);
        return string.IsNullOrWhiteSpace(visibleSource)
            ? string.Empty
            : $"(source: {visibleSource} {SourceBackedPagePrefix(language)}{Math.Max(1, pageStart)})";
    }

    private static (string Route, string Fit) BuildSourceBackedCandidateSlotMetadataForInventory(
        SourceBackedOptionCandidate candidate,
        string? query,
        string language)
    {
        language = NormalizeLanguageCode(language);
        var labels = DetectRequestedPlanningSlotAxisLabels(query, language);
        var slotGroups = BuildStructuredPlanningSlotTermGroups(labels, query);
        if (slotGroups.Count == 0)
            return (string.Empty, "none");

        var primaryMatches = FindStructuredPlanningCandidateSlotMatches(candidate, slotGroups, useAlternativeTerms: false);
        if (primaryMatches.Length > 0)
            return (FormatStructuredPlanningSlotRouteLabels(slotGroups, primaryMatches), "primary");

        var alternativeMatches = FindStructuredPlanningCandidateSlotMatches(candidate, slotGroups, useAlternativeTerms: true);
        if (alternativeMatches.Length > 0)
            return (FormatStructuredPlanningSlotRouteLabels(slotGroups, alternativeMatches), "alternative");

        return (string.Empty, string.IsNullOrWhiteSpace(candidate.Hit.RetrievalQuery) ? "unrouted" : "neutral");
    }

    private static string FormatStructuredPlanningSlotRouteLabels(
        IReadOnlyList<StructuredPlanningSlotTermGroup> slotGroups,
        IReadOnlyList<int> matches)
    {
        if (slotGroups.Count == 0 || matches.Count == 0)
            return string.Empty;

        return string.Join(
            ",",
            matches
                .Where(index => (uint)index < (uint)slotGroups.Count)
                .Select(index => slotGroups[index].Label)
                .Where(static label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.Ordinal));
    }

    private static string BuildSourceBackedCandidateWritingNote(string contentRole, RagHitSummary hit, string language)
    {
        var role = NormalizeLexicalLookup(contentRole);
        if ((role.Contains("navigation", StringComparison.Ordinal) || BackendSelectionHintsPreferNavigation(hit))
            && !LooksLikeResolvedRouteTargetHit(hit))
            return "background only; do not present as a proposed item unless the same hit has concrete content";
        if (IsRouteDiscoveryAnchorHit(hit) && !LooksLikeResolvedRouteTargetHit(hit))
            return "discovery anchor; use it to search/read the target page before presenting a final item";
        if (role.Contains("fragment", StringComparison.Ordinal) || BackendSelectionHintsPreferLowSignal(hit))
            return "weak evidence; cite carefully and keep uncertainty visible";
        if (role.Contains("supporting", StringComparison.Ordinal) || role.Contains("advisory", StringComparison.Ordinal))
            return "context or uncertainty note, not the main recommendation";
        if (role.Contains("actionable", StringComparison.Ordinal))
            return "can be proposed if the title/detail is clear; rewrite naturally";

        return "evidence inventory; rewrite naturally and keep the source reference short";
    }

    private static string BuildSourceBackedCandidateSupportCue(RagHitSummary hit)
    {
        var routeCue = ExtractRouteDiscoveryTitleCue(hit);
        if (!string.IsNullOrWhiteSpace(routeCue))
            return $"route title: {routeCue}";

        var cardTitle = hit.MatchedContentCards?
            .Select(static card => CleanSourceBackedOptionTitle(card.Title))
            .FirstOrDefault(static title => IsUsableSourceBackedOptionTitle(title)
                && !LooksLikeWeakSourceBackedOptionTitle(title));
        if (!string.IsNullOrWhiteSpace(cardTitle))
            return $"readable title: {cardTitle}";

        var section = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(section))
            return $"related section: {section}";

        if (BackendSelectionHintsPreferUsableEvidence(hit))
            return "actionable source page";

        var evidence = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 100);
        if (string.IsNullOrWhiteSpace(evidence))
            evidence = FormatReadableEvidenceExcerpt(hit.ContextualSnippet ?? string.Empty, maxLength: 100);
        if (LooksLikeNoisyCandidateSupportCue(evidence))
            return string.Empty;

        return evidence;
    }

    private static string BuildWriterEvidenceCueForPrompt(RagHitSummary hit, string? query, int maxLength)
    {
        var cardEvidence = BuildWriterContentCardEvidenceCue(hit);
        if (!string.IsNullOrWhiteSpace(cardEvidence))
            return TruncateForPrompt(cardEvidence, maxLength);

        var title = ExtractReadablePartialPlanningLeadTitle(hit, query ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(title)
            && !LooksLikeGenericWriterEvidenceCueTitle(title)
            && !LooksLikeNoisyCandidateSupportCue(title))
            return TruncateForPrompt(title, maxLength);

        var section = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(section)
            && !LooksLikeGenericWriterEvidenceCueTitle(section)
            && !LooksLikeNoisyCandidateSupportCue(section))
            return TruncateForPrompt(section, maxLength);

        var evidence = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength));
        if (LooksLikeNoisyCandidateSupportCue(evidence))
            evidence = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(hit.ContextualSnippet ?? string.Empty, maxLength));

        return LooksLikeNoisyCandidateSupportCue(evidence)
            ? string.Empty
            : evidence;
    }

    private static string BuildWriterContentCardEvidenceCue(RagHitSummary hit)
    {
        if (hit.MatchedContentCards is null || hit.MatchedContentCards.Count == 0)
            return string.Empty;

        var fragments = new List<string>();
        foreach (var card in hit.MatchedContentCards.Take(2))
        {
            var title = CollapseWhitespace(card.Title);
            var cardFragments = new List<string>();

            var facts = card.Evidence?.Facts?
                .Select(fact => CollapseWhitespace(
                    !string.IsNullOrWhiteSpace(fact.SourceText)
                        ? fact.SourceText
                        : string.Join(' ', new[] { fact.Label, fact.Value, fact.Unit }.Where(static part => !string.IsNullOrWhiteSpace(part)))))
                .Where(static fact => !string.IsNullOrWhiteSpace(fact))
                .Where(static fact => !LooksLikeNoisyCandidateSupportCue(fact))
                .Take(2)
                .ToArray();
            if (facts is { Length: > 0 })
                cardFragments.AddRange(facts);

            var quantityFacts = card.Evidence?.QuantityFacts?
                .Select(fact => CollapseWhitespace(
                    !string.IsNullOrWhiteSpace(fact.SourceText)
                        ? fact.SourceText
                        : $"{fact.Label} {fact.Value.ToString(CultureInfo.InvariantCulture)} {fact.Unit}"))
                .Where(static fact => !string.IsNullOrWhiteSpace(fact))
                .Where(static fact => !LooksLikeNoisyCandidateSupportCue(fact))
                .Take(2)
                .ToArray();
            if (quantityFacts is { Length: > 0 })
                cardFragments.AddRange(quantityFacts);

            if (!string.IsNullOrWhiteSpace(title)
                && !LooksLikeGenericWriterEvidenceCueTitle(title)
                && !LooksLikeNoisyCandidateSupportCue(title))
            {
                if (cardFragments.Count > 0)
                    fragments.Add(title);
            }

            if (cardFragments.Count == 0)
                continue;

            fragments.AddRange(cardFragments);
        }

        return string.Join(" | ", fragments.Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
    }

    private static bool LooksLikeGenericWriterEvidenceCueTitle(string? title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return normalized is "document"
            or "property value"
            or "properties"
            or "content"
            or "content card"
            or "source"
            or "sourced lead"
            or "piste sourcee"
            or "option sourcee";
    }

    private static string BuildWriterUseCueForPrompt(RagHitSummary hit, string? query)
    {
        var role = NormalizeLexicalLookup(hit.SelectionHintRole ?? hit.ContentRole ?? string.Empty);
        if (role.Contains("navigation", StringComparison.Ordinal) || BackendSelectionHintsPreferNavigation(hit))
            return "Use only as navigation/context; do not promote as a proposed item unless the text itself contains a concrete item.";
        if (role.Contains("fragment", StringComparison.Ordinal) || BackendSelectionHintsPreferLowSignal(hit))
            return "Use only as weak context; mention uncertainty if it is cited.";
        if (role.Contains("supporting", StringComparison.Ordinal) || role.Contains("advisory", StringComparison.Ordinal))
            return "Use as supporting context or uncertainty note, not as the main recommendation.";
        if (role.Contains("actionable", StringComparison.Ordinal) || LooksLikeAnyDocumentaryPlanningRequest(query))
            return "May be used as a concrete candidate if the title/evidence is clear; rewrite it naturally in the target language.";
        return "Use as evidence inventory; rewrite naturally and keep the source/page reference short.";
    }

    private static bool LooksLikeNoisyCandidateSupportCue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokenCount = Regex.Matches(normalized, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Count;
        var digitCount = text.Count(char.IsDigit);
        var separatorCount = text.Count(static ch => ch is ';' or '|' or '/' or '\\' or '=');
        var upperCaseLetters = text.Count(char.IsUpper);
        var letters = text.Count(char.IsLetter);

        return tokenCount > 28
            || separatorCount >= 6
            || (digitCount >= 10 && tokenCount >= 10)
            || (letters >= 20 && upperCaseLetters > letters * 0.70);
    }
}
