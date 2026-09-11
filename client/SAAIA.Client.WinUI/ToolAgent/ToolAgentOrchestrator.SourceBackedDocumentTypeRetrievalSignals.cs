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
    private static bool IsGenericDocumentVersionOrTypeRetrievalTerm(string term)
        => Regex.IsMatch(
            NormalizeLexicalLookup(term),
            @"^(?:and|the|for|backed|compare|comparing|comparaison|comparison|version|versions|edition|editions|document|documents|file|fichier|pdf|old|older|previous|prior|archived|archive|legacy|obsolete|ancienne|ancien|precedente|precedent|anterieure|anterieur|current|latest|newest|recent|actuelle|actuel|courante|courant|derniere|nouvelle|nouveau|safety|securite|data|sheet|fiche|fiches|donnees|msds|sds|fds)$",
            RegexOptions.CultureInvariant);

    private static bool LooksLikeSafetyDataSheetDocumentTypeRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
                   normalized,
                   @"\b(?:msds|sds|fds|material\s+safety\s+data\s+sheets?|safety\s+data\s+sheets?|fiches?\s+de\s+donnees\s+de\s+securite|fiches?\s+donnees\s+securite|fiches?\s+de\s+securite)\b",
                   RegexOptions.CultureInvariant)
               || (Regex.IsMatch(normalized, @"\b(?:safety|securite)\b", RegexOptions.CultureInvariant)
                   && Regex.IsMatch(normalized, @"\b(?:data\s+sheets?|fiches?|donnees)\b", RegexOptions.CultureInvariant));
    }

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

    private static int ComputeRequestedDocumentTypeAnchorScore(string? query, RagHitSummary hit)
    {
        if (!LooksLikeSafetyDataSheetDocumentTypeRequest(query))
            return 0;

        var titleSignal = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            hit.DocName,
            hit.DocPath,
            hit.SectionTitle,
            hit.HeadingPath,
            string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (string.IsNullOrWhiteSpace(titleSignal))
            return 0;

        if (Regex.IsMatch(titleSignal, @"\b(?:msds|sds|fds)\b", RegexOptions.CultureInvariant))
            return 160;
        if (Regex.IsMatch(
                titleSignal,
                @"\b(?:material\s+safety\s+data\s+sheets?|safety\s+data\s+sheets?|fiches?\s+de\s+donnees\s+de\s+securite|fiches?\s+donnees\s+securite|fiches?\s+de\s+securite)\b",
                RegexOptions.CultureInvariant))
        {
            return 140;
        }

        return 0;
    }
}
