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
        var terms = ExtractQuerySignalTerms(normalizedQuery)
            .SelectMany(BuildRetrievalTermVariants)
            .ToHashSet(StringComparer.Ordinal);
        if (terms.Count == 0)
            yield break;

        var hasProperty = terms.Contains("propriete")
            || terms.Contains("proprietes")
            || terms.Contains("property")
            || terms.Contains("properties");
        var hasElectrical = terms.Contains("electrique")
            || terms.Contains("electriques")
            || terms.Contains("electric")
            || terms.Contains("electrical")
            || terms.Contains("dielectric");
        var hasThermal = terms.Contains("thermique")
            || terms.Contains("thermiques")
            || terms.Contains("thermal")
            || terms.Contains("temperature")
            || terms.Contains("melt point")
            || terms.Contains("melting point");
        var hasSafetyDataSheet = LooksLikeSafetyDataSheetDocumentTypeRequest(query);
        var hasHistoricalVersion = ContainsHistoricalDocumentVersionSurface(normalizedQuery);
        var hasCurrentVersion = ContainsCurrentDocumentVersionSurface(normalizedQuery);
        var hasVersionSurface = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:version|versions|edition|editions|document|documents|file|fichier|pdf)\b",
            RegexOptions.CultureInvariant);
        var subjectTerms = Regex.Matches(normalizedQuery, @"[\p{L}\p{N}]{3,}", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericDocumentVersionOrTypeRetrievalTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var subjectPrefix = subjectTerms.Length == 0 ? string.Empty : string.Join(' ', subjectTerms) + " ";

        if (hasElectrical)
        {
            yield return hasProperty ? "electrical properties" : "electrical";
            yield return "dielectric properties";
        }

        if (hasThermal)
        {
            yield return hasProperty ? "thermal properties" : "thermal";
            yield return "melt point temperature";
        }

        if (hasSafetyDataSheet)
        {
            yield return $"{subjectPrefix}safety data sheet";
            yield return $"{subjectPrefix}material safety data sheet";
            yield return $"{subjectPrefix}msds";
            yield return $"{subjectPrefix}sds";
            yield return $"{subjectPrefix}fiche de donnees de securite";
        }

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

            case "electrique":
            case "electriques":
            case "electric":
            case "electrical":
                yield return "electric";
                yield return "electrical";
                yield return "dielectric";
                break;

            case "thermique":
            case "thermiques":
            case "thermal":
                yield return "thermal";
                yield return "temperature";
                yield return "melt point";
                yield return "melting point";
                break;

            case "msds":
            case "sds":
            case "fds":
                yield return "msds";
                yield return "sds";
                yield return "safety data sheet";
                yield return "material safety data sheet";
                yield return "fiche de donnees de securite";
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
