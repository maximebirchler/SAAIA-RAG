using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildMissingExplicitDocumentAnswer(string language, string requestedDocument, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        requestedDocument = CleanupRequestedItemTitle(requestedDocument) ?? CollapseWhitespace(requestedDocument);
        var closeLeads = SelectMissingExactItemCloseLeads(requestedDocument, hits)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .Take(3)
            .ToList();

        var header = (closeLeads.Count > 0, language) switch
        {
            (true, "en") => $"I did not find the requested document \"{requestedDocument}\" in the indexed corpus. I will not summarize it or use it as a source. Closest documented alternatives:",
            (true, "es") => $"No he encontrado el documento solicitado \"{requestedDocument}\" en el corpus indexado. No voy a resumirlo ni usarlo como fuente. Pistas cercanas con fuente:",
            (true, "pt") => $"Nao encontrei o documento solicitado \"{requestedDocument}\" no corpus indexado. Nao vou resume-lo nem usa-lo como fonte. Pistas proximas com fonte:",
            (true, "de") => $"Ich habe das angefragte Dokument \"{requestedDocument}\" im indexierten Korpus nicht gefunden. Ich fasse es nicht zusammen und verwende es nicht als Quelle. Naheliegende belegte Hinweise:",
            (true, "it") => $"Non ho trovato il documento richiesto \"{requestedDocument}\" nel corpus indicizzato. Non lo riassumo ne lo uso come fonte. Indicazioni vicine con fonte:",
            (true, _) => $"Je n'ai pas trouvÃ© le document demandÃ© \"{requestedDocument}\" dans le corpus indexÃ©. Je ne le rÃ©sume pas et je ne l'utilise pas comme source principale. Alternatives documentÃ©es les plus proches :",
            (false, "en") => $"I did not find the requested document \"{requestedDocument}\" in the indexed corpus. I will not summarize it or use it as a source.",
            (false, "es") => $"No he encontrado el documento solicitado \"{requestedDocument}\" en el corpus indexado. No voy a resumirlo ni usarlo como fuente.",
            (false, "pt") => $"Nao encontrei o documento solicitado \"{requestedDocument}\" no corpus indexado. Nao vou resume-lo nem usa-lo como fonte.",
            (false, "de") => $"Ich habe das angefragte Dokument \"{requestedDocument}\" im indexierten Korpus nicht gefunden. Ich fasse es nicht zusammen und verwende es nicht als Quelle.",
            (false, "it") => $"Non ho trovato il documento richiesto \"{requestedDocument}\" nel corpus indicizzato. Non lo riassumo ne lo uso come fonte.",
            _ => $"Je n'ai pas trouvÃ© le document demandÃ© \"{requestedDocument}\" dans le corpus indexÃ©. Je ne le rÃ©sume pas et je ne l'utilise pas comme source principale."
        };

        if (closeLeads.Count == 0)
            return header;

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in closeLeads)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = CollapseWhitespace(hit.Excerpt);
            if (excerpt.Length > 240)
                excerpt = excerpt[..240].TrimEnd() + "...";

            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildMissingExactItemAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var closeLeads = SelectMissingExactItemCloseLeads(requestedTitle, hits);
        if (closeLeads.Count == 0)
        {
            var fallbackCandidates = hits
                .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit))
                .Select(hit => new
                {
                    Hit = hit,
                    Relevance = ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)),
                    Structure = ComputeExactItemCardCompletenessCueScore(hit)
                })
                .Where(item => !LooksLikePageReferenceOnlyHit(item.Hit) || item.Structure >= 4 || item.Hit.PageEnd > item.Hit.PageStart)
                .OrderByDescending(item => item.Structure >= 4)
                .ThenByDescending(item => item.Relevance)
                .Take(3)
                .Select(item => item.Hit)
                .ToList();
            closeLeads = fallbackCandidates;
        }
        else if (closeLeads.Any(LooksLikePageReferenceOnlyHit))
        {
            closeLeads = closeLeads
                .Concat(hits
                    .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                    .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                    .Where(hit => !LooksLikePageReferenceOnlyHit(hit))
                    .Where(hit => !closeLeads.Any(existing => SameRagHitRange(existing, hit))))
                .OrderBy(hit => LooksLikePageReferenceOnlyHit(hit) ? 1 : 0)
                .ThenByDescending(hit => ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(4)
                .ToList();
        }

        var typoResolutionLeads = closeLeads.Count > 0
            ? closeLeads
            : hits
                .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .OrderByDescending(hit => ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(4)
                .ToList();
        var correctedTitle = TryResolveTypoTolerantCloseTitle(requestedTitle, typoResolutionLeads);
        if (!string.IsNullOrWhiteSpace(correctedTitle))
        {
            var usableTypoResolutionLeads = typoResolutionLeads
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(correctedTitle!, hit))
                .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                .Where(hit => !LooksLikePageReferenceOnlyHit(hit))
                .Where(hit => ExactItemEvidenceStartsWithRequestedTitle(correctedTitle!, hit)
                    || RagHitHasUsableRequestedTitleAnchor(correctedTitle!, hit))
                .ToList();
            if (usableTypoResolutionLeads.Count > 0)
            {
                return BuildSourceBackedExactItemAnswer(
                    language,
                    correctedTitle!,
                    usableTypoResolutionLeads,
                    $"{correctedTitle} details source");
            }

            closeLeads = closeLeads
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(correctedTitle!, hit))
                .ToList();
        }

        var header = (closeLeads.Count > 0, language) switch
        {
            (true, "en") => $"I did not find the exact requested item \"{requestedTitle}\" in the available excerpts. I will not invent missing facts, quantities, steps, or details. Closest documented alternatives:",
            (true, "es") => $"No he encontrado el elemento exacto solicitado \"{requestedTitle}\" en los extractos disponibles. No voy a inventar hechos, cantidades, pasos ni detalles. Pistas cercanas con fuente:",
            (true, "pt") => $"Nao encontrei o item exato solicitado \"{requestedTitle}\" nos excertos disponiveis. Nao vou inventar factos, quantidades, passos nem detalhes. Pistas proximas com fonte:",
            (true, "de") => $"Ich habe den exakt angefragten Eintrag \"{requestedTitle}\" in den verfuegbaren Auszuegen nicht gefunden. Ich erfinde keine Fakten, Mengen, Schritte oder Details. Naheliegende belegte Hinweise:",
            (true, "it") => $"Non ho trovato l'elemento esatto richiesto \"{requestedTitle}\" negli estratti disponibili. Non invento fatti, quantita, passaggi o dettagli. Indicazioni vicine con fonte:",
            (true, _) => $"Je n'ai pas trouvÃ© l'Ã©lÃ©ment exact demandÃ© \"{requestedTitle}\" dans les extraits disponibles. Je n'invente donc pas les faits, quantitÃ©s, Ã©tapes ou dÃ©tails manquants. Alternatives documentÃ©es les plus proches :",
            (false, "en") => $"I did not find the exact requested item \"{requestedTitle}\" in the available excerpts. I will not invent missing facts, quantities, steps, or details.",
            (false, "es") => $"No he encontrado el elemento exacto solicitado \"{requestedTitle}\" en los extractos disponibles. No voy a inventar hechos, cantidades, pasos ni detalles.",
            (false, "pt") => $"Nao encontrei o item exato solicitado \"{requestedTitle}\" nos excertos disponiveis. Nao vou inventar factos, quantidades, passos nem detalhes.",
            (false, "de") => $"Ich habe den exakt angefragten Eintrag \"{requestedTitle}\" in den verfuegbaren Auszuegen nicht gefunden. Ich erfinde keine Fakten, Mengen, Schritte oder Details.",
            (false, "it") => $"Non ho trovato l'elemento esatto richiesto \"{requestedTitle}\" negli estratti disponibili. Non invento fatti, quantita, passaggi o dettagli.",
            _ => $"Je n'ai pas trouvÃ© l'Ã©lÃ©ment exact demandÃ© \"{requestedTitle}\" dans les extraits disponibles. Je n'invente donc pas les faits, quantitÃ©s, Ã©tapes ou dÃ©tails manquants."
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in closeLeads)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = CollapseWhitespace(hit.Excerpt);
            if (excerpt.Length > 240)
                excerpt = excerpt[..240].TrimEnd() + "...";

            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? TryResolveTypoTolerantCloseTitle(string requestedTitle, IReadOnlyList<RagHitSummary> closeLeads)
    {
        if (closeLeads.Count == 0)
            return null;

        var candidates = closeLeads
            .SelectMany(hit => ExtractSourceBackedTitleCandidates(hit).Concat(ExtractPlanItemTitleCandidatesV2(GetPlanExtractionText(hit))))
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsableSourceBackedOptionTitle)
            .Select(title => new
            {
                Title = title,
                Score = ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, title)
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Title.Length)
            .ToArray();

        var best = candidates.FirstOrDefault(static item => item.Score >= 8);
        if (best is null)
        {
            foreach (var variant in BuildTypoTolerantQueryVariants(requestedTitle))
            {
                var normalizedVariant = NormalizeLexicalLookup(variant);
                if (string.IsNullOrWhiteSpace(normalizedVariant))
                    continue;

                var variantTerms = ExtractRequestedTitleSignalTerms(normalizedVariant);
                if (variantTerms.Length == 0)
                    continue;

                var hasVariantEvidence = closeLeads.Any(hit =>
                {
                    var lookup = NormalizeLexicalLookup(GetRagHitLookupText(hit));
                    return variantTerms.All(term => lookup.Contains(term, StringComparison.Ordinal));
                });
                if (hasVariantEvidence)
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalizedVariant);
            }

            return null;
        }

        return string.Equals(NormalizeLexicalLookup(best.Title), NormalizeLexicalLookup(requestedTitle), StringComparison.Ordinal)
            ? null
            : best.Title;
    }

    private sealed record SourceBackedPlanningCoverage(
        int CandidateCount,
        int DistinctSourcePages,
        int MinimumCandidates,
        int TargetSlots,
        bool HasRequiredAnchor,
        int RichEvidenceCount,
        int EvidenceRichnessScore,
        bool IsAdequate,
        int Score);
}
