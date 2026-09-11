using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static List<ToolMemory.SourceRef> ReconcileVisibleSourcesWithFinalAnswer(
        string answer,
        IEnumerable<ToolMemory.SourceRef>? sources)
    {
        var visibleSources = NormalizeVisibleSourceRefsForMemory(sources);
        if (visibleSources.Count == 0 || string.IsNullOrWhiteSpace(answer))
            return visibleSources;

        var citedSources = visibleSources
            .Where(source => IsSourceRefCitedInAnswer(answer, source))
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        return citedSources.Count > 0
            ? citedSources
            : visibleSources
                .GroupBy(BuildVisibleSourceRefDedupeKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();
    }

    private static bool ShouldRequireVisibleSourcesToBeCited(string answer, string? query)
    {
        if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(query))
            return false;

        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return true;

        if (!LooksLikeAnyDocumentaryPlanningRequest(query)
            && !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && !LooksLikeSourceBackedOptionRequest(query)
            && !LooksLikeBroadSourceBackedCompositionRequest(query)
            && !LooksLikeMultipleCandidateSynthesisRequest(query)
            && !LooksLikeGenericCollectionOrListRequest(query))
        {
            return false;
        }

        return CountConcreteAnswerLines(answer) >= 3;
    }

    private static List<ToolMemory.SourceRef> ReconcileRequiredVisibleSourcesWithFinalAnswer(
        string answer,
        IEnumerable<ToolMemory.SourceRef>? sources,
        string? query)
    {
        var visibleSources = NormalizeVisibleSourceRefsForMemory(sources);
        if (visibleSources.Count == 0)
            return visibleSources;

        var citedSources = visibleSources
            .Where(source => IsSourceRefCitedInAnswer(answer, source))
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        if (citedSources.Count > 0)
            return citedSources;

        return ShouldRequireVisibleSourcesToBeCited(answer, query)
            ? new List<ToolMemory.SourceRef>()
            : visibleSources
                .GroupBy(BuildVisibleSourceRefDedupeKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();
    }

    internal static List<ToolMemory.SourceRef> ReconcileRequiredVisibleSourcesWithFinalAnswerForTests(
        string answer,
        IEnumerable<ToolMemory.SourceRef>? sources,
        string? query)
        => ReconcileRequiredVisibleSourcesWithFinalAnswer(answer, sources, query);

    private static string BuildVisibleSourceRefDedupeKey(ToolMemory.SourceRef source)
        => BuildSourceRefVisiblePageMergeKey(source);

    private static int CountConcreteAnswerLines(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return 0;

        var withoutSourceBlock = RemoveTrailingModelEmittedSourceList(answer);
        return withoutSourceBlock
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => CollapseWhitespace(line))
            .Count(static line =>
                line.Length >= 12
                && !Regex.IsMatch(line, @"^(?:source|sources|references?|r[eÃ©]f[eÃ©]rences?|fuentes?|fontes?|quellen?|fonti)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && Regex.IsMatch(line, @"^(?:[-*\u2022]|\d+[.)]|[A-Za-zÃ€-Ã–Ã˜-Ã¶Ã¸-Ã¿].{0,35}:)", RegexOptions.CultureInvariant));
    }

    private static bool LooksLikeConcreteStructuredPlanningAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var withoutSourceBlock = RemoveTrailingModelEmittedSourceList(answer);
        var normalized = NormalizeLexicalLookup(withoutSourceBlock);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var dayMarkerCount = Regex.Matches(
            normalized,
            @"\b(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|lunes|martes|miercoles|miÃ©rcoles|jueves|viernes|sabado|sÃ¡bado|domingo|segunda|ter[cÃ§]a|quarta|quinta|sexta|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|lunedi|lunedÃ¬|martedi|martedÃ¬|mercoledi|mercoledÃ¬|giovedi|giovedÃ¬|venerdi|venerdÃ¬|sabato|domenica)\b",
            RegexOptions.CultureInvariant).Count;
        var bulletCount = Regex.Matches(
            withoutSourceBlock,
            @"(?m)^\s*(?:[-*\u2022\u25E6]|\d+[.)])\s+\S",
            RegexOptions.CultureInvariant).Count;

        return dayMarkerCount >= 2
            || bulletCount >= 3
            || normalized.Contains('|', StringComparison.Ordinal);
    }

    private static bool LooksLikeCitationOnlyStructuredPlanningAnswer(string? answer, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var candidateAnswer = RemoveTrailingModelEmittedSourceList(answer).Trim();
        if (string.IsNullOrWhiteSpace(candidateAnswer)
            || !LooksLikeConcreteStructuredPlanningAnswer(candidateAnswer))
        {
            return false;
        }

        var candidateLines = 0;
        var citationOnlyLines = 0;
        foreach (var rawLine in SplitStructuredPlanningCellCandidateLines(candidateAnswer))
        {
            var line = CollapseWhitespace(rawLine);
            if (!LooksLikeStructuredPlanningCellLine(line)
                || !StructuredPlanningLineContainsCitationHandle(line))
            {
                continue;
            }

            candidateLines++;
            var content = StripStructuredPlanningCellLabelsAndCitations(line);
            if (LooksLikeCitationOnlyStructuredPlanningCellContent(content))
                citationOnlyLines++;
        }

        return citationOnlyLines > 0;
    }

    private static bool ContainsUnresolvedStructuredPlanningEvidenceIds(string? answer)
        => Regex.IsMatch(
            RemoveTrailingModelEmittedSourceList(answer ?? string.Empty),
            @"(?<![A-Za-z0-9])\[E\d{1,3}\](?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool LooksLikeUnsupportedStructuredPlanningRandomization(string? answer, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var normalizedAnswer = NormalizeLexicalLookup(RemoveTrailingModelEmittedSourceList(answer));
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
            return false;

        if (Regex.IsMatch(
                normalizedAnswer,
                @"\b(?:reparti(?:e|es|s)?\s+de\s+maniere\s+aleatoire|distribu(?:e|ee|ees|es|es)?\s+au\s+hasard|illustrer\s+le\s+format|illustratif|illustrative|fictif|fictive|example\s+only|for\s+illustration\s+only|random(?:ly|ized)?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        var normalizedQuery = NormalizeLexicalLookup(query);
        if (!Regex.IsMatch(
                normalizedQuery,
                @"\b(?:n\s*invente\s*rien|ne\s*pas\s*inventer|sans\s*inventer|pas\s*d\s*invention|do\s*not\s*invent|don'?t\s*invent)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
            normalizedAnswer,
            @"\b(?:aleatoire|hasard|random|fictif|fictive|exemple)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeOverCitedStructuredPlanningAnswer(string? answer, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var candidateAnswer = RemoveTrailingModelEmittedSourceList(answer).Trim();
        if (string.IsNullOrWhiteSpace(candidateAnswer))
            return false;

        var candidateLines = 0;
        var overCitedLines = 0;
        foreach (var rawLine in SplitStructuredPlanningCellCandidateLines(candidateAnswer))
        {
            var line = CollapseWhitespace(rawLine);
            if (!LooksLikeStructuredPlanningCellLine(line))
                continue;

            var citationCount = CountStructuredPlanningCellCitationHandles(line);
            if (citationCount <= 0)
                continue;

            candidateLines++;
            if (citationCount > 1)
                overCitedLines++;
        }

        return candidateLines >= 4
            && overCitedLines >= 3
            && overCitedLines * 2 >= candidateLines;
    }

    private static int CountStructuredPlanningCellCitationHandles(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return 0;

        var sourceMentions = Regex.Matches(
            line,
            @"(?:\(\s*sources?\s*:\s*[^)\r\n]{1,240}\)|\[\s*sources?\s*:\s*[^\]\r\n]{1,240}\])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        var openMentions = Regex.Matches(
            line,
            @"\[\[open\|[^\]\r\n]{1,240}\]\]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        var evidenceMentions = Regex.Matches(
            line,
            @"(?<![A-Za-z0-9])\[E\d{1,3}\](?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;

        return sourceMentions + openMentions + evidenceMentions;
    }

    private static IEnumerable<string> SplitStructuredPlanningCellCandidateLines(string text)
    {
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var collapsed = CollapseWhitespace(line);
            if (string.IsNullOrWhiteSpace(collapsed))
                continue;

            var compactCells = SplitCompactStructuredPlanningCells(collapsed);
            if (compactCells.Count > 1)
            {
                foreach (var cell in compactCells)
                    yield return cell;
                continue;
            }

            yield return collapsed;
        }
    }

    private static IReadOnlyList<string> SplitCompactStructuredPlanningCells(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Array.Empty<string>();

        var withoutBullet = Regex.Replace(
            line,
            @"^\s*(?:[-*\u2022\u25E6]|\d+[.)])\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        var parts = Regex
            .Split(
                withoutBullet,
                @"\s+(?:[-\u2013\u2014]\s+)?(?=\b(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|lunes|martes|miercoles|mi[eÃ©]rcoles|jueves|viernes|sabado|s[aÃ¡]bado|domingo|segunda|ter[cÃ§]a|quarta|quinta|sexta|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|lunedi|luned[iÃ¬]|martedi|marted[iÃ¬]|mercoledi|mercoled[iÃ¬]|giovedi|gioved[iÃ¬]|venerdi|venerd[iÃ¬]|sabato|domenica)\b\s*(?:[-\u2013\u2014:]|$))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(CollapseWhitespace)
            .Select(static part => part.Trim(' ', '-', '\u2013', '\u2014', ';'))
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length > 1 ? parts : new[] { line };
    }

    private static bool LooksLikeStructuredPlanningCellLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var normalized = NormalizeLexicalLookup(line);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasBulletPrefix = Regex.IsMatch(
            line,
            @"^\s*(?:[-*\u2022\u25E6]|\d+[.)])\s+\S",
            RegexOptions.CultureInvariant);
        var hasPlanningAxis = Regex.IsMatch(
            normalized,
            @"\b(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|jour|jours|day|days?|slot|case|section|rubrique|phase|step|etape|[eÃ©]tape)\b",
            RegexOptions.CultureInvariant);

        return hasPlanningAxis
            || (hasBulletPrefix && line.Contains(':', StringComparison.Ordinal));
    }

    private static bool StructuredPlanningLineContainsCitationHandle(string line)
        => Regex.IsMatch(
            line ?? string.Empty,
            @"(?:\(\s*sources?\s*:|\[\s*sources?\s*:|(?<![A-Za-z0-9])\[E\d{1,3}\](?![A-Za-z0-9])|\[\[open\|)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StripStructuredPlanningCellLabelsAndCitations(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var cleaned = Regex.Replace(
            line,
            @"\[\[open\|[^\]\r\n]{0,240}\]\]",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = StripInlineSourceCitationsForEvidenceIdMapping(cleaned);
        cleaned = Regex.Replace(
            cleaned,
            @"(?<![A-Za-z0-9])(?:\[(?:E\d{1,3})\]|\((?:E\d{1,3})\)|source\s*:\s*E\d{1,3}|citation\s*:\s*E\d{1,3})(?![A-Za-z0-9])",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"\s*\(\s*(?:sources?|document|file|fichier)\s*:[^)\r\n]{0,240}\)?\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"\s*\[\s*(?:sources?|document|file|fichier)\s*:[^\]\r\n]{0,240}\]?\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"^\s*(?:[-*\u2022\u25E6]|\d+[.)])\s*",
            string.Empty,
            RegexOptions.CultureInvariant);

        var colonIndex = cleaned.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < cleaned.Length - 1)
            cleaned = cleaned[(colonIndex + 1)..];
        else if (colonIndex == cleaned.Length - 1)
            cleaned = string.Empty;

        cleaned = Regex.Replace(
            cleaned,
            @"\b(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|jour|jours|day|days?|slot|case|section|rubrique|phase|step|etape|[eÃ©]tape)\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"\b[\w.-]+\.pdf\b|\bp\.?\s*\d+(?:\s*[-/]\s*\d+)?\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"[-:;,.()\[\]{}|]+", " ", RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned);
    }

    private static bool LooksLikeCitationOnlyStructuredPlanningCellContent(string content)
    {
        var normalized = NormalizeLexicalLookup(content);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (normalized.Contains("a completer avec une source utile", StringComparison.Ordinal))
            return false;

        var meaningfulTokens = Regex.Matches(normalized, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static token => !IsCitationOnlyPlanningFillerToken(token))
            .ToArray();
        return meaningfulTokens.Length == 0;
    }

    private static bool IsCitationOnlyPlanningFillerToken(string token)
        => token is "source" or "sources" or "citation" or "citations"
            or "document" or "documents" or "doc" or "docs"
            or "file" or "fichier" or "pdf" or "page" or "pages"
            or "open" or "evidence" or "item" or "id" or "ref"
            or "reference" or "references" or "web" or "sist";

    private static bool IsSourceRefCitedInAnswer(string answer, ToolMemory.SourceRef source)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var normalizedAnswer = NormalizeVisibleSourceCitationLookup(answer);
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
            return false;

        var pageStart = Math.Max(1, source.PageStart);
        var pageEnd = Math.Max(pageStart, source.PageEnd);
        var pagePatterns = BuildSourcePageCitationPatterns(pageStart, pageEnd);
        var identities = BuildSourceCitationIdentities(source);

        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity) || !normalizedAnswer.Contains(identity, StringComparison.Ordinal))
                continue;

            foreach (var pagePattern in pagePatterns)
            {
                if (Regex.IsMatch(normalizedAnswer, $@"{Regex.Escape(identity)}.{{0,80}}{pagePattern}", RegexOptions.CultureInvariant)
                    || Regex.IsMatch(normalizedAnswer, $@"{pagePattern}.{{0,80}}{Regex.Escape(identity)}", RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildSourceCitationIdentities(ToolMemory.SourceRef source)
    {
        var values = new[]
            {
                source.DocName,
                source.Label,
                Path.GetFileName(source.DocPath ?? string.Empty),
                source.DocPath
            }
            .Select(NormalizeVisibleSourceCitationLookup)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var stems = values
            .Select(static value =>
            {
                var extensionMatch = Regex.Match(value, @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv|json|ya?ml|html?)\b", RegexOptions.CultureInvariant);
                return extensionMatch.Success
                    ? value[..extensionMatch.Index].Trim()
                    : string.Empty;
            })
            .Where(static value => value.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        values.AddRange(stems);
        return values.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> BuildSourcePageCitationPatterns(int pageStart, int pageEnd)
    {
        var pages = Enumerable.Range(pageStart, Math.Max(1, pageEnd - pageStart + 1))
            .Take(12)
            .Distinct()
            .ToArray();

        return pages
            .SelectMany(static page => new[]
            {
                $@"\bp\.?\s*{page}\b",
                $@"\bpage\s*{page}\b",
                $@"\bp\s*{page}\b"
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeVisibleSourceCitationLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeLexicalLookup(value)
            .Replace('\\', '/');

        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\s*([/()._-])\s*", "$1", RegexOptions.CultureInvariant);
        return normalized.Trim();
    }

    private static void CanonicalizeFilenameOnlySourceRefAliases(List<ToolMemory.SourceRef> sources)
    {
        foreach (var source in sources)
        {
            var normalizedPath = NormalizeVisibleSourcePathIdentity(source.DocPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath.Contains('/'))
                continue;

            var fileName = FirstNonEmptySourceIdentity(Path.GetFileName(source.DocPath ?? string.Empty), source.DocName, source.Label);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var pageStart = Math.Max(1, source.PageStart);
            var pageEnd = Math.Max(pageStart, source.PageEnd);
            var hash = CollapseWhitespace(source.SourceHash ?? string.Empty).ToLowerInvariant();
            var category = FirstNonEmptySourceIdentity(source.CategoryPath, source.Category, source.CategoryRef);

            var matchingPaths = sources
                .Where(candidate => !ReferenceEquals(candidate, source))
                .Where(candidate =>
                {
                    var candidatePath = NormalizeVisibleSourcePathIdentity(candidate.DocPath);
                    if (string.IsNullOrWhiteSpace(candidatePath) || !candidatePath.Contains('/'))
                        return false;

                    var candidateFile = FirstNonEmptySourceIdentity(Path.GetFileName(candidate.DocPath ?? string.Empty), candidate.DocName, candidate.Label);
                    if (!string.Equals(candidateFile, fileName, StringComparison.OrdinalIgnoreCase))
                        return false;

                    var candidatePageStart = Math.Max(1, candidate.PageStart);
                    var candidatePageEnd = Math.Max(candidatePageStart, candidate.PageEnd);
                    if (candidatePageStart != pageStart || candidatePageEnd != pageEnd)
                        return false;

                    var candidateHash = CollapseWhitespace(candidate.SourceHash ?? string.Empty).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(hash)
                        && !string.IsNullOrWhiteSpace(candidateHash)
                        && !string.Equals(hash, candidateHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var candidateCategory = FirstNonEmptySourceIdentity(candidate.CategoryPath, candidate.Category, candidate.CategoryRef);
                    return string.IsNullOrWhiteSpace(category)
                        || string.IsNullOrWhiteSpace(candidateCategory)
                        || string.Equals(category, candidateCategory, StringComparison.OrdinalIgnoreCase);
                })
                .Select(static candidate => candidate.DocPath)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            if (matchingPaths.Count == 1)
                source.DocPath = matchingPaths[0];
        }
    }

    private static string FirstNonEmptySourceIdentity(params string?[] values)
        => values
            .Select(NormalizeLexicalLookup)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string BuildSourceRefVisiblePageMergeKey(ToolMemory.SourceRef source)
    {
        var evidenceSuffix = string.IsNullOrWhiteSpace(source.EvidenceId)
            ? string.Empty
            : "|evidence:" + CollapseWhitespace(source.EvidenceId).ToLowerInvariant();
        string WithEvidenceIdentity(string key) => key + evidenceSuffix;

        var pageStart = Math.Max(1, source.PageStart);
        var pageEnd = Math.Max(pageStart, source.PageEnd);
        var pagePart = $"p:{pageStart}-{pageEnd}";

        var path = NormalizeVisibleSourcePathIdentity(source.DocPath);
        if (!string.IsNullOrWhiteSpace(path) && LooksLikeQualifiedDocumentPath(source.DocPath))
            return WithEvidenceIdentity($"path:{path}|{pagePart}");

        var hash = CollapseWhitespace(source.SourceHash ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(hash))
        {
            var fileName = NormalizeLexicalLookup(Path.GetFileName(source.DocPath ?? string.Empty));
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = NormalizeLexicalLookup(source.DocName);
            var filePart = string.IsNullOrWhiteSpace(fileName) ? "unknown" : fileName;
            return WithEvidenceIdentity($"hash:{hash}|file:{filePart}|{pagePart}");
        }

        if (!string.IsNullOrWhiteSpace(path))
            return WithEvidenceIdentity($"path:{path}|{pagePart}");

        var docId = CollapseWhitespace(source.DocId ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(docId))
            return WithEvidenceIdentity($"id:{docId}|{pagePart}");

        var visibleName = NormalizeLexicalLookup(source.DocName);
        if (string.IsNullOrWhiteSpace(visibleName))
            visibleName = NormalizeLexicalLookup(source.Label);
        if (!string.IsNullOrWhiteSpace(visibleName))
            return WithEvidenceIdentity($"name:{visibleName}|{pagePart}");

        return WithEvidenceIdentity($"unknown:{pagePart}");
    }

    private static string NormalizeVisibleSourcePathIdentity(string? path)
    {
        var normalized = NormalizeLexicalLookup(path).Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var marker = "/documents/";
        var markerIndex = normalized.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
            return normalized[(markerIndex + marker.Length)..].TrimStart('/');

        marker = "saaia-repo/documents/";
        markerIndex = normalized.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
            return normalized[(markerIndex + marker.Length)..].TrimStart('/');

        return normalized;
    }

    private static bool LooksLikeQualifiedDocumentPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.Contains('/', StringComparison.Ordinal)
               || path.Contains('\\', StringComparison.Ordinal)
               || path.Contains(':', StringComparison.Ordinal));
}
