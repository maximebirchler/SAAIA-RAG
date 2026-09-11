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

    internal static bool ShouldUseSourceBackedExtractiveAnswer(string query, ToolResults toolResults)
    {
        var hits = EnumerateRagHitSummaries(toolResults).Take(8).ToList();
        if (hits.Count == 0)
            return false;

        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        if (LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
            return true;

        if (Regex.IsMatch(
                normalizedQuery,
            @"\b(?:fiche|fiches|card|cards|element|elements|item|items|etape|etapes|Ã©tape|Ã©tapes|step|steps|procedure|procedures|process|processus)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return LooksLikeSourceBackedQuantityScalingRequest(query)
            || LooksLikeSourceBackedAdaptationRequest(query)
            || LooksLikeRankingDocumentaryRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query)
            || LooksLikeShortTechnicalEvidenceTopic(query);
    }

    private static string TryBuildCorpusClaimVerificationAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeCorpusClaimVerificationRequest(query))
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var hits = SelectSourceBackedExtractiveHits(toolResults, query, maxHits: 5)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(4)
            .ToList();
        if (hits.Count == 0)
            return string.Empty;

        var header = SourceBackedLabel(
            language,
            "Les pages retrouvÃ©es ne suffisent pas Ã  confirmer une obligation gÃ©nÃ©rale ou une rÃ¨gle qui s'appliquerait toujours. Voici la vÃ©rification prudente possible avec les sources citÃ©es :",
            "I cannot confirm a general obligation or an \"always\" claim from the retrieved excerpts alone. I will keep this to a cautious source-backed check:",
            "No puedo confirmar una obligacion general o un \"siempre\" solo con los extractos recuperados. Limito la respuesta a una verificacion prudente:",
            "Nao posso confirmar uma obrigacao geral ou um \"sempre\" apenas com os excertos recuperados. Limito a resposta a uma verificacao prudente:",
            "Ich kann aus den gefundenen Auszuegen keine allgemeine Pflicht oder ein \"immer\" bestaetigen. Ich beschraenke die Antwort auf eine vorsichtige belegte Pruefung:",
            "Non posso confermare un obbligo generale o un \"sempre\" solo dagli estratti recuperati. Limito la risposta a una verifica prudente:");
        var conclusion = SourceBackedLabel(
            language,
            "Conclusion prudente : l'hypothÃ¨se doit Ãªtre confirmÃ©e document par document. Je ne la traite pas comme une rÃ¨gle universelle tant qu'une page source citÃ©e ne le dit pas explicitement.",
            "Cautious conclusion: treat the claim as limited or to be confirmed document by document, not as a universal rule, unless a cited source page says so explicitly.",
            "Conclusion prudente: trata la hipotesis como limitada o pendiente de confirmar documento por documento, no como regla universal, salvo que una pagina citada lo diga explicitamente.",
            "Conclusao prudente: trata a hipotese como limitada ou a confirmar documento por documento, nao como regra universal, salvo se uma pagina citada o disser explicitamente.",
            "Vorsichtige Schlussfolgerung: Behandle die Annahme als begrenzt oder dokumentweise zu pruefen, nicht als allgemeine Regel, ausser eine zitierte Quellseite sagt es ausdruecklich.",
            "Conclusione prudente: tratta l'ipotesi come limitata o da confermare documento per documento, non come regola universale, salvo che una pagina citata lo dica esplicitamente.");

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in hits.Take(4))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 240);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        sb.Append(conclusion);
        return sb.ToString().TrimEnd();
    }

    private static string TryBuildComparativeDocumentaryAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeComparativeDocumentaryRequest(query))
            return string.Empty;
        if (LooksLikeRankingDocumentaryRequest(query))
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var hits = SelectComparativeDocumentaryHits(EnumerateRagHitSummaries(toolResults), query, maxHits: 8)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit) || MatchesExplicitComparativeDocumentReference(query, hit))
            .GroupBy(hit => string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(hit => ComputeComparativeDocumentaryEvidenceScore(hit, BuildRagEvidenceSelectionQuery(query), ExtractQuerySignalTerms(NormalizeLexicalLookup(query)).ToArray())).First())
            .Take(4)
            .ToList();
        if (hits.Select(hit => string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() < 2)
        {
            return string.Empty;
        }

        var labels = language switch
        {
            "en" => (
                Header: "Here is a source-separated comparison limited to the retrieved excerpts:",
                PerDoc: "By document:",
                Common: "Common ground: the selected sources address the requested theme, but I do not merge their rules beyond the cited excerpts.",
                Difference: "Differences: each document must be read in its own framework; use the cited pages to compare wording, scope and procedure.",
                Limit: "Limit: this is not an exhaustive legal or technical comparison of the full documents."),
            "es" => (
                Header: "Comparacion separada por fuente, limitada a los extractos recuperados:",
                PerDoc: "Por documento:",
                Common: "Puntos comunes: las fuentes seleccionadas tratan el tema pedido, pero no fusiono sus reglas mas alla de los extractos citados.",
                Difference: "Diferencias: cada documento debe leerse en su propio marco; usa las paginas citadas para comparar redaccion, alcance y procedimiento.",
                Limit: "Limite: no es una comparacion juridica o tecnica exhaustiva de los documentos completos."),
            "pt" => (
                Header: "Comparacao separada por fonte, limitada aos excertos recuperados:",
                PerDoc: "Por documento:",
                Common: "Pontos comuns: as fontes selecionadas tratam o tema pedido, mas nao junto as regras para alem dos excertos citados.",
                Difference: "Diferencas: cada documento deve ser lido no seu proprio enquadramento; usa as paginas citadas para comparar redacao, ambito e procedimento.",
                Limit: "Limite: nao e uma comparacao juridica ou tecnica exaustiva dos documentos completos."),
            "de" => (
                Header: "Quellengetrennte Gegenueberstellung, begrenzt auf die gefundenen Auszuege:",
                PerDoc: "Nach Dokument:",
                Common: "Gemeinsamkeit: Die ausgewaehlten Quellen behandeln das angefragte Thema, aber ich fuehre ihre Regeln nicht ueber die zitierten Auszuege hinaus zusammen.",
                Difference: "Unterschiede: Jedes Dokument muss in seinem eigenen Rahmen gelesen werden; nutze die zitierten Seiten fuer Wortlaut, Geltungsbereich und Verfahren.",
                Limit: "Grenze: Dies ist kein vollstaendiger rechtlicher oder technischer Vergleich der Gesamtdokumente."),
            "it" => (
                Header: "Confronto separato per fonte, limitato agli estratti recuperati:",
                PerDoc: "Per documento:",
                Common: "Punti comuni: le fonti selezionate trattano il tema richiesto, ma non fondo le loro regole oltre gli estratti citati.",
                Difference: "Differenze: ogni documento va letto nel proprio quadro; usa le pagine citate per confrontare formulazione, ambito e procedura.",
                Limit: "Limite: non e un confronto giuridico o tecnico esaustivo dei documenti completi."),
            _ => (
                Header: "Voici une comparaison sÃ©parÃ©e par source, limitÃ©e aux passages retrouvÃ©s :",
                PerDoc: "Par document :",
                Common: "Points communs : les sources sÃ©lectionnÃ©es traitent le thÃ¨me demandÃ©, mais je ne fusionne pas leurs rÃ¨gles au-delÃ  des passages citÃ©s.",
                Difference: "DiffÃ©rences : chaque document doit Ãªtre lu dans son propre cadre ; utilise les pages citÃ©es pour comparer formulation, pÃ©rimÃ¨tre et procÃ©dure.",
                Limit: "Limite : ce n'est pas une comparaison juridique ou technique exhaustive des documents complets.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.PerDoc);
        foreach (var hit in hits)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        sb.AppendLine(labels.Common);
        sb.AppendLine(labels.Difference);
        sb.Append(labels.Limit);
        return sb.ToString().TrimEnd();
    }

    private static bool MatchesExplicitComparativeDocumentReference(string? query, RagHitSummary hit)
    {
        if (!LooksLikeComparativeDocumentaryRequest(query))
            return false;

        foreach (var fileReference in ExtractExplicitDocumentFileReferenceQueries(query).Take(6))
        {
            if (CandidateMatchesDocumentIdentity(fileReference, hit.DocName, hit.DocPath))
                return true;
        }

        return false;
    }

    private static bool MatchesRequestedDocumentFileIdentity(string? requestedTitle, RagHitSummary hit)
    {
        return !string.IsNullOrWhiteSpace(requestedTitle)
            && Regex.IsMatch(requestedTitle!, @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && CandidateMatchesDocumentIdentity(requestedTitle!, hit.DocName, hit.DocPath);
    }

    private static string? ResolveRequestedDocumentFileTitle(string? query, string? requestedTitle)
    {
        if (!string.IsNullOrWhiteSpace(requestedTitle)
            && Regex.IsMatch(requestedTitle!, @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return requestedTitle;
        }

        var explicitPdfTitle = TryExtractPdfFileNameRequestedTitle(query);
        if (!string.IsNullOrWhiteSpace(explicitPdfTitle))
            return explicitPdfTitle;

        return ExtractExplicitDocumentFileReferenceQueries(query).FirstOrDefault();
    }

    internal static string BuildSourceBackedExtractiveAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        var versionTraceabilityAnswer = TryBuildDocumentVersionTraceabilityAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(versionTraceabilityAnswer))
            return versionTraceabilityAnswer;

        var missingRequiredEvidence = TryBuildMissingRequiredEvidenceAnswer(toolResults, query, language);
        if (!ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(toolResults, query)
            && !string.IsNullOrWhiteSpace(missingRequiredEvidence))
            return missingRequiredEvidence;

        var corpusClaimVerificationAnswer = TryBuildCorpusClaimVerificationAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(corpusClaimVerificationAnswer))
            return corpusClaimVerificationAnswer;

        var comparativeAnswer = TryBuildComparativeDocumentaryAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(comparativeAnswer))
            return comparativeAnswer;

        var allHits = EnumerateRagHitSummaries(toolResults)
            .OrderBy(hit => LooksLikeNavigationOnlyHit(hit) ? 1 : 0)
            .ThenByDescending(hit => ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit)))
            .ThenByDescending(hit => hit.Score)
            .ToList();

        if (allHits.Count == 0)
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query,
                query,
                nearbyHitCount: 0);

        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var requestedTitle = LooksLikeShortTechnicalEvidenceTopic(evidenceQuery)
            ? null
            : TryExtractRequestedItemTitle(query);
        var requestedDocumentFileTitle = ResolveRequestedDocumentFileTitle(query, requestedTitle);
        var requestedTitleIsDocumentFile = !string.IsNullOrWhiteSpace(requestedDocumentFileTitle);
        var isQuantityScalingRequest = LooksLikeSourceBackedQuantityScalingRequest(query);
        if (!isQuantityScalingRequest
            && !requestedTitleIsDocumentFile
            && !string.IsNullOrWhiteSpace(requestedTitle)
            && !allHits.Any(hit => (RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit)
                    || IsUsableExactItemNavigationOverride(requestedTitle!, hit))
                && !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit)))
        {
            var answer = BuildMissingExactItemAnswer(language, requestedTitle!, allHits);
            return LooksLikeSourceBypassOrUnsupportedInventionRequest(query)
                ? ApplySourcePolicyGuardPrefix(answer, language)
                : answer;
        }

        var hits = SelectSourceBackedExtractiveHits(toolResults, query, maxHits: 4).ToList();
        if (requestedTitleIsDocumentFile)
        {
            var documentHits = hits
                .Where(hit => CandidateMatchesDocumentIdentity(requestedDocumentFileTitle!, hit.DocName, hit.DocPath))
                .ToList();
            if (documentHits.Count == 0)
            {
                documentHits = allHits
                    .Where(hit => CandidateMatchesDocumentIdentity(requestedDocumentFileTitle!, hit.DocName, hit.DocPath))
                    .Take(4)
                    .ToList();
            }

            if (documentHits.Count > 0)
                hits = documentHits;
        }
        if (!isQuantityScalingRequest
            && !requestedTitleIsDocumentFile
            && !string.IsNullOrWhiteSpace(requestedTitle)
            && hits.Count == 0)
        {
            var answer = BuildMissingExactItemAnswer(language, requestedTitle!, allHits);
            return LooksLikeSourceBypassOrUnsupportedInventionRequest(query)
                ? ApplySourcePolicyGuardPrefix(answer, language)
                : answer;
        }

        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        if (requestedMaxMinutes.HasValue)
        {
            var withinTimeHits = hits
                .Where(hit =>
                {
                    var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                    return !visibleMinutes.HasValue || visibleMinutes.Value <= requestedMaxMinutes.Value;
                })
                .ToList();
            if (withinTimeHits.Count > 0)
                hits = withinTimeHits;
        }

        var excludedTerms = ExtractSourceBackedExcludedTerms(query);
        if (excludedTerms.Count > 0)
        {
            var compliantHits = hits
                .Where(hit => !RagHitContainsAnyExcludedTerm(hit, excludedTerms))
                .ToList();
            if (compliantHits.Count == 0)
                return BuildNoSourceBackedCompliantOptionAnswer(language, excludedTerms, hits);

            hits = compliantHits;
        }

        if (isQuantityScalingRequest)
        {
            var scaledAnswer = BuildSourceBackedQuantityScalingAnswer(language, query, hits);
            if (!string.IsNullOrWhiteSpace(scaledAnswer))
                return scaledAnswer;
        }

        if (LooksLikeSourceBackedAdaptationRequest(query))
        {
            var adaptationAnswer = BuildSourceBackedAdaptationAnswer(language, query, hits);
            if (!string.IsNullOrWhiteSpace(adaptationAnswer))
                return adaptationAnswer;
        }

        if (LooksLikeRankingDocumentaryRequest(query))
        {
            var rankingAnswer = BuildSourceBackedRankingAnswer(language, query, hits);
            if (!string.IsNullOrWhiteSpace(rankingAnswer))
                return rankingAnswer;
        }

        if (!string.IsNullOrWhiteSpace(requestedTitle) && !requestedTitleIsDocumentFile)
        {
            if (hits.Count > 0 && hits.Any(hit => (RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit)
                        || IsUsableExactItemNavigationOverride(requestedTitle!, hit))
                    && !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit)))
                return BuildSourceBackedExactItemAnswer(language, requestedTitle!, hits, query);
        }

        var sb = new StringBuilder();
        sb.Append(BuildSourceBackedExtractiveHeader(language, noExplicitPairing: ShouldWarnNoExplicitPairing(query, hits)));

        sb.AppendLine();
        foreach (var hit in hits)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var evidenceCue = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: SourceBackedEvidenceMaxChars);
            if (string.IsNullOrWhiteSpace(evidenceCue))
                evidenceCue = CleanReadableProcedureArtifacts(FormatSourceBackedEvidenceExcerpt(hit, query, SourceBackedEvidenceMaxChars));
            if (string.IsNullOrWhiteSpace(evidenceCue))
                continue;

            sb.Append("- ");
            sb.Append(evidenceCue);
            sb.Append(" (");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.AppendLine(").");
        }

        AppendSourceBackedExtractionQualityCaveat(sb, hits, language);
        return sb.ToString().TrimEnd();
    }
}
