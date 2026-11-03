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
    private static IEnumerable<string> BuildGenericRetrievalSemanticQueries(string query)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            yield break;

        var hasHistoricalVersion = ContainsHistoricalDocumentVersionSurface(normalizedQuery);
        var hasCurrentVersion = ContainsCurrentDocumentVersionSurface(normalizedQuery);
        var hasVersionSurface = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:version|versions|edition|editions|document|documents|file|fichier|pdf)\b",
            RegexOptions.CultureInvariant);
        var subjectTerms = Regex.Matches(normalizedQuery, @"[\p{L}\p{N}]{3,}", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericDocumentVersionRetrievalTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var subjectPrefix = subjectTerms.Length == 0 ? string.Empty : string.Join(' ', subjectTerms) + " ";

        if (hasHistoricalVersion && (hasVersionSurface || hasCurrentVersion))
        {
            yield return $"{subjectPrefix}old version";
            yield return $"{subjectPrefix}previous version";
            yield return $"{subjectPrefix}archived version";
            yield return $"{subjectPrefix}ancienne version";
        }

        if (hasCurrentVersion && (hasVersionSurface || hasHistoricalVersion))
        {
            yield return $"{subjectPrefix}current version";
            yield return $"{subjectPrefix}latest version";
            yield return $"{subjectPrefix}version courante";
            yield return $"{subjectPrefix}derniere version";
        }
    }

    private static IEnumerable<string> BuildGenericRetrievalSemanticVariants(string term)
    {
        switch (term)
        {
            case "propriete":
            case "proprietes":
            case "caracteristique":
            case "caracteristiques":
                yield return "property";
                yield return "properties";
                yield return "characteristic";
                yield return "characteristics";
                break;

            case "ancienne":
            case "ancien":
            case "precedente":
            case "precedent":
            case "anterieure":
            case "anterieur":
            case "older":
            case "previous":
            case "prior":
            case "archived":
            case "archive":
            case "legacy":
            case "obsolete":
                yield return "ancienne version";
                yield return "old version";
                yield return "previous version";
                yield return "archived version";
                break;

            case "actuelle":
            case "actuel":
            case "courante":
            case "courant":
            case "derniere":
            case "nouvelle":
            case "nouveau":
            case "current":
            case "latest":
            case "newest":
            case "recent":
                yield return "version courante";
                yield return "current version";
                yield return "latest version";
                yield return "derniere version";
                break;
        }
    }
}
