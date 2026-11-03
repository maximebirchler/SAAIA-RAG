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
    private static bool IsGenericDocumentVersionRetrievalTerm(string term)
        => Regex.IsMatch(
            NormalizeLexicalLookup(term),
            @"^(?:and|the|for|backed|compare|comparing|comparaison|comparison|version|versions|edition|editions|document|documents|file|fichier|pdf|old|older|previous|prior|archived|archive|legacy|obsolete|ancienne|ancien|precedente|precedent|anterieure|anterieur|current|latest|newest|recent|actuelle|actuel|courante|courant|derniere|nouvelle|nouveau)$",
            RegexOptions.CultureInvariant);

    private static bool ContainsHistoricalDocumentVersionSurface(string? normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:ancien(?:ne)?s?|precedent(?:e)?s?|anterieur(?:e)?s?|old|older|previous|prior|archived|archive|archives|historical|legacy|obsolete)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ContainsCurrentDocumentVersionSurface(string? normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:actuel(?:le)?s?|courant(?:e)?s?|derniere?s?|nouveau|nouvelle?s?|current|latest|newest|recent|newer)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool HistoricalDocumentVersionSurfaceIsNegated(string? normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:pas|not|never|eviter|evite|avoid|sans|without)\b.{0,80}\b(?:ancienne?s?\s+versions?|old\s+versions?|older\s+versions?|previous\s+versions?|archived\s+versions?)\b|\b(?:ancienne?s?\s+versions?|old\s+versions?|older\s+versions?|previous\s+versions?|archived\s+versions?)\b.{0,80}\b(?:pas|not|never|eviter|evite|avoid|sans|without)\b",
            RegexOptions.CultureInvariant);
    }

    // Title overlap is a retrieval signal, never a filter or proof of document type.
    private static int ComputeDocumentTitleQueryOverlap(string query, RagHitSummary hit)
    {
        var title = !string.IsNullOrWhiteSpace(hit.DocName)
            ? hit.DocName
            : Path.GetFileName(hit.DocPath);
        static IEnumerable<string> TitleTerms(string? text)
            => Regex.Matches(NormalizeLexicalLookup(text), @"[\p{L}\p{N}]{3,}")
                .Select(static match => match.Value)
                .Where(static term => !QuerySignalStopWords.Contains(term));

        var titleTerms = TitleTerms(Path.GetFileNameWithoutExtension(title))
            .ToHashSet(StringComparer.Ordinal);
        return TitleTerms(query).Distinct(StringComparer.Ordinal).Count(titleTerms.Contains);
    }
}
