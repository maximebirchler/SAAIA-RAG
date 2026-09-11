using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeWriterControlLeak(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        return answer.Contains("SOURCE_BACKED_", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("PRIVATE_SOURCE_", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("TOOL_RESULTS", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("omittedFromWriterPrompt", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("writerEvidence", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("writerUse", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("evidenceRole", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("candidate(s)", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("slot(s)", StringComparison.OrdinalIgnoreCase)
            || answer.Contains("lead(s)", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(answer, @"\btool\s+result\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                answer,
                @"\b(?:piste\(s\)\s+sourc\w*|candidat\(s\)\s+sourc\w*|element\(s\)\s+sourc\w*|Ã©lÃ©ment\(s\)\s+sourc\w*|cr[Ã©e]neau\(x\)\s+demand\w*|banque\s+d['â€™]options|source-backed\s+leads?|candidate\s+bank|option\s+bank|documented\s+elements?\s+available|usable\s+starting\s+options?|without\s+adding\s+facts|limit\s+the\s+answer\s+to\s+excerpts?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                NormalizeLexicalLookup(answer),
                @"\b(?:source\s+backed\s+leads?|candidate\s+bank|option\s+bank|evidence\s+inventory|pistes?\s+sourcees?|piste\s+s\s+sourcee\s+s|banque\s+d\s+options?|candidats?\s+sources?|candidat\s+s\s+source\s+s|creneaux?\s+demandes?|creneau\s+x\s+demande\s+s|elements?\s+sources?\s+distincts?|element\s+s\s+source\s+s\s+distinct|elements?\s+documentes?\s+disponibles?|elements?\s+documentaires?\s+partiels?|sans\s+ajout\s+de\s+faits|limite\s+la\s+reponse\s+aux\s+extraits?|limite\s+la\s+reponse\s+aux\s+pages?|usable\s+starting\s+options?|documented\s+elements?\s+available|without\s+adding\s+facts|limit\s+the\s+answer\s+to\s+excerpts?|limito\s+la\s+respuesta\s+a\s+los?\s+extractos?|sin\s+anadir\s+hechos|limito\s+a\s+resposta\s+aos?\s+excertos?|sem\s+adicionar\s+factos|ich\s+beschranke\s+die\s+antwort\s+auf\s+auszuge|ohne\s+fakten\s+hinzuzufugen|limito\s+la\s+risposta\s+agli?\s+estratti|senza\s+aggiungere\s+fatti)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                NormalizeLexicalLookup(answer),
                @"\b(?:je\s+cite\s+ces\s+sources?\s+separement|je\s+cite\s+ces\s+sources?|i\s+cite\s+these\s+sources?\s+separately|i\s+cite\s+these\s+sources?|cito\s+estas\s+fuentes?\s+por\s+separado|cito\s+estas\s+fontes?\s+separadamente|ich\s+zitiere\s+diese\s+quellen?\s+separat|cito\s+queste\s+fonti\s+separatamente)\b",
                RegexOptions.CultureInvariant);
    }

    private static string ResolveRequestedAnswerShape(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return "auto";

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
            return "schedule_or_plan";

        if (LooksLikeComparativeDocumentaryRequest(query) || LooksLikeRankingDocumentaryRequest(query))
            return "comparison";

        if (Regex.IsMatch(
                normalized,
                @"\b(?:procedure|procedures|processus|process|etapes?|steps?|mode\s+d\s+emploi|comment\s+faire|how\s+to|workflow|marche\s+a\s+suivre|methode|method|procedimiento|procedimientos|proceso|procesos|pasos?|como\s+hacer|metodo|metodos|procedimento|procedimentos|processo|processos|passos?|como\s+fazer|methode|methoden|schritte?|anleitung|wie\s+geht|wie\s+mache|procedura|procedure|processo|processi|passi|come\s+fare|metodo|metodi)\b",
                RegexOptions.CultureInvariant))
        {
            return "procedure";
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:documents?\s+qui|sources?\s+qui|fichiers?\s+qui|quels?\s+documents?|which\s+documents?|which\s+sources?|liste|lister|list|documentos?\s+que|fuentes?\s+que|archivos?\s+que|que\s+documentos?|lista|listar|documentos?\s+que|fontes?\s+que|ficheiros?\s+que|quais?\s+documentos?|liste|listar|welche\s+dokumente?|welche\s+quellen?|quellen?\s+die|dokumente?\s+die|liste|auflisten|quali\s+documenti?|quali\s+fonti?|documenti?\s+che|fonti?\s+che|elenca|lista)\b",
                RegexOptions.CultureInvariant))
        {
            return "document_list";
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:resume|resumer|synthese|synthetise|summary|summarize|summarise|about|de\s+quoi\s+parle|resumen|resumir|sintesis|sintetiza|sobre\s+que\s+trata|resumo|resumir|sintese|sintetiza|sobre\s+o\s+que\s+fala|zusammenfassung|zusammenfassen|fasse|worum\s+geht|riassunto|riassumere|riassumi|sintesi|sintetizza|di\s+cosa\s+parla)\b",
                RegexOptions.CultureInvariant))
        {
            return "summary";
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|quels|quelles|quoi|choisir|choisis|choix|recommande|recommander|conseille|conseiller|propose|proposer|suggest|recommend|which|what|best|meilleur|meilleure|option|options|cual|cuales|que|elegir|elige|opcion|opciones|recomienda|recomendar|aconseja|aconsejar|propone|proponer|sugiere|sugerir|mejor|qual|quais|escolher|escolhe|opcao|opcoes|recomenda|recomendar|aconselha|aconselhar|propoe|propor|sugere|sugerir|melhor|welche|welcher|welches|was|waehlen|waehle|auswahl|optionen|empfiehl|empfehlen|rate|raten|vorschlag|beste|quale|quali|cosa|scegliere|scegli|scelta|opzione|opzioni|consiglia|consigliare|proponi|proporre|suggerisci|suggerire|migliore)\b",
                RegexOptions.CultureInvariant))
        {
            return "recommendation";
        }

        return "auto";
    }

    private static bool LooksLikeBroadSynthesisRequestShape(string? query)
    {
        var shape = ResolveRequestedAnswerShape(query);
        return shape is "schedule_or_plan" or "comparison" or "procedure" or "recommendation" or "document_list" or "summary"
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query);
    }

    private static bool LooksLikeRawExcerptDumpPlanningAnswer(string? answer, string? query)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || !LooksLikeBroadSynthesisRequestShape(query))
        {
            return false;
        }

        var text = answer.Trim();
        var sourceLeadLineCount = Regex.Matches(
            text,
            @"(?im)^\s*(?:[-*â€¢]|\d+[.)])?\s*[^:\r\n]{1,160}\.(?:pdf|docx?|xlsx?|pptx?)\s+(?:p\.?|pages?|pag\.?|p(?:a|\u00e1)gina|s\.?|seite)\s*\d+\s*(?::|-|â€“)",
            RegexOptions.CultureInvariant).Count;
        sourceLeadLineCount += Regex.Matches(
            text,
            @"(?im)^\s*\u2022\s*[^:\r\n]{1,160}\.(?:pdf|docx?|xlsx?|pptx?)\s+(?:p\.?|pages?|pag\.?|p(?:a|\u00e1)gina|s\.?|seite)\s*\d+\s*(?::|-|â€“)",
            RegexOptions.CultureInvariant).Count;
        if (sourceLeadLineCount >= 2)
            return true;

        var longBulletWithSourceCount = Regex.Matches(
            text,
            @"(?im)^\s*(?:[-*â€¢]|\d+[.)])\s+.{180,}\b(?:p\.?\s*\d+|pages?\s+\d+|pag\.?\s*\d+|p(?:a|\u00e1)gina\s+\d+|s\.?\s*\d+|seite\s+\d+)\b",
            RegexOptions.CultureInvariant).Count;
        longBulletWithSourceCount += Regex.Matches(
            text,
            @"(?im)^\s*\u2022\s+.{180,}\b(?:p\.?\s*\d+|pages?\s+\d+|pag\.?\s*\d+|p(?:a|\u00e1)gina\s+\d+|s\.?\s*\d+|seite\s+\d+)\b",
            RegexOptions.CultureInvariant).Count;
        longBulletWithSourceCount += Regex.Matches(
            text,
            @"(?im)^\s*(?:[-*\u2022\u25e6]|\d+[.)])\s+.{80,}\([^()\r\n]{1,180}\.(?:pdf|docx?|xlsx?|pptx?)\s+(?:p\.?|pages?|pag\.?|p(?:a|\u00e1)gina|s\.?|seite)\s*\d+\)",
            RegexOptions.CultureInvariant).Count;
        if (longBulletWithSourceCount >= 2 && LooksLikeWeeklyPlanningRequest(query))
            return true;

        var rawSourceReferenceCount = Regex.Matches(
            text,
            @"(?i)\b(?:pdf|docx?|xlsx?|pptx?)\s+(?:p\.?|pages?|pag\.?|p(?:a|\u00e1)gina|s\.?|seite)\s*\d+\s*(?::|-|â€“)",
            RegexOptions.CultureInvariant).Count;
        var organizationSignals = Regex.IsMatch(
            NormalizeLexicalLookup(text),
            @"\b(?:organisation|rotation|banque|options?|planning|calendrier|lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|schedule|monday|tuesday|wednesday|thursday|friday|saturday|sunday|semana|wochenplan|settimana)\b",
            RegexOptions.CultureInvariant);

        if (rawSourceReferenceCount >= 3 && !organizationSignals)
            return true;

        return sourceLeadLineCount >= 1
            && rawSourceReferenceCount >= 2
            && LooksLikeWeeklyPlanningRequest(query)
            && Regex.IsMatch(
                NormalizeLexicalLookup(text),
                @"\b(?:pistes?\s+trouvees?|passages?\s+trouves?|extraits?\s+trouves?|brut|bruts|raw)\b",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeOverfilledPartialPlanningAnswer(string answer, string? query)
    {
        if (!LooksLikeWeeklyPlanningRequest(query))
            return false;

        var normalized = NormalizeLexicalLookup(answer);
        var dayMentions = Regex.Matches(
                normalized,
                @"\b(?:lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday|lunes|martes|miercoles|jueves|viernes|sabado|domingo|segunda|terca|quarta|quinta|sexta|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|lunedi|martedi|mercoledi|giovedi|venerdi|sabato|domenica)\b",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var labelledLineCount = Regex.Matches(
            answer,
            @"(?im)^\s*(?:[-*\u2022\u25e6]|\d+[.)])?\s*[\p{L}\p{N}][\p{L}\p{N} '\-/]{2,48}\s*:",
            RegexOptions.CultureInvariant).Count;
        if (dayMentions < 3 && labelledLineCount < 6)
            return false;

        var sourceRefs = Regex.Matches(
                answer,
                @"(?i)\b[\p{L}\p{N}_ .,'()\-]+?\.(?:pdf|docx?|xlsx?|pptx?)\s*\(?\s*p\.?\s*\d+",
                RegexOptions.CultureInvariant)
            .Select(static match => NormalizeLexicalLookup(match.Value))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (sourceRefs.Count < 6)
            return false;

        var grouped = sourceRefs
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Count())
            .OrderByDescending(static count => count)
            .ToList();
        if (grouped.Count == 0)
            return false;

        return grouped.Count <= 3 && grouped[0] >= 3;
    }

    private static bool LooksLikePoorPlanningFallbackAnswer(string? answer, string? query)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var broadOrCollectionLikeQuery = LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSourceBackedOptionRequest(query);
        if (!broadOrCollectionLikeQuery)
            return false;

        if (LooksLikeWriterControlLeak(answer))
            return true;

        if (LooksLikeRawExcerptDumpPlanningAnswer(answer, query))
            return true;

        if (LooksLikeOverfilledPartialPlanningAnswer(answer, query))
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query)
            && LooksLikeMarkdownPipeTableAnswer(answer))
        {
            return true;
        }

        var normalized = NormalizeLexicalLookup(answer);
        if (Regex.IsMatch(
                normalized,
                @"\b(?:here\s+is\s+a\s+(?:cleaner\s+)?source\s+backed\s+starting\s+list|here\s+is\s+a\s+usable\s+starting\s+point|use\s+these\s+sourced\s+leads\s+as\s+a\s+base|cleaner\s+source\s+backed\s+starting\s+list|documented\s+candidates?\s+as\s+a\s+base|voici\s+une\s+liste\s+de\s+depart\s+plus\s+exploitable|utilise\s+ces\s+candidats?\s+documentes?\s+comme\s+base|a\s+utiliser\s+comme\s+point\s+de\s+depart|base\s+de\s+travail\s+exploitable\s+a\s+partir\s+des\s+elements?\s+sources?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:je\s+cite\s+ces\s+sources?\s+separement|je\s+limite\s+la\s+reponse\s+aux\s+pages?\s+retrouvees?|i\s+cite\s+these\s+sources?\s+separately|i\s+limit\s+the\s+answer\s+to\s+the\s+retrieved\s+pages?|cito\s+estas\s+fuentes?\s+por\s+separado|limito\s+la\s+respuesta\s+a\s+las\s+paginas?\s+recuperadas?|cito\s+estas\s+fontes?\s+separadamente|limito\s+a\s+resposta\s+as\s+paginas?\s+recuperadas?|ich\s+zitiere\s+diese\s+quellen?\s+separat|ich\s+beschranke\s+die\s+antwort\s+auf\s+die\s+gefundenen?\s+seiten?|cito\s+queste\s+fonti\s+separatamente|limito\s+la\s+risposta\s+alle\s+pagine\s+trovate)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:voici\s+la\s+tracabilite|preuves?\s+de\s+tracabilite|remplacement\s+changement\s+de\s+statut\s+ou\s+une?\s+applicabilite|traceability\s+that\s+i\s+can\s+establish|traceability\s+evidence|replacement\s+status\s+change\s+or\s+full\s+applicability|trazabilidad\s+que\s+puedo\s+establecer|pruebas?\s+de\s+trazabilidad|substitucion\s+cambio\s+de\s+estado\s+o\s+aplicabilidad|rastreabilidade\s+que\s+posso\s+estabelecer|provas?\s+de\s+rastreabilidade|substituicao\s+alteracao\s+de\s+estado\s+ou\s+aplicabilidade|rueckverfolgbarkeit\s+die\s+ich\s+herstellen\s+kann|nachweise?\s+der\s+rueckverfolgbarkeit|ersetzung\s+statusaenderung\s+oder\s+anwendbarkeit|tracciabilita\s+che\s+posso\s+stabilire|prove?\s+di\s+tracciabilita|sostituzione\s+cambio\s+di\s+stato\s+o\s+applicabilita)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
            @"\b(?:elements?\s+documentaires?\s+partiels?\s+sur|elements?\s+documentes?\s+disponibles?|elements?\s+exploitables?|sources?\s+donnent?\s+quelques?\s+elements?|options?\s+utilisables?\s+pour\s+demarrer|pas\s+assez\s+pour\s+remplir|sources?\s+recuperees?\s+ne\s+suffisent?|recherche\s+a\s+trouve\s+des\s+passages?\s+proches?|passages?\s+proches?\s+mais\s+ils\s+restent?|passages?\s+disponibles?\s+restent?\s+trop\s+faibles?|pages?\s+trouvees?\s+sont\s+trop\s+limitees?|sources?\s+plus\s+larges?\s+ou\s+plus\s+variees?|pour\s+produire\s+quelque\s+chose\s+de\s+fiable|pour\s+obtenir\s+un\s+planning\s+complet|pistes?\s+sourcees?\s+disponibles?|je\s+limite\s+la\s+reponse\s+aux\s+extraits?|je\s+peux\s+construire\s+une\s+base\s+exploitable|sources?\s+(?:retrouvees?\s+)?ne\s+prouvent?\s+pas|j\s+ai\s+trouve\s+\d+.{0,80}(?:pistes?|piste\s+s|elements?|element\s+s|candidats?|candidat\s+s).{0,60}(?:sourcees?|sourcee\s+s|sources?|source\s+s)|pistes?\s+sourcees?\s+distinctes?|piste\s+s\s+sourcee\s+s\s+distincte\s+s|creneaux?\s+demandes?|creneau\s+x\s+demande\s+s|banque\s+d\s+options?|partial\s+document\s+evidence\s+about|documented\s+base\s+is\s+incomplete|usable\s+elements?\s+for\s+\d+\s+requested\s+places|usable\s+starting\s+options?|i\s+can\s+build\s+an\s+usable\s+basis|search\s+found\s+nearby\s+passages?|available\s+passages?\s+are\s+still\s+too\s+weak|found\s+pages?\s+are\s+too\s+limited|broader\s+or\s+more\s+varied\s+sources?|sources?\s+do\s+not\s+prove|here\s+are\s+the\s+source\s+backed\s+leads?|without\s+adding\s+facts\s+quantities?\s+or\s+steps?|candidate\s+bank|option\s+bank|indicios?\s+documentales?\s+parciales?\s+sobre|indicios?\s+documentais?\s+parciais?\s+sobre|posso\s+construir\s+uma\s+base\s+util|as\s+fontes?\s+nao\s+provam|puedo\s+construir\s+una\s+base\s+util|las\s+fuentes?\s+no\s+prueban|ich\s+kann\s+eine\s+nutzbare\s+grundlage\s+erstellen|die\s+quellen?\s+belegen\s+nicht|posso\s+costruire\s+una\s+base\s+utile|le\s+fonti?\s+non\s+dimostrano)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (LooksLikeGenericCollectionOrListRequest(query)
            && Regex.IsMatch(
                normalized,
                @"\b(?:j\s+ai\s+trouve\s+l\s+element\s+demande|i\s+found\s+the\s+requested\s+item|elemento\s+solicitado|elemento\s+pedido|angefragte\s+element|elemento\s+richiesto)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var sourceLeadLineCount = Regex.Matches(
            answer,
            @"(?im)^\s*(?:[-*\u2022â€¢]|\d+[.)])?\s*[^:\r\n]{1,160}\.(?:pdf|docx?|xlsx?|pptx?)\s+p\.?\s*\d+\s*(?::|-|â€“)",
            RegexOptions.CultureInvariant).Count;
        return sourceLeadLineCount >= 2;
    }

    private static bool LooksLikeMarkdownPipeTableAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var lines = answer
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();

        var pipeRowCount = lines.Count(static line => line.Count(static ch => ch == '|') >= 3);
        if (pipeRowCount < 2)
            return false;

        var separatorRowCount = lines.Count(static line => Regex.IsMatch(
            line,
            @"^\|?\s*:?-{2,}:?\s*(?:\|\s*:?-{2,}:?\s*)+\|?$",
            RegexOptions.CultureInvariant));

        return separatorRowCount > 0;
    }

    private static bool LooksLikeUnderusedSourceBackedPlanningAnswer(string? answer, ToolResults toolResults, string? query)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || !LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeWeeklyPlanningRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query))
        {
            return false;
        }

        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        var candidates = SelectSourceBackedPlanningCandidates(toolResults, query, targetItemCount)
            .Take(Math.Min(5, targetItemCount))
            .ToList();
        if (candidates.Count < 2)
            return false;

        var normalizedAnswer = NormalizeLexicalLookup(answer);
        var mentionedCandidates = candidates.Count(candidate => SourceBackedPlanningCandidateMentioned(normalizedAnswer, candidate));
        if (mentionedCandidates >= Math.Min(2, candidates.Count))
            return false;

        var sourceLinkCount = Regex.Matches(answer, @"\[\[open\|", RegexOptions.CultureInvariant).Count;
        if (sourceLinkCount >= Math.Min(2, candidates.Count))
            return false;

        var bulletLikeLineCount = Regex.Matches(
            answer,
            @"(?m)^\s*(?:[-*]|\d+[.)]|(?:jour|day|dia|tag|giorno|option|opcion|opcao|opzione)\s+\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;

        return bulletLikeLineCount < Math.Min(2, candidates.Count);
    }

    private static bool SourceBackedPlanningCandidateMentioned(string normalizedAnswer, SourceBackedOptionCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
            return false;

        var title = NormalizeLexicalLookup(candidate.Title);
        if (!string.IsNullOrWhiteSpace(title) && title.Length >= 4 && normalizedAnswer.Contains(title, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractQuerySignalTerms(title)
            .Where(static term => term.Length >= 5)
            .Take(4)
            .ToArray();
        if (titleTerms.Length >= 2 && titleTerms.Count(term => normalizedAnswer.Contains(term, StringComparison.Ordinal)) >= 2)
            return true;

        return false;
    }
}
