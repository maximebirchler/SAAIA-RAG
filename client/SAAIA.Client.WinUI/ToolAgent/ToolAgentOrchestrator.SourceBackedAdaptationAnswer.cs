using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildSourceBackedAdaptationAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var selected = RankSourceBackedAdaptationHits(hits, query)
            .Take(5)
            .ToList();
        if (selected.Count == 0)
            return string.Empty;

        var labels = BuildSourceBackedAdaptationLabels(language);
        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.SourceSection);
        foreach (var hit in selected.Take(4))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: SourceBackedEvidenceMaxChars);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        var targetFacts = ExtractSourceBackedAdaptationTargetFacts(selected, query, maxFacts: 5, language);
        if (targetFacts.Count > 0)
        {
            sb.AppendLine(SourceBackedLabel(
                language,
                "Cibles visibles a adapter :",
                "Visible targets to adapt:",
                "Objetivos visibles que adaptar:",
                "Alvos visiveis a adaptar:",
                "Sichtbare anzupassende Ziele:",
                "Obiettivi visibili da adattare:"));
            foreach (var fact in targetFacts)
            {
                sb.Append("- ");
                sb.AppendLine(fact);
            }
        }

        sb.AppendLine(labels.AdaptationSection);
        foreach (var note in BuildSourceBackedAdaptationNotes(language, query))
        {
            sb.Append("- ");
            sb.AppendLine(note);
        }

        sb.Append(labels.Caution);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<RagHitSummary> RankSourceBackedAdaptationHits(IEnumerable<RagHitSummary> hits, string query)
    {
        var focusGroups = BuildSourceBackedAdaptationFocusGroups(query);
        var focusQuery = string.Join(' ', focusGroups.SelectMany(static group => group).Distinct(StringComparer.Ordinal));
        var scored = hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select(hit =>
            {
                var primaryText = GetRagHitPrimaryEvidenceText(hit);
                var lookupText = GetRagHitLookupText(hit);
                var primaryMatches = CountMatchedFocusGroups(focusGroups, primaryText);
                var lookupMatches = CountMatchedFocusGroups(focusGroups, lookupText);
                return new
                {
                    Hit = hit,
                    MatchedFocusGroups = primaryMatches,
                    LookupMatchedFocusGroups = lookupMatches,
                    TargetFactScore = CountSourceBackedAdaptationTargetFacts(hit, query),
                    PrimaryRelevance = ComputeRagHitLexicalRelevance(focusQuery, primaryText),
                    Relevance = ComputeRagHitLexicalRelevance(focusQuery, lookupText),
                    IsIntroLead = LooksLikeIntroLeadInHit(hit)
                };
            })
            .ToList();

        if (scored.Count == 0)
            return Array.Empty<RagHitSummary>();

        var bestMatchCount = scored.Max(static item => item.MatchedFocusGroups);
        if (bestMatchCount >= 2)
        {
            scored = scored
                .Where(item => item.MatchedFocusGroups >= 2)
                .ToList();
        }

        var bestTargetFactScore = scored.Max(static item => item.TargetFactScore);
        if (bestTargetFactScore > 0)
        {
            scored = scored
                .Where(static item => item.TargetFactScore > 0)
                .ToList();
        }

        var nonIntroScored = scored
            .Where(static item => !item.IsIntroLead)
            .ToList();
        if (nonIntroScored.Count > 0)
            scored = nonIntroScored;

        return scored
            .OrderByDescending(static item => item.MatchedFocusGroups)
            .ThenByDescending(static item => item.TargetFactScore)
            .ThenByDescending(static item => item.LookupMatchedFocusGroups)
            .ThenByDescending(static item => item.PrimaryRelevance)
            .ThenByDescending(static item => item.Relevance)
            .ThenByDescending(static item => item.Hit.Score)
            .Select(static item => item.Hit)
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildSourceBackedAdaptationFocusGroups(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<IReadOnlyList<string>>();

        return ExtractQuerySignalTerms(normalized)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !Regex.IsMatch(
                term,
                @"\b(?:alleger|all[e\u00e9]ger|lighten|reduce|reduire|adapter|adaptation|adapte|changer|modifier|change|modify|prudente|prudent)\b",
                RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .Select(static term => (IReadOnlyList<string>)BuildRetrievalTermVariants(term)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .Where(static group => group.Count > 0)
            .ToArray();
    }

    private static int CountMatchedFocusGroups(IReadOnlyList<IReadOnlyList<string>> focusGroups, string? text)
    {
        if (focusGroups.Count == 0 || string.IsNullOrWhiteSpace(text))
            return 0;

        var normalizedText = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        return focusGroups.Count(group => group.Any(term =>
            term.Length >= 3 && normalizedText.Contains(term, StringComparison.Ordinal)));
    }

    private static (string Header, string SourceSection, string AdaptationSection, string Caution) BuildSourceBackedAdaptationLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "I separate what is directly supported by the documents from the cautious adaptation.",
                "From the documents:",
                "Cautious adaptation:",
                "I do not invent exact quantities or steps that are not visible in the cited excerpts."),
            "es" => (
                "Separo lo que esta directamente respaldado por los documentos de la adaptacion prudente.",
                "Lo que viene de los documentos:",
                "Adaptacion prudente:",
                "No invento cantidades ni pasos exactos que no aparezcan en los extractos citados."),
            "pt" => (
                "Separo o que esta diretamente apoiado pelos documentos da adaptacao prudente.",
                "O que vem dos documentos:",
                "Adaptacao prudente:",
                "Nao invento quantidades nem passos exatos que nao estejam visiveis nos excertos citados."),
            "de" => (
                "Ich trenne, was direkt aus den Dokumenten belegt ist, von der vorsichtigen Anpassung.",
                "Aus den Dokumenten:",
                "Vorsichtige Anpassung:",
                "Ich erfinde keine exakten Mengen oder Schritte, die in den zitierten Auszuegen nicht sichtbar sind."),
            "it" => (
                "Separo cio che e direttamente supportato dai documenti dall'adattamento prudente.",
                "Dai documenti:",
                "Adattamento prudente:",
                "Non invento quantita o passaggi esatti che non sono visibili negli estratti citati."),
            _ => (
                "Je sÃ©pare ce qui est directement appuyÃ© par les documents de l'adaptation prudente.",
                "Ce qui vient des documents :",
                "Adaptation prudente :",
                "Je n'invente pas de quantitÃ©s ni d'Ã©tapes exactes absentes des passages citÃ©s.")
        };
    }

    private static IReadOnlyList<string> BuildSourceBackedAdaptationNotes(string language, string query)
    {
        var objective = ExtractSourceBackedAdaptationObjective(query);
        var focusTerms = ExtractSourceBackedAdaptationFocusTerms(query);
        var focusSuffix = focusTerms.Count == 0 ? string.Empty : string.Join(", ", focusTerms);
        return NormalizeLanguageCode(language) switch
        {
            "en" => new[]
            {
                $"Use the cited excerpts as the base; the adaptation target is: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Prioritize the passages where the requested target appears explicitly." : $"Prioritize the passages where these requested targets appear explicitly: {focusSuffix}.",
                "Apply the requested change only to elements directly concerned by the question, then validate the result step by step.",
                "For a final operational version, choose one cited source page so the adapted answer can keep every source-backed constraint visible."
            },
            "es" => new[]
            {
                $"Usa los extractos citados como base; el objetivo de adaptacion es: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Prioriza los pasajes donde el objetivo solicitado aparece explicitamente." : $"Prioriza los pasajes donde estos objetivos solicitados aparecen explicitamente: {focusSuffix}.",
                "Aplica el cambio pedido solo a los elementos directamente afectados por la pregunta y valida el resultado paso a paso.",
                "Para una version operativa final, elige una pagina fuente citada para mantener visibles todas las restricciones documentadas."
            },
            "pt" => new[]
            {
                $"Usa os excertos citados como base; o objetivo da adaptacao e: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Da prioridade aos excertos onde o alvo pedido aparece explicitamente." : $"Da prioridade aos excertos onde estes alvos pedidos aparecem explicitamente: {focusSuffix}.",
                "Aplica a alteracao pedida apenas aos elementos diretamente ligados a pergunta e valida o resultado passo a passo.",
                "Para uma versao operacional final, escolhe uma pagina fonte citada para manter visiveis todas as restricoes documentadas."
            },
            "de" => new[]
            {
                $"Nutze die zitierten Auszuege als Grundlage; das Anpassungsziel ist: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Bevorzuge die Stellen, in denen das angefragte Ziel ausdruecklich vorkommt." : $"Bevorzuge die Stellen, in denen diese angefragten Ziele ausdruecklich vorkommen: {focusSuffix}.",
                "Wende die angefragte Aenderung nur auf Elemente an, die direkt von der Frage betroffen sind, und pruefe das Ergebnis schrittweise.",
                "Fuer eine endgueltige Arbeitsversion waehle eine zitierte Quellseite, damit alle belegten Einschraenkungen sichtbar bleiben."
            },
            "it" => new[]
            {
                $"Usa gli estratti citati come base; l'obiettivo di adattamento e: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Dai priorita ai passaggi in cui l'obiettivo richiesto appare esplicitamente." : $"Dai priorita ai passaggi in cui questi obiettivi richiesti appaiono esplicitamente: {focusSuffix}.",
                "Applica il cambiamento richiesto solo agli elementi direttamente coinvolti dalla domanda e valida il risultato passo passo.",
                "Per una versione operativa finale, scegli una pagina fonte citata cosi da mantenere visibili tutti i vincoli documentati."
            },
            _ => new[]
            {
                $"Prendre les extraits cites comme base ; l'objectif d'adaptation est : {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Prioriser les passages ou la cible demandee apparait explicitement." : $"Prioriser les passages ou ces cibles demandees apparaissent explicitement : {focusSuffix}.",
                "Isoler les quantitÃ©s ou contraintes visibles liÃ©es Ã  la cible, puis modifier seulement cette partie en gardant les autres contraintes sourcÃ©es inchangÃ©es.",
                "Faire l'adaptation par petits paliers et verifier le resultat : si la cible participe a une contrainte fonctionnelle, reglementaire, de securite ou de performance, la validation devient obligatoire.",
                "Pour une version finale operationnelle, choisir une page source citee afin de garder visibles toutes les contraintes documentees."
            }
        };
    }

    private static IReadOnlyList<string> ExtractSourceBackedAdaptationTargetFacts(IReadOnlyList<RagHitSummary> hits, string query, int maxFacts, string language)
    {
        var targetGroups = BuildSourceBackedAdaptationTargetGroups(query);
        if (targetGroups.Count == 0)
            targetGroups = BuildSourceBackedAdaptationFocusGroups(query);
        if (targetGroups.Count == 0)
            return Array.Empty<string>();

        var facts = new List<string>();
        foreach (var hit in hits)
        {
            var segments = ExtractAdaptationEvidenceSegments(hit, targetGroups)
                .Take(3)
                .ToList();
            if (segments.Count == 0)
                continue;

            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            facts.Add($"{docLabel} {SourceBackedPagePrefix(language)}{hit.PageStart} : {string.Join("; ", segments)}");
            if (facts.Count >= maxFacts)
                break;
        }

        return facts;
    }

    private static int CountSourceBackedAdaptationTargetFacts(RagHitSummary hit, string query)
    {
        var targetGroups = BuildSourceBackedAdaptationTargetGroups(query);
        if (targetGroups.Count == 0)
            targetGroups = BuildSourceBackedAdaptationFocusGroups(query);
        if (targetGroups.Count == 0)
            return 0;

        return ExtractAdaptationEvidenceSegments(hit, targetGroups).Take(4).Count();
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildSourceBackedAdaptationTargetGroups(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<IReadOnlyList<string>>();

        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:moins\s+de|less|reduce(?:d)?|reduire|reduit|diminuer|diminution|alleger|all[e\u00e9]ger|lighten|sans|without|en|in|de|du|des)\s+(?:la\s+|le\s+|les\s+|l\s+|d\s+|the\s+)?(?<target>[\p{L}][\p{L}\p{N}_-]{2,30})",
            RegexOptions.CultureInvariant))
        {
            var term = match.Groups["target"].Value;
            if (!IsSourceBackedActionRetrievalNoiseTerm(term))
                terms.Add(term);
        }

        return terms
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 3)
            .Where(static term => !Regex.IsMatch(term, @"\b(?:document|documents|pdf|source|sources|adaptation|prudente|base)\b", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .Select(static term => (IReadOnlyList<string>)BuildRetrievalTermVariants(term)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .Where(static group => group.Count > 0)
            .ToArray();
    }

    private static IEnumerable<string> ExtractAdaptationEvidenceSegments(RagHitSummary hit, IReadOnlyList<IReadOnlyList<string>> targetGroups)
    {
        var text = CollapseWhitespace(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var previousWasItemizedLabel = false;
        foreach (var segment in Regex.Split(text, @"(?:[â€¢\n\r]|(?<=[.;:])\s+)"))
        {
            var value = CollapseWhitespace(segment).Trim(' ', '-', ':', ';', ',');
            if (value.Length < 5)
                continue;
            if (value.Length > 140)
                value = value[..140].TrimEnd() + "...";

            var normalizedSegment = NormalizeLexicalLookup(value);
            var carriesItemizedContext = previousWasItemizedLabel;
            previousWasItemizedLabel = Regex.IsMatch(
                normalizedSegment,
                @"^\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|materials?|materiel|mat[eÃ©]riel|components?|composants?)\b$",
                RegexOptions.CultureInvariant);
            if (!targetGroups.Any(group => group.Any(term => term.Length >= 3 && normalizedSegment.Contains(term, StringComparison.Ordinal))))
                continue;

            var hasSpecificSignal = Regex.IsMatch(
                normalizedSegment,
                @"\b\d+(?:[,.]\d+)?\s*(?:%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12}|min|h)\b|\b(?:quantite|quantites|amount|quantity|constraint|contrainte)\b",
                RegexOptions.CultureInvariant);
            if (!hasSpecificSignal
                && (carriesItemizedContext || LooksLikeDelimitedTargetFact(value))
                && value.Contains(',', StringComparison.Ordinal))
            {
                hasSpecificSignal = true;
            }
            if (!hasSpecificSignal)
                continue;

            yield return value;
        }
    }

    private static string ExtractSourceBackedAdaptationObjective(string query)
    {
        var objective = CollapseWhitespace(query);
        objective = Regex.Replace(
            objective,
            @"(?i)\b(?:dis\s+bien|separe|separer|distingue|distinguer|indique|indiquer|precise|preciser|tell|separate|distinguish|show|state)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        objective = objective.Trim(' ', '.', '?', '!', ':', ';', ',');
        if (objective.Length == 0)
            objective = CollapseWhitespace(query);

        return objective.Length <= 140
            ? objective
            : objective[..140].TrimEnd() + "...";
    }

    private static IReadOnlyList<string> ExtractSourceBackedAdaptationFocusTerms(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return ExtractQuerySignalTerms(normalized)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !Regex.IsMatch(
                term,
                @"\b(?:alleger|all[e\u00e9]ger|lighten|reduce|reduire|adapter|adaptation|adapte|changer|modifier|change|modify|prudente|prudent)\b",
                RegexOptions.CultureInvariant))
            .SelectMany(BuildRetrievalTermVariants)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }
}
