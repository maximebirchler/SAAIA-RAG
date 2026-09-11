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

    private static string BuildSourceBackedExactItemAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits, string query)
    {
        if (LooksLikeGenericCollectionOrListRequest(query))
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var displayTitle = ResolveSourceBackedExactItemDisplayTitle(requestedTitle, hits);
        var header = language switch
        {
            "en" => $"I found \"{displayTitle}\" in the available sources. Here is the source-backed information I can use from the cited pages:",
            "es" => $"He encontrado \"{displayTitle}\" en las fuentes disponibles. Estos son los datos con fuente que puedo usar desde las pÃƒÂ¡ginas citadas:",
            "pt" => $"Encontrei \"{displayTitle}\" nas fontes disponÃƒÂ­veis. Estas sÃƒÂ£o as informaÃƒÂ§ÃƒÂµes com fonte que posso usar a partir das pÃƒÂ¡ginas citadas:",
            "de" => $"Ich habe \"{displayTitle}\" in den verfÃƒÂ¼gbaren Quellen gefunden. Das sind die belegten Informationen aus den zitierten Seiten:",
            "it" => $"Ho trovato \"{displayTitle}\" nelle fonti disponibili. Queste sono le informazioni documentate che posso usare dalle pagine citate:",
            _ => $"J'ai trouvÃƒÂ© Ã‚Â« {displayTitle} Ã‚Â» dans les sources disponibles. Voici les informations sourcÃƒÂ©es que je peux utiliser depuis les pages citÃƒÂ©es :"
        };

        if (LooksLikeParameterLookupRequest(query))
            return BuildSourceBackedExactItemParameterAnswer(language, requestedTitle, hits, header);

        var hasStructuredExactItemEvidence = hits.Any(hit =>
            RagHitContainsRequestedTitle(hit, requestedTitle)
            && (ComputeExactItemCardCompletenessCueScore(hit) >= 8
                || HasContentCardEvidenceFacts(hit)));
        if (LooksLikeStructuredItemCardRequest(query) || hasStructuredExactItemEvidence)
            return BuildSourceBackedExactItemCardAnswer(language, requestedTitle, hits, header);

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            if (ShouldIncludeRawSourceExcerptForAnswerLanguage(language, hit))
            {
                var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: SourceBackedEvidenceMaxChars);
                sb.AppendLine(excerpt);
            }
            else
            {
                sb.AppendLine(SourceBackedLabel(
                    language,
                    "extrait disponible dans la langue du document sur la page citee",
                    "excerpt available in the document language on the cited page",
                    "extracto disponible en el idioma del documento en la pagina citada",
                    "excerto disponivel na lingua do documento na pagina citada",
                    "Auszug in der Dokumentsprache auf der zitierten Seite verfuegbar",
                    "estratto disponibile nella lingua del documento nella pagina citata"));
            }
        }

        var note = language switch
        {
            "en" => "If you need a step-by-step card, I can only expand the parts visible in these excerpts.",
            "es" => "Si necesitas una ficha paso a paso, solo puedo desarrollar las partes visibles en estos extractos.",
            "pt" => "Se precisares de uma ficha passo a passo, so posso desenvolver as partes visiveis nestes excertos.",
            "de" => "Falls du eine Schritt-fuer-Schritt-Karte brauchst, kann ich nur die in diesen Auszuegen sichtbaren Teile ausarbeiten.",
            "it" => "Se ti serve una scheda passo passo, posso sviluppare solo le parti visibili in questi estratti.",
            _ => "Si tu veux une fiche pas a pas, je ne peux developper que les elements visibles dans ces extraits."
        };
        sb.AppendLine(note);
        AppendSourceBackedExtractionQualityCaveat(sb, hits, language);
        return sb.ToString().TrimEnd();
    }

}
