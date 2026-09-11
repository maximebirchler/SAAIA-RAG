using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedOptionAnswer(ToolResults toolResults, string language, int minItems = 1, string? query = null)
    {
        if (IsBroadenedSourceSearchConfirmationEnvelope(query))
            return string.Empty;

        language = NormalizeLanguageCode(language);
        var missingPairingAnchor = TryBuildMissingPairingAnchorAnswer(toolResults, query ?? string.Empty, language);

        var selection = SelectSourceBackedOptionAnswerCandidates(toolResults, query, minItems, language);
        var optionItems = selection.Items.ToList();
        var requestedMaxMinutes = selection.RequestedMaxMinutes;
        var wantsTotalPairing = selection.WantsTotalPairing;

        if (!ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(toolResults, query)
            && !string.IsNullOrWhiteSpace(missingPairingAnchor)
            && optionItems.Count == 0)
        {
            return missingPairingAnchor;
        }

        if (optionItems.Count == 0 || optionItems.Count < minItems)
            return string.Empty;

        var header = language switch
        {
            "en" => "Here are source-backed options found in the available documents:",
            "es" => "Aqui tienes opciones basadas en los documentos disponibles:",
            "pt" => "Aqui estao opcoes baseadas nos documentos disponiveis:",
            "de" => "Hier sind quellenbasierte Optionen aus den verfuegbaren Dokumenten:",
            "it" => "Ecco opzioni basate sui documenti disponibili:",
            _ => "Voici des options appuyees sur les documents disponibles :"
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        var pairingLeadCaveat = BuildPairingLeadCaveat(query ?? string.Empty, language);
        if (!string.IsNullOrWhiteSpace(missingPairingAnchor) && !string.IsNullOrWhiteSpace(pairingLeadCaveat))
        {
            sb.AppendLine(pairingLeadCaveat);
        }
        else if (ShouldWarnNoExplicitPairing(query ?? string.Empty, optionItems.Select(static item => item.Hit).ToList()))
        {
            var pairingCaveat = language switch
            {
                "en" => "I did not find a passage that explicitly connects every part of the request, so I list these as documented options to verify, not as certified compatible recommendations.",
                "es" => "No he encontrado un pasaje que conecte explicitamente todas las partes de la solicitud; las enumero como pistas con fuente, no como recomendaciones compatibles certificadas.",
                "pt" => "Nao encontrei uma passagem que ligue explicitamente todas as partes do pedido; listo-as como pistas com fonte, nao como recomendacoes compativeis certificadas.",
                "de" => "Ich habe keine Stelle gefunden, die alle Teile der Anfrage ausdruecklich verbindet; ich liste sie daher als belegte Hinweise, nicht als bestaetigte kompatible Empfehlungen.",
                "it" => "Non ho trovato un passaggio che colleghi esplicitamente tutte le parti della richiesta; le elenco quindi come indicazioni con fonte, non come raccomandazioni compatibili certificate.",
                _ => "Je n'ai pas trouvÃ© de passage qui relie explicitement tous les Ã©lÃ©ments de la demande ; je liste donc ces Ã©lÃ©ments documentÃ©s Ã  vÃ©rifier, pas comme recommandations compatibles certifiÃ©es."
            };
            sb.AppendLine(AppendBroadenedSearchOfferIfHelpful(pairingCaveat, query, language));
        }

        if (wantsTotalPairing)
        {
            var requestedMaxMinutesValue = requestedMaxMinutes.GetValueOrDefault();
            var visibleCandidateTotal = !selection.HasCertifiedTotalPair
                && optionItems.Count >= 2
                && optionItems.All(static candidate => candidate.VisibleMinutes.HasValue)
                    ? optionItems.Sum(static candidate => candidate.VisibleMinutes!.Value)
                    : (int?)null;
            var pairingNote = (selection.HasCertifiedTotalPair, language) switch
            {
                (true, "en") => $"The visible durations fit the constraint: {selection.VisibleTotalMinutes} minutes total, within the requested {requestedMaxMinutesValue} minutes.",
                (true, "es") => $"Las duraciones visibles cumplen la restriccion: {selection.VisibleTotalMinutes} minutos en total, dentro de los {requestedMaxMinutesValue} minutos pedidos.",
                (true, "pt") => $"As duracoes visiveis respeitam a restricao: {selection.VisibleTotalMinutes} minutos no total, dentro dos {requestedMaxMinutesValue} minutos pedidos.",
                (true, "de") => $"Die sichtbaren Dauern erfuellen die Vorgabe: insgesamt {selection.VisibleTotalMinutes} Minuten, innerhalb der gewuenschten {requestedMaxMinutesValue} Minuten.",
                (true, "it") => $"Le durate visibili rispettano il vincolo: {selection.VisibleTotalMinutes} minuti totali, entro i {requestedMaxMinutesValue} minuti richiesti.",
                (true, _) => $"Les durees visibles respectent la contrainte : {selection.VisibleTotalMinutes} minutes au total, dans la limite demandee de {requestedMaxMinutesValue} minutes.",
                (false, "en") when visibleCandidateTotal.HasValue => $"I cannot certify the requested combined total under {requestedMaxMinutesValue} minutes: the visible total for the retained items is {visibleCandidateTotal.Value} minutes. I list them as documented options to verify, not as a compatible set.",
                (false, "es") when visibleCandidateTotal.HasValue => $"No puedo certificar el total combinado pedido en menos de {requestedMaxMinutesValue} minutos: el total visible de los elementos retenidos es {visibleCandidateTotal.Value} minutos. Los enumero como pistas con fuente, no como conjunto compatible.",
                (false, "pt") when visibleCandidateTotal.HasValue => $"Nao posso certificar o total combinado pedido em menos de {requestedMaxMinutesValue} minutos: o total visivel dos itens retidos e {visibleCandidateTotal.Value} minutos. Listo-os como pistas com fonte, nao como conjunto compativel.",
                (false, "de") when visibleCandidateTotal.HasValue => $"Ich kann die angefragte kombinierte Summe unter {requestedMaxMinutesValue} Minuten nicht bestaetigen: die sichtbare Summe der behaltenen Eintraege betraegt {visibleCandidateTotal.Value} Minuten. Ich liste sie als belegte Hinweise, nicht als kompatibles Set.",
                (false, "it") when visibleCandidateTotal.HasValue => $"Non posso certificare il totale combinato richiesto sotto {requestedMaxMinutesValue} minuti: il totale visibile degli elementi mantenuti e {visibleCandidateTotal.Value} minuti. Li elenco come piste con fonte, non come insieme compatibile.",
                (false, _) when visibleCandidateTotal.HasValue => $"Je ne peux pas certifier le total combine demande en moins de {requestedMaxMinutesValue} minutes : le total visible des elements retenus est de {visibleCandidateTotal.Value} minutes. Je les liste comme elements documentes a verifier, pas comme ensemble compatible.",
                (false, "en") => $"I do not have enough complete visible durations to guarantee the requested combined total under {requestedMaxMinutesValue} minutes; I only list source-backed components.",
                (false, "es") => $"No tengo suficientes duraciones completas visibles para garantizar el total combinado pedido en menos de {requestedMaxMinutesValue} minutos; solo enumero componentes con fuente.",
                (false, "pt") => $"Nao tenho duracoes completas visiveis suficientes para garantir o total combinado pedido em menos de {requestedMaxMinutesValue} minutos; listo apenas componentes com fonte.",
                (false, "de") => $"Ich habe nicht genug vollstaendige sichtbare Dauern, um die angefragte kombinierte Summe unter {requestedMaxMinutesValue} Minuten zu garantieren; ich liste nur belegte Bestandteile.",
                (false, "it") => $"Non ho durate complete visibili sufficienti per garantire il totale combinato richiesto sotto {requestedMaxMinutesValue} minuti; elenco solo componenti con fonte.",
                _ => $"Je n'ai pas assez de durees completes visibles pour garantir le total combine demande en moins de {requestedMaxMinutesValue} minutes ; je liste seulement des composants sources."
            };
            sb.AppendLine(pairingNote);
        }

        for (var i = 0; i < optionItems.Count; i++)
        {
            var hit = optionItems[i].Hit;
            var optionLabel = language switch
            {
                "es" => $"Opcion {i + 1}",
                "pt" => $"Opcao {i + 1}",
                "it" => $"Opzione {i + 1}",
                _ => $"Option {i + 1}"
            };

            sb.Append("- ");
            sb.Append(optionLabel);
            sb.Append(" : ");
            sb.Append(optionItems[i].Title);
            var visibleMinutes = optionItems[i].VisibleMinutes;
            if (visibleMinutes.HasValue)
            {
                sb.Append(" - ");
                sb.Append(visibleMinutes.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(SourceBackedVisibleMinutesSuffix(language));
            }
            sb.Append(" (");
            sb.Append(string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(")");

            var evidence = ShouldRenderRawSourceBackedOptionEvidence(query)
                ? FormatSourceBackedOptionEvidence(hit)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                sb.Append(" - ");
                sb.Append(evidence);
            }

            sb.AppendLine();
        }

        var note = language switch
        {
            "en" => "I keep quantities, timing, and substitutions tied to the source pages.",
            "es" => "Mantengo cantidades, tiempos y sustituciones ligados a las paginas fuente.",
            "pt" => "Mantenho quantidades, tempos e substituicoes ligados as paginas fonte.",
            "de" => "Mengen, Zeiten und Ersetzungen bleiben an die Quellseiten gebunden.",
            "it" => "Tengo quantita, tempi e sostituzioni legati alle pagine fonte.",
            _ => "Je garde les quantitÃ©s, temps et substitutions rattachÃ©s aux pages source."
        };
        sb.AppendLine(note);

        return sb.ToString().TrimEnd();
    }

    private static bool ShouldRenderRawSourceBackedOptionEvidence(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return !IsBroadenedSourceSearchConfirmationEnvelope(query)
            && !LooksLikeGenericCollectionOrListRequest(query)
            && !LooksLikeAnyDocumentaryPlanningRequest(query)
            && !LooksLikeBroadSynthesisRequestShape(query)
            && !LooksLikeBroadSourceBackedCompositionRequest(query)
            && !LooksLikeMultipleCandidateSynthesisRequest(query)
            && !LooksLikeSoftChoiceRecommendationRequest(query)
            && !LooksLikeSourceBackedPairingRecommendationRequest(query);
    }

    private static SourceBackedOptionAnswerSelection SelectSourceBackedOptionAnswerCandidates(
        ToolResults toolResults,
        string? query,
        int minItems,
        string language = "")
    {
        var allOptionItems = SelectSourceBackedOptionCandidates(toolResults, query, keepOverRequestedDuration: true, language: language)
            .ToList();
        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        var wantsTotalPairing = LooksLikeTotalDurationConstraintRequest(query, requestedMaxMinutes);

        if (!wantsTotalPairing)
        {
            return new SourceBackedOptionAnswerSelection(
                allOptionItems.Take(5).ToList(),
                WantsTotalPairing: false,
                HasCertifiedTotalPair: false,
                RequestedMaxMinutes: requestedMaxMinutes,
                VisibleTotalMinutes: null);
        }

        var timedItems = allOptionItems
            .Where(candidate => candidate.VisibleMinutes.HasValue)
            .Where(candidate => candidate.VisibleMinutes!.Value <= requestedMaxMinutes!.Value)
            .ToList();
        var requiredItems = wantsTotalPairing ? Math.Max(2, minItems) : Math.Max(1, minItems);
        var combined = SelectSourceBackedCombinedDurationSet(timedItems, requiredItems, requestedMaxMinutes!.Value);
        if (combined.Count >= requiredItems)
        {
            return new SourceBackedOptionAnswerSelection(
                combined,
                WantsTotalPairing: true,
                HasCertifiedTotalPair: true,
                RequestedMaxMinutes: requestedMaxMinutes,
                VisibleTotalMinutes: combined.Sum(static candidate => candidate.VisibleMinutes!.Value));
        }

        var fallbackSet = allOptionItems.Take(requiredItems).ToList();
        if (fallbackSet.Count >= requiredItems)
        {
            return new SourceBackedOptionAnswerSelection(
                fallbackSet,
                WantsTotalPairing: true,
                HasCertifiedTotalPair: false,
                RequestedMaxMinutes: requestedMaxMinutes,
                VisibleTotalMinutes: null);
        }

        var fallbackItems = timedItems.Count >= requiredItems
            ? timedItems.Take(5).ToList()
            : allOptionItems.Take(5).ToList();
        return new SourceBackedOptionAnswerSelection(
            fallbackItems,
            WantsTotalPairing: true,
            HasCertifiedTotalPair: false,
            RequestedMaxMinutes: requestedMaxMinutes,
            VisibleTotalMinutes: null);
    }

    private static bool LooksLikeTotalDurationConstraintRequest(string? query, int? requestedMaxMinutes)
    {
        if (!requestedMaxMinutes.HasValue || string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:total|combined|combine|combinee|combin[eÃ©]e|ensemble|overall|cumul|cumulative|cumule|cumul[eÃ©])\b",
            RegexOptions.CultureInvariant)
            || (LooksLikeSourceBackedOptionRequest(query)
                && Regex.IsMatch(normalized, @"(?:\+|\b(?:et|and)\b)", RegexOptions.CultureInvariant));
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedCombinedDurationSet(
        IReadOnlyList<SourceBackedOptionCandidate> candidates,
        int minItems,
        int requestedMaxMinutes)
    {
        var selected = new List<SourceBackedOptionCandidate>();
        var total = 0;
        foreach (var candidate in candidates
            .Where(static candidate => candidate.VisibleMinutes.HasValue)
            .OrderByDescending(static candidate => candidate.Score))
        {
            if (total + candidate.VisibleMinutes!.Value > requestedMaxMinutes)
                continue;
            selected.Add(candidate);
            total += candidate.VisibleMinutes.Value;
            if (selected.Count >= minItems)
                break;
        }

        return selected.Count >= minItems ? selected : Array.Empty<SourceBackedOptionCandidate>();
    }
}
