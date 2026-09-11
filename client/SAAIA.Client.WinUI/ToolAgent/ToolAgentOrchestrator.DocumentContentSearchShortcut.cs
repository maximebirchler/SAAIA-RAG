using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool TryExtractDocumentContentSearchTopic(string? message, out string topic)
    {
        topic = string.Empty;
        var raw = CollapseWhitespace(message ?? string.Empty);
        if (raw.Length == 0)
            return false;

        var asksForDocuments = Regex.IsMatch(
            raw,
            @"(?i)\b(?:trouve\w*|cherche\w*|liste\w*|donne\w*|montre\w*|affiche\w*|find|search|list|show|give|busc\w*|procuro|procur\w*|such\w*|zeig\w*|mostr\w*|cerc\w*)\b.{0,180}\b(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                raw,
                @"(?i)\b(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\s+(?:qui\s+|que\s+|die\s+|che\s+)?(?:parle\w*|mentionne\w*|traite\w*|contien\w*|about|regarding|concerning|habl\w*|mencion\w*|trat\w*|contien\w*|fal\w*|mencion\w*|trat\w*|contem|enth\w*|sprech\w*|erwaehn\w*|erw[a\u00e4]hn\w*|parl\w*|menzion\w*|riguard\w*)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                raw,
                @"(?i)\b(?:quels?|quelles?|which|what|qu[e\u00e9]|quais?|welche|quali)\s+(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
                RegexOptions.CultureInvariant);

        if (!asksForDocuments)
            return false;

        var asksAboutContent = Regex.IsMatch(
            raw,
            @"(?i)\b(?:parle\w*|mentionne\w*|traite\w*|contien\w*|about|regarding|concerning|habl\w*|mencion\w*|trat\w*|contien\w*|fal\w*|contem|sprech\w*|erwaehn\w*|erw[a\u00e4]hn\w*|dar[u\u00fc]ber|parl\w*|menzion\w*|riguard\w*)\b",
            RegexOptions.CultureInvariant);
        if (!asksAboutContent)
            return false;

        topic = TryExtractDocumentContentSearchTopicAnchor(raw);
        if (string.IsNullOrWhiteSpace(topic))
            topic = NormalizeRagQueryForRetrieval(raw);

        return !string.IsNullOrWhiteSpace(topic)
            && !string.Equals(topic, raw, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExactDocumentPassageLocalizationRequest(string? message)
    {
        var normalized = NormalizeLexicalLookup(message);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasLocalizationAction = Regex.IsMatch(
            normalized,
            @"\b(?:montre|montrer|localise|localiser|indique|indiquer|situe|situer|retrouve|retrouver|show|locate|find|point|indica|indicar|localiza|localizar|muestra|mostrar|mostra|mostrar|zeige|zeigen|finde|finden|individua|individuare)\b",
            RegexOptions.CultureInvariant);
        if (!hasLocalizationAction)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:ou\s+dans|where\s+in|donde\s+en|onde\s+no|wo\s+in|dove\s+nel)\b.{0,90}\b(?:documents?|fichiers?|files?|documentos?|dokumente?|documenti)\b|\b(?:passage|extrait|page|clause|section|paragraph|paragraphe|quote|citation|pasaje|extracto|pagina|clausula|secao|abschnitt|stelle|passaggio|estratto)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ShouldExpandDocumentContentSearch(string? message, JsonElement currentResult)
    {
        if (!HasRagHits(currentResult))
            return true;

        var hits = EnumerateRagHitSummaries(currentResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (hits.Count == 0)
            return true;

        var distinctDocs = CountDistinctDocumentContentSearchSources(hits);
        if (LooksLikeDocumentContentSelectionExplanationRequest(message))
            return distinctDocs < 3 || hits.Count < 5;

        return LooksLikeBroadSynthesisRequestShape(message) && distinctDocs < 3;
    }

    private static bool ShouldUseWriterForDocumentContentSearchAnswer(string? message, JsonElement currentResult)
    {
        if (!HasRagHits(currentResult))
            return false;

        if (!LooksLikeDocumentContentSelectionExplanationRequest(message))
            return false;

        var hits = EnumerateRagHitSummaries(currentResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (hits.Count == 0)
            return false;

        return CountDistinctDocumentContentSearchSources(hits) >= 1;
    }

    private static IEnumerable<RagHitSummary> EnumerateRagHitSummaries(JsonElement result)
    {
        if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.ValueKind == JsonValueKind.Object)
                yield return BuildRagHitSummary(hit);
        }
    }

    private static JsonElement FilterDocumentContentSearchResult(JsonElement result, string topic)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return result.Clone();

        var root = JsonNode.Parse(result.GetRawText()) as JsonObject;
        if (root?["hits"] is not JsonArray hits)
            return result.Clone();

        var filteredHits = new JsonArray();
        foreach (var node in hits)
        {
            if (node is not JsonObject hitNode)
                continue;

            using var hitDocument = JsonDocument.Parse(hitNode.ToJsonString());
            var hit = BuildRagHitSummary(hitDocument.RootElement);
            if (IsUsableDocumentContentSearchHit(hit, topic))
                filteredHits.Add(JsonNode.Parse(hitNode.ToJsonString()));
        }

        root["hits"] = filteredHits;
        using var filteredDocument = JsonDocument.Parse(root.ToJsonString());
        return filteredDocument.RootElement.Clone();
    }

    private static bool IsUsableDocumentContentSearchHit(RagHitSummary hit, string topic)
    {
        if (LooksLikeNavigationOnlyHit(hit) || LooksLikeLowSignalContentCandidateHit(hit))
            return false;

        var evidence = NormalizeLexicalLookup(string.Join(
            ' ',
            new[]
            {
                GetRagHitPrimaryEvidenceText(hit),
                hit.SectionTitle,
                hit.HeadingPath,
                hit.MatchedContentCards is null
                    ? string.Empty
                    : string.Join(' ', hit.MatchedContentCards.Select(static card => card.Title))
            }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var evidenceQuery = string.IsNullOrWhiteSpace(hit.RetrievalQuery)
            ? topic
            : hit.RetrievalQuery;
        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsDocumentContentSearchNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (signalTerms.Length == 0)
            return false;

        var requiredMatches = signalTerms.Length <= 2
            ? 1
            : (int)Math.Ceiling(signalTerms.Length * 0.75);
        var matchCount = signalTerms.Count(term =>
            evidence.Contains(term, StringComparison.Ordinal)
            || (term.Length >= 6
                && evidence.Contains(term[..^1], StringComparison.Ordinal)));
        return matchCount >= requiredMatches;
    }

    private static bool IsBetterDocumentContentSearchCoverage(JsonElement currentResult, JsonElement candidateResult)
    {
        if (!HasRagHits(candidateResult))
            return false;
        if (!HasRagHits(currentResult))
            return true;

        var currentHits = EnumerateRagHitSummaries(currentResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        var candidateHits = EnumerateRagHitSummaries(candidateResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();

        var currentDocs = CountDistinctDocumentContentSearchSources(currentHits);
        var candidateDocs = CountDistinctDocumentContentSearchSources(candidateHits);
        if (candidateDocs != currentDocs)
            return candidateDocs > currentDocs;

        if (candidateHits.Count != currentHits.Count)
            return candidateHits.Count > currentHits.Count;

        var currentRichness = currentHits.Sum(ComputeSourceBackedEvidenceRichnessScore);
        var candidateRichness = candidateHits.Sum(ComputeSourceBackedEvidenceRichnessScore);
        return candidateRichness > currentRichness;
    }

    private static int CountDistinctDocumentContentSearchSources(IEnumerable<RagHitSummary> hits)
        => hits
            .Select(static hit => string.IsNullOrWhiteSpace(hit.SourceHash)
                ? (string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath)
                : hit.SourceHash)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static bool LooksLikeDocumentContentSelectionExplanationRequest(string? message)
    {
        var normalized = NormalizeLexicalLookup(message);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:references?|r[eé]f[eé]rences?|referencias?|refer[eê]ncias?|referenzen?|riferimenti)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                normalized,
                @"\b(?:documents?|docs?|sources?|fichiers?|pdfs?|pages?|cite|citer|citation|citations|citar|cita|citacion|citacao|zitieren|zitat|citare|citazione)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var hasDocumentScope =
            ContainsDocumentSourceNoun(normalized)
            || Regex.IsMatch(
                normalized,
                @"\b(?:pages?|pdfs?)\b",
                RegexOptions.CultureInvariant)
            || (Regex.IsMatch(
                    normalized,
                    @"\b(?:cite|citer|citation|citations|citar|cita|citacion|citacao|zitieren|zitat|citare|citazione)\b",
                    RegexOptions.CultureInvariant)
                && Regex.IsMatch(
                    normalized,
                    @"\b(?:references?|r[eé]f[eé]rences?|referencias?|refer[eê]ncias?|referenzen?|riferimenti)\b",
                    RegexOptions.CultureInvariant));
        if (!hasDocumentScope)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:why|useful|relevant|important|best|recommend|recommendation|worth|because|reason|reasons|select|selection|prioriti[sz]e|cite|citation|reference|references|pourquoi|utile|utiles|pertinent|pertinents|importants?|recommande|recommandes|raison|raisons|choisir|selection|selectionner|prioriser|citer|citation|reference|references|porque|por\s+que|util|uteis|relevante|relevantes|importante|importantes|recomienda|recomendar|seleccion|seleccionar|priorizar|citar|cita|citacion|referencia|porque|selecionar|selecao|priorizar|citacao|referencia|warum|nutzlich|nuetzlich|relevant|wichtig|empfehl|auswahl|auswaehlen|auswahlen|priorisieren|zitieren|zitat|referenz|perche|utile|utili|rilevante|rilevanti|importante|importanti|consigli|selezione|selezionare|priorizzare|citare|citazione|riferimento)\b",
            RegexOptions.CultureInvariant);
    }

    private static string TryExtractDocumentContentSearchTopicAnchor(string raw)
    {
        var prefix = Regex.Replace(
            CollapseWhitespace(raw),
            @"(?is)[\.\?!\u00bf\u00a1]*\s*(?:quels?|quelles?|which|what|qu[e\u00e9]|quais?|welche|quali)\s+(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        if (string.IsNullOrWhiteSpace(prefix))
            prefix = raw;

        var mentionedCheckMatch = Regex.Match(
            prefix,
            @"(?i)\b(?:si|whether|se|ob)\s+(?<topic>[^?.!;]+?)\s+(?:est|sont|is|are|es|esta|est[a\u00e1]|est[a\u00e3]o|ist|sind|[e\u00e8])\s+(?:mentionn\w*|mentioned|mencion\w*|erwaehn\w*|erw[a\u00e4]hn\w*|menzion\w*)\b",
            RegexOptions.CultureInvariant);
        if (mentionedCheckMatch.Success)
        {
            var cleaned = CleanupDocumentContentSearchTopic(mentionedCheckMatch.Groups["topic"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !ContainsDocumentSourceNoun(cleaned))
                return cleaned;
        }

        var intentMatch = Regex.Match(
            prefix,
            @"(?i)\b(?:je\s+cherche|je\s+veux|j['\u2019]aimerais|i\s+(?:am\s+)?looking\s+for|i\s+need|busco|estoy\s+buscando|procuro|estou\s+a\s+procurar|ich\s+suche|cerco)\b\s*(?<topic>[^?.!\u00bf\u00a1]+)",
            RegexOptions.CultureInvariant);
        if (intentMatch.Success)
        {
            var cleaned = CleanupDocumentContentSearchTopic(intentMatch.Groups["topic"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !ContainsDocumentSourceNoun(cleaned))
                return cleaned;
        }

        var aboutMatch = Regex.Match(
            prefix,
            @"(?i)\b(?:about|regarding|concerning|sur|a\s+propos\s+de|sobre|zu|ueber|[u\u00fc]ber|su|riguardo\s+a)\s+(?<topic>[^?.!\u00bf\u00a1]+)",
            RegexOptions.CultureInvariant);
        if (aboutMatch.Success)
        {
            var cleaned = CleanupDocumentContentSearchTopic(aboutMatch.Groups["topic"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !ContainsDocumentSourceNoun(cleaned))
                return cleaned;
        }

        return string.Empty;
    }

    private static List<string> BuildDocumentContentSearchQueries(string raw, string topic)
    {
        var queries = new List<string>();
        AddDistinctQuery(queries, BuildDocumentContentSearchPrimaryQuery(topic));

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(topic))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsDocumentContentSearchNoiseTerm(term))
            .Take(6)
            .ToArray();
        if (signalTerms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', signalTerms));

        foreach (var pair in BuildDocumentContentSearchTermPairs(signalTerms).Take(10))
            AddDistinctQuery(queries, pair);

        return queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static string BuildDocumentContentSearchPrimaryQuery(string topic)
        => CollapseWhitespace(NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(topic)));

    private static string BuildDocumentContentSearchNaturalQuery(string raw, string primaryQuery)
    {
        var query = CollapseWhitespace(NormalizeLexicalLookup(raw));
        query = Regex.Replace(query, @"(?i)\b(?:about|regarding|concerning)\b", "on", RegexOptions.CultureInvariant);
        if (query.Length is < 8 or > 240)
            return string.Empty;

        var normalizedPrimary = NormalizeRagQueryForRetrieval(primaryQuery);
        if (!string.IsNullOrWhiteSpace(normalizedPrimary)
            && string.Equals(query, normalizedPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return query;
    }

    private static IEnumerable<string> BuildDocumentContentSearchTermPairs(IReadOnlyList<string> signalTerms)
    {
        if (signalTerms.Count < 2)
            yield break;

        var max = Math.Min(signalTerms.Count, 6);
        for (var i = 0; i < max; i++)
        {
            for (var j = i + 1; j < max; j++)
            {
                yield return $"{signalTerms[i]} {signalTerms[j]}";
            }
        }
    }

    private static bool IsDocumentContentSearchNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return normalized is "document" or "documents" or "source" or "sources" or "fichier" or "fichiers"
            or "file" or "files" or "documento" or "documentos" or "fuente" or "fuentes"
            or "fonte" or "fontes" or "dokument" or "dokumente" or "quelle" or "quellen"
            or "documenti" or "fonti" or "conseil" or "conseils" or "advice" or "tips"
            or "consejo" or "consejos" or "conselho" or "conselhos" or "hinweise"
            or "consiglio" or "consigli" or "cherche" or "looking" or "busco" or "procuro"
            or "suche" or "cerco";
    }

    private async Task<IReadOnlyList<string>> TryBuildTranslatedDocumentContentSearchQueriesAsync(
        string topic,
        string language,
        CancellationToken ct)
    {
        topic = CollapseWhitespace(topic);
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > 180)
            return Array.Empty<string>();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var system = """
You generate retrieval query variants for a private document search system.
Return only a JSON object with this shape: {"queries":["..."]}.
Generate concise search queries that preserve the original meaning.
Always include the original wording plus natural translations into French, English, Spanish, Portuguese, German and Italian.
Do not skip a language because the source language is already English.
Do not add explanations, categories, document names, or facts not present in the input.
Keep each query under 90 characters.
""";
            var user = JsonSerializer.Serialize(new
            {
                sourceLanguage = NormalizeLanguageCode(language),
                topic
            });
            var raw = await _llm.CompleteAsync(new[] { ("system", system), ("user", user) }, forceJson: true, timeout.Token).ConfigureAwait(false);
            return ParseDocumentContentSearchQueryVariants(raw, topic);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseDocumentContentSearchQueryVariants(string? raw, string originalTopic)
    {
        var queries = new List<string>();
        AddDistinctQuery(queries, originalTopic);
        if (string.IsNullOrWhiteSpace(raw))
            return queries;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queries", out var nested))
                root = nested;

            if (root.ValueKind != JsonValueKind.Array)
                return queries;

            foreach (var item in root.EnumerateArray())
            {
                AddDocumentContentSearchQueryVariant(queries, item);
            }
        }
        catch
        {
            return queries;
        }

        return queries.Take(12).ToArray();
    }

    private static void AddDocumentContentSearchQueryVariant(List<string> queries, JsonElement item)
    {
        switch (item.ValueKind)
        {
            case JsonValueKind.String:
            {
                var query = CollapseWhitespace(item.GetString() ?? string.Empty);
                if (query.Length is >= 3 and <= 120)
                    AddDistinctQuery(queries, query);
                break;
            }
            case JsonValueKind.Object:
            {
                foreach (var property in item.EnumerateObject())
                    AddDocumentContentSearchQueryVariant(queries, property.Value);
                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var nested in item.EnumerateArray())
                    AddDocumentContentSearchQueryVariant(queries, nested);
                break;
            }
        }
    }

    private static bool ContainsDocumentSourceNoun(string value)
        => Regex.IsMatch(
            value ?? string.Empty,
            @"(?i)\b(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
            RegexOptions.CultureInvariant);

    private static string CleanupDocumentContentSearchTopic(string value)
    {
        var topic = CleanupStandaloneTopic(value);
        topic = Regex.Replace(
            topic,
            @"(?i)^(?:des?\s+|les?\s+|the\s+|some\s+|unos?\s+|unas?\s+|os\s+|as\s+|uma?\s+|ein(?:e|en|em|er|es)?\s+|gli\s+|le\s+|i\s+)?(?:conseils?|advice|tips?|consejos?|conselhos?|hinweise|consigli|informazioni|infos?)\s*(?:avec|sur|about|regarding|concerning|sobre|zu|ueber|[u\u00fc]ber|su|riguardo\s+a)?\s*",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        topic = Regex.Replace(
            topic,
            @"(?i)\b(?:cela|ceci|this|that|eso|esto|isso|isto|dar[u\u00fc]ber|ne)\s*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim(' ', '.', '?', '!', ':', ';', ',', '"', '\'');
        return topic;
    }

    private static string BuildDocumentContentSearchAnswer(ToolResults toolResults, string topic, string language)
    {
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => IsUsableDocumentContentSearchHit(hit, topic))
            .ToList();
        if (hits.Count == 0)
        {
            return NormalizeLanguageCode(language) switch
            {
                "en" => $"I did not find any indexed document content about {topic}.",
                "es" => $"No he encontrado contenido indexado sobre {topic}.",
                "pt" => $"Nao encontrei conteudo indexado sobre {topic}.",
                "de" => $"Ich habe keine indexierten Dokumentinhalte zu {topic} gefunden.",
                "it" => $"Non ho trovato contenuti indicizzati su {topic}.",
                _ => $"Je n'ai trouvé aucun contenu indexé sur {topic}."
            };
        }

        var docs = hits
            .GroupBy(h => string.IsNullOrWhiteSpace(h.DocPath) ? h.DocName : h.DocPath, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var pages = g
                    .Select(h => h.PageStart)
                    .Where(p => p > 0)
                    .Distinct()
                    .OrderBy(p => p)
                    .Take(5)
                    .ToList();
                return new
                {
                    Label = string.IsNullOrWhiteSpace(first.DocName) ? Path.GetFileName(first.DocPath) : first.DocName,
                    Pages = pages,
                    Reason = BuildDocumentContentSearchDocReason(g, language)
                };
            })
            .Take(8)
            .ToList();

        var header = NormalizeLanguageCode(language) switch
        {
            "en" => $"I found {docs.Count} document(s) with indexed content about {topic}:",
            "es" => $"He encontrado {docs.Count} documento(s) con contenido indexado sobre {topic}:",
            "pt" => $"Encontrei {docs.Count} documento(s) com conteudo indexado sobre {topic}:",
            "de" => $"Ich habe {docs.Count} Dokument(e) mit indexiertem Inhalt zu {topic} gefunden:",
            "it" => $"Ho trovato {docs.Count} documento/i con contenuti indicizzati su {topic}:",
            _ => $"J'ai trouvé {docs.Count} document(s) avec du contenu indexé sur {topic} :"
        };

        var lines = docs.Select(d =>
        {
            var pages = d.Pages.Count == 0
                ? string.Empty
                : $" ({SourceBackedPagePrefix(language)}{string.Join(", ", d.Pages)})";
            var reason = string.IsNullOrWhiteSpace(d.Reason)
                ? string.Empty
                : $" - {d.Reason}";
            return $"- {d.Label}{pages}{reason}";
        });

        return header + "\n" + string.Join("\n", lines);
    }

    private static string BuildDocumentContentSearchDocReason(IEnumerable<RagHitSummary> hits, string language)
    {
        var hit = hits
            .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
            .ThenByDescending(static h => h.Score)
            .FirstOrDefault();
        if (hit is null)
            return string.Empty;

        var descriptor = hit.MatchedContentCards?
            .Select(static card => CleanSourceBackedOptionTitle(card.Title))
            .FirstOrDefault(static title =>
                !LooksLikeWeakSourceBackedOptionTitle(title)
                && !LooksLikeCopyrightNotice(title));

        if (string.IsNullOrWhiteSpace(descriptor))
        {
            descriptor = new[] { hit.SectionTitle, hit.HeadingPath }
                .Select(static value => CleanSourceBackedOptionTitle(value))
                .FirstOrDefault(static title =>
                    !LooksLikeWeakSourceBackedOptionTitle(title)
                    && !LooksLikeCopyrightNotice(title));
        }

        if (string.IsNullOrWhiteSpace(descriptor))
            return string.Empty;

        return NormalizeLanguageCode(language) switch
        {
            "en" => $"matched section: {descriptor}",
            "es" => $"seccion encontrada: {descriptor}",
            "pt" => $"secao encontrada: {descriptor}",
            "de" => $"gefundener Abschnitt: {descriptor}",
            "it" => $"sezione trovata: {descriptor}",
            _ => $"section trouvée : {descriptor}"
        };
    }

    private static bool LooksLikeCopyrightNotice(string? value)
        => Regex.IsMatch(
            NormalizeLexicalLookup(value),
            @"\b(?:all\s+rights\s+reserved|copyright|tous\s+droits\s+reserves|todos\s+los\s+derechos\s+reservados|alle\s+rechte\s+vorbehalten|tutti\s+i\s+diritti\s+riservati)\b",
            RegexOptions.CultureInvariant);
}
