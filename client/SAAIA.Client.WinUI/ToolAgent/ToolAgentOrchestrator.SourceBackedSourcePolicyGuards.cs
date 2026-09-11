using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string TryBuildSourcePolicyGuardAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeSourceBypassOrUnsupportedInventionRequest(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return string.Empty;
        }

        language = NormalizeLanguageCode(language);
        if (LooksLikeDocumentVersionYearOmissionRequest(query)
            && !LooksLikeDocumentInstructionPolicyRequest(query))
        {
            return string.Empty;
        }

        if ((LooksLikeDocumentVersionTraceabilityRequest(query)
             || LooksLikeDocumentVersionYearOmissionRequest(query))
            && !LooksLikeDocumentInstructionPolicyRequest(query)
            && !LooksLikeHardSourceBypassOrUnsupportedInventionRequest(query))
        {
            return string.Empty;
        }

        if (LooksLikeDocumentInstructionPolicyRequest(query))
            return BuildDocumentInstructionPolicyAnswer(language);

        var prefix = BuildSourcePolicyGuardPrefix(language);
        var sourceList = BuildSourcePolicyGuardSourceList(toolResults, language);

        return string.IsNullOrWhiteSpace(sourceList)
            ? prefix
            : $"{prefix}{Environment.NewLine}{Environment.NewLine}{sourceList}";
    }

    private static string BuildSourcePolicyGuardPrefix(string language)
        => SourceBackedLabel(
            language,
            "Je ne peux pas ignorer les sources ni inventer une rÃ©ponse documentaire. Je reste donc strictement sur ce que les sources disponibles permettent.",
            "I cannot ignore the sources or invent a documentary answer. I will stay strictly within what the available sources support.",
            "No puedo ignorar las fuentes ni inventar una respuesta documental. Me limito estrictamente a lo que permiten las fuentes disponibles.",
            "Nao posso ignorar as fontes nem inventar uma resposta documental. Vou limitar-me estritamente ao que as fontes disponiveis sustentam.",
            "Ich kann die Quellen nicht ignorieren und keine dokumentarische Antwort erfinden. Ich bleibe daher strikt bei dem, was die verfuegbaren Quellen belegen.",
            "Non posso ignorare le fonti ne inventare una risposta documentale. Mi limito quindi strettamente a cio che le fonti disponibili supportano.");

    private static string BuildSourcePolicyGuardSourceList(ToolResults toolResults, string language)
    {
        var sources = DeriveSourcesFromRagHits(toolResults).Take(5).ToList();
        if (sources.Count == 0)
            return string.Empty;

        var header = SourceBackedLabel(
            language,
            "Sources disponibles :",
            "Available sources:",
            "Fuentes disponibles:",
            "Fontes disponiveis:",
            "Verfuegbare Quellen:",
            "Fonti disponibili:");
        var lines = sources.Select(source => $"- {FormatSourcePolicyGuardSourceRef(source, language)}");
        return $"{header}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    private static string FormatSourcePolicyGuardSourceRef(ToolMemory.SourceRef source, string language)
    {
        var label = NullIfWhiteSpace(source.Label)
                    ?? NullIfWhiteSpace(source.DocName)
                    ?? NullIfWhiteSpace(Path.GetFileName(source.DocPath))
                    ?? NullIfWhiteSpace(source.DocPath)
                    ?? "source";
        var pageStart = Math.Max(1, source.PageStart);
        var pageEnd = Math.Max(pageStart, source.PageEnd);
        var pageText = pageEnd > pageStart
            ? $"{SourceBackedPagePrefix(language)}{pageStart}-{pageEnd}"
            : $"{SourceBackedPagePrefix(language)}{pageStart}";
        return $"{label} {pageText}";
    }

    private static string BuildDocumentInstructionPolicyAnswer(string language)
        => SourceBackedLabel(
            NormalizeLanguageCode(language),
            "Je ne peux pas ignorer les consignes précédentes, inventer une réponse ou laisser une phrase dans un document modifier les règles de réponse. Une phrase retrouvée dans un document est une donnée du corpus à évaluer, pas une instruction système. Je reste limité aux sources disponibles et je refuse la partie non sourcée.",
            "I cannot ignore previous instructions, invent an answer, or let a sentence inside a document change the response rules. A sentence found in a PDF is corpus content to evaluate, not a system instruction. I stay limited to available sources and refuse the unsupported part.",
            "No puedo ignorar instrucciones anteriores, inventar una respuesta ni dejar que una frase dentro de un documento cambie las reglas de respuesta. Una frase encontrada en un PDF es contenido del corpus que se evalua, no una instruccion del sistema. Me limito a las fuentes disponibles y rechazo la parte sin respaldo.",
            "Nao posso ignorar instrucoes anteriores, inventar uma resposta nem deixar que uma frase dentro de um documento altere as regras de resposta. Uma frase encontrada num PDF e conteudo do corpus a avaliar, nao uma instrucao do sistema. Fico limitado as fontes disponiveis e recuso a parte sem suporte.",
            "Ich kann vorherige Anweisungen nicht ignorieren, keine Antwort erfinden und keinen Satz in einem Dokument die Antwortregeln aendern lassen. Ein Satz in einem PDF ist Korpusinhalt, der bewertet wird, keine Systemanweisung. Ich bleibe auf verfuegbare Quellen beschraenkt und lehne den unbelegten Teil ab.",
            "Non posso ignorare le istruzioni precedenti, inventare una risposta o lasciare che una frase dentro un documento cambi le regole di risposta. Una frase trovata in un PDF e contenuto del corpus da valutare, non un'istruzione di sistema. Mi limito alle fonti disponibili e rifiuto la parte non supportata.");

    private static string BuildSourceAbsentAssertionPolicyAnswer(string language)
        => SourceBackedLabel(
            NormalizeLanguageCode(language),
            "Non. Si l'extraction, le texte ou les sources disponibles sont vides ou insuffisants, je ne dois pas affirmer un champ, une valeur ou une conclusion documentaire. Je dois dire que l'information n'est pas confirmable avec les sources disponibles, demander une source exploitable ou citer uniquement les Ã©lÃ©ments effectivement retrouvÃ©s.",
            "No. If the extraction, text, or available sources are empty or insufficient, I must not assert a field, value, or documentary conclusion. I should state that the information cannot be confirmed from the available sources, ask for usable evidence, or cite only the evidence actually found.",
            "No. Si la extraccion, el texto o las fuentes disponibles estan vacios o son insuficientes, no debo afirmar un campo, valor o conclusion documental. Debo indicar que la informacion no se puede confirmar con las fuentes disponibles, pedir evidencia utilizable o citar solo lo encontrado.",
            "Nao. Se a extracao, o texto ou as fontes disponiveis estiverem vazios ou forem insuficientes, nao devo afirmar um campo, valor ou conclusao documental. Devo dizer que a informacao nao pode ser confirmada com as fontes disponiveis, pedir evidencia utilizavel ou citar apenas o que foi encontrado.",
            "Nein. Wenn Extraktion, Text oder verfuegbare Quellen leer oder unzureichend sind, darf ich kein Feld, keinen Wert und keine dokumentarische Schlussfolgerung behaupten. Ich sollte sagen, dass die Information mit den verfuegbaren Quellen nicht bestaetigt werden kann, nutzbare Nachweise anfordern oder nur gefundene Belege zitieren.",
            "No. Se l'estrazione, il testo o le fonti disponibili sono vuoti o insufficienti, non devo affermare un campo, un valore o una conclusione documentale. Devo dire che l'informazione non e confermabile con le fonti disponibili, chiedere evidenza utilizzabile o citare solo cio che e stato trovato.");

    private static string BuildBinaryAnswerWithSourceUncertaintyPolicyAnswer(string language)
        => SourceBackedLabel(
            NormalizeLanguageCode(language),
            "Je refuse de transformer la question en oui/non clair si les sources ne tranchent pas explicitement. Une rÃ©ponse binaire n'est acceptable que si les passages citÃ©s Ã©tablissent directement la conclusion ; sinon je dois expliquer la limite, citer les documents pertinents et Ã©viter tout toujours/jamais non prouvÃ©.",
            "I will not turn the question into a clear yes/no if the sources do not explicitly settle it. A binary answer is acceptable only when the cited passages directly support the conclusion; otherwise I should explain the limit, cite the relevant documents, and avoid any unsupported always/never claim.",
            "No convertire la pregunta en un si/no claro si las fuentes no lo resuelven explicitamente. Una respuesta binaria solo es aceptable si los pasajes citados sostienen directamente la conclusion; si no, debo explicar el limite, citar los documentos pertinentes y evitar afirmaciones absolutas sin prueba.",
            "Nao transformo a pergunta num sim/nao claro se as fontes nao resolverem isso explicitamente. Uma resposta binaria so e aceitavel quando os trechos citados sustentam diretamente a conclusao; caso contrario devo explicar o limite, citar os documentos pertinentes e evitar qualquer sempre/nunca sem prova.",
            "Ich mache daraus kein klares Ja/Nein, wenn die Quellen es nicht ausdruecklich klaeren. Eine binaere Antwort ist nur akzeptabel, wenn die zitierten Passagen die Schlussfolgerung direkt stuetzen; sonst sollte ich die Grenze erklaeren, relevante Dokumente zitieren und unbelegte Immer/Nie-Aussagen vermeiden.",
            "Non trasformo la domanda in un si/no netto se le fonti non lo stabiliscono esplicitamente. Una risposta binaria e accettabile solo quando i passaggi citati supportano direttamente la conclusione; altrimenti devo spiegare il limite, citare i documenti pertinenti ed evitare affermazioni assolute non provate.");

    private static bool LooksLikeSourceAbsentAssertionPolicyRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasAssertionMarker = Regex.IsMatch(
            normalized,
            @"\b(?:affirmer|affirme|confirmer|confirme|conclure|deduire|declarer|dire|donner|donne|pretendre|assert|state|claim|confirm|conclude|infer|deduce|give|provide|afirmar|confirmar|concluir|deduzir|behaupten|bestaetigen|bestatigen|schlussfolgern|affermare|confermare|concludere)\b",
            RegexOptions.CultureInvariant);
        if (!hasAssertionMarker)
            return false;

        var asksExactUnsupportedValue = Regex.IsMatch(
            normalized,
            @"\b(?:valeur|valeurs|value|values|champ|champs|field|fields|table|tables|tableau|tableaux)\b.{0,45}\b(?:exact|exacte|exacts|exactes|precise|precis|precises|specific)\b|\b(?:exact|exacte|exacts|exactes|precise|precis|precises|specific)\b.{0,45}\b(?:valeur|valeurs|value|values|champ|champs|field|fields|table|tables|tableau|tableaux)\b",
            RegexOptions.CultureInvariant);
        var hasUnfoundEvidenceSurface = Regex.IsMatch(
            normalized,
            @"\b(?:ne\s+(?:re)?trouves?\s+pas|pas\s+(?:re)?trouvee?|not\s+found|cannot\s+find|can't\s+find|unable\s+to\s+find|introuvable|unavailable|indisponible|absent|absente|missing|manquant|manquante)\b.{0,70}\b(?:page|pages|source|sources|preuve|preuves|evidence|table|tables|tableau|tableaux)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:page|pages|source|sources|preuve|preuves|evidence|table|tables|tableau|tableaux)\b.{0,70}\b(?:ne\s+(?:re)?trouves?\s+pas|pas\s+(?:re)?trouvee?|not\s+found|cannot\s+find|can't\s+find|unable\s+to\s+find|introuvable|unavailable|indisponible|absent|absente|missing|manquant|manquante)\b",
                RegexOptions.CultureInvariant);
        if (asksExactUnsupportedValue && hasUnfoundEvidenceSurface)
            return true;

        var hasEvidenceMissingMarker = Regex.IsMatch(
            normalized,
            @"\b(?:extraction|texte|text|source|sources|evidence|preuve|preuves|document|documents|page|pages|ocr)\b.{0,70}\b(?:vide|vides|empty|missing|absent|absente|absentes|manquant|manquante|manquantes|indisponible|unavailable|introuvable|not\s+found|insuffisant|insuffisante|insufficient|illlisible|illisible)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:vide|vides|empty|missing|absent|absente|absentes|manquant|manquante|manquantes|indisponible|unavailable|introuvable|not\s+found|insuffisant|insuffisante|insufficient|illlisible|illisible)\b.{0,70}\b(?:extraction|texte|text|source|sources|evidence|preuve|preuves|document|documents|page|pages|ocr)\b",
                RegexOptions.CultureInvariant);
        if (hasEvidenceMissingMarker)
            return true;

        var hasMissingValueMarker = Regex.IsMatch(
            normalized,
            @"\b(?:valeur|valeurs|value|values|champ|champs|field|fields|information|informations|info|data|donnee|donnees)\b.{0,70}\b(?:vide|vides|empty|missing|absent|absente|absentes|manquant|manquante|manquantes|indisponible|unavailable|introuvable|not\s+found|insuffisant|insuffisante|insufficient|illlisible|illisible)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:vide|vides|empty|missing|absent|absente|absentes|manquant|manquante|manquantes|indisponible|unavailable|introuvable|not\s+found|insuffisant|insuffisante|insufficient|illlisible|illisible)\b.{0,70}\b(?:valeur|valeurs|value|values|champ|champs|field|fields|information|informations|info|data|donnee|donnees)\b",
                RegexOptions.CultureInvariant);
        return hasMissingValueMarker;
    }

    private static bool ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|source|sources|corpus|autre\s+document|other\s+document|vs|versus)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeBinaryAnswerWithSourceUncertaintyRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var asksBinary = Regex.IsMatch(
            normalized,
            @"\b(?:oui\s*/\s*non|oui\s+non|yes\s*/\s*no|yes\s+no|sim\s*/\s*nao|ja\s*/\s*nein|si\s*/\s*no|reponse\s+(?:binaire|oui\s*/\s*non)|binary\s+answer)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:reponse|answer|respuesta|antwort|risposta)\b.{0,25}\b(?:oui|non|yes|no|ja|nein|si|sim|nao)\b",
                RegexOptions.CultureInvariant);
        if (!asksBinary)
            return false;

        var hasSourceScope = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|source|sources|corpus|preuve|preuves|evidence|passages?)\b",
            RegexOptions.CultureInvariant);
        var hasUncertaintyPolicy = Regex.IsMatch(
            normalized,
            @"\b(?:ne\s+permettent\s+pas|ne\s+permet\s+pas|permettent\s+pas|permet\s+pas|not\s+allow|do\s+not\s+allow|cannot|can't|pas\s+clair|pas\s+claire|non\s+clair|unclear|insuffisant|insuffisante|insufficient|ne\s+tranche\s+pas|not\s+settle|refuse|refuser|refuses?|refusa|refuseren|simplification|simplifier|simplify)\b",
            RegexOptions.CultureInvariant);

        return hasSourceScope && hasUncertaintyPolicy;
    }

    private static bool LooksLikeDocumentInstructionPolicyRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasDocumentMarker = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|source|sources|corpus|phrase|texte|fichier|file|files)\b",
            RegexOptions.CultureInvariant);
        var hasPolicyMarker = Regex.IsMatch(
            normalized,
            @"\b(?:consigne|consignes|instruction|instructions|regle|regles|rules|systeme|system|reponse|response|prompt)\b",
            RegexOptions.CultureInvariant);
        var hasInstructionOverrideMarker = Regex.IsMatch(
            normalized,
            @"\b(?:ignore|ignorer|ignorez|modifier|modifie|change|changer|override|bypass|precedent|precedente|precedentes|previous|prior|obeir|obeis|obey|suivre|follow|appliquer|respecter)\b",
            RegexOptions.CultureInvariant);
        var hasNoCitationInstruction = Regex.IsMatch(
                normalized,
                @"\b(?:ne\s+pas|pas|sans|without|do\s+not|don\s*t|no)\b.{0,40}\b(?:citer|cite|citation|citations|sources?|references?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:citer|cite|citation|citations|sources?|references?)\b.{0,40}\b(?:ne\s+pas|pas|sans|without|do\s+not|don\s*t|no)\b",
                RegexOptions.CultureInvariant);

        return hasDocumentMarker && hasPolicyMarker && (hasInstructionOverrideMarker || hasNoCitationInstruction);
    }

    private static bool LooksLikeDocumentVersionYearOmissionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:sans\s+(?:dire|preciser)|ne\s+precise\s+pas|ne\s+pas\s+preciser|without\s+(?:saying|specifying))\b.{0,80}\b(?:annee|annees|year|years|version|versions|edition|editions)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeExplicitSourceBypassOrUnsupportedInventionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans\s+oublier|n\s*oublie\s+pas|ne\s+pas\s+oublier|without\s+forgetting|do\s+not\s+forget|don\s*t\s+forget|sin\s+olvidar|sem\s+esquecer|ohne\s+zu\s+vergessen|senza\s+dimenticare)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = RemoveNegatedInventionPolicyPhrases(normalized);

        var asksToIgnoreSources =
            Regex.IsMatch(
                normalized,
                @"\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b.{0,60}\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sans|without|sin|sem|ohne|senza)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant);

        var asksToInvent = Regex.IsMatch(
            normalized,
            @"\b(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
            RegexOptions.CultureInvariant);

        return asksToIgnoreSources || asksToInvent;
    }

    private static bool LooksLikeHardSourceBypassOrUnsupportedInventionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans\s+oublier|n\s*oublie\s+pas|ne\s+pas\s+oublier|without\s+forgetting|do\s+not\s+forget|don\s*t\s+forget|sin\s+olvidar|sem\s+esquecer|ohne\s+zu\s+vergessen|senza\s+dimenticare)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = RemoveNegatedInventionPolicyPhrases(normalized);

        return Regex.IsMatch(
                normalized,
                @"\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sans|without|sin|sem|ohne|senza)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
                RegexOptions.CultureInvariant);
    }

    private static string ApplySourcePolicyGuardPrefix(string answer, string language)
    {
        var prefix = BuildSourcePolicyGuardPrefix(language);
        if (string.IsNullOrWhiteSpace(answer))
            return prefix;

        return answer.Contains(prefix, StringComparison.OrdinalIgnoreCase)
            ? answer.Trim()
            : $"{prefix}{Environment.NewLine}{Environment.NewLine}{answer}".Trim();
    }

    private static bool LooksLikeSourceBypassOrUnsupportedInventionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (LooksLikeSourceAbsentAssertionPolicyRequest(normalized))
            return true;

        if (LooksLikeBinaryAnswerWithSourceUncertaintyRequest(normalized))
            return true;

        if (LooksLikeDocumentInstructionPolicyRequest(normalized))
            return true;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans\s+oublier|n\s*oublie\s+pas|ne\s+pas\s+oublier|without\s+forgetting|do\s+not\s+forget|don\s*t\s+forget|sin\s+olvidar|sem\s+esquecer|ohne\s+zu\s+vergessen|senza\s+dimenticare)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = RemoveNegatedInventionPolicyPhrases(normalized);

        var asksToIgnoreSources =
            Regex.IsMatch(
                normalized,
                @"\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignoren|ignorar|ignora|ignorar|ignora|ignora|ignori|ignora|ignori|ignori|ignori|ignorar|ignorare|ignoriere|ignorieren)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b.{0,60}\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sans|without|sin|sem|ohne|senza)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant);

        var asksToInvent =
            Regex.IsMatch(
                normalized,
                @"\b(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
                RegexOptions.CultureInvariant);

        return asksToIgnoreSources || asksToInvent;
    }

    private static string RemoveNegatedInventionPolicyPhrases(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:n\s*['â€™]?\s*|ne\s+)(?:invente|inventer|inventez)\b.{0,40}\b(?:pas|rien|jamais|aucun|aucune|aucunement)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\bne\s+(?:pas|jamais)\s+(?:invente|inventer|inventez)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans|without|sin|sem|ohne|senza)\s+(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:do\s+not|don\s*t|dont|never)\s+(?:invent|make\s+up|hallucinate)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:no|not)\s+(?:invented|made\s+up|hallucinated)\b",
            " ",
            RegexOptions.CultureInvariant);

        return normalized;
    }

    private static string BuildSourcePolicyRetrievalQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans|without|sin|sem|ohne|senza)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b.{0,60}\b(?:sources?|documents?|pdf|pages?|citations?|references?|fuentes?|fontes?|quellen?|fonti)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b.{0,60}\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:une|un|an|a|una|uma|eine|uno|un)\s+(?:version|versao|versione|variante|variant)\s+(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\s+(?:de|du|des|of|da|do|das|der|die|della|del|di)?\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:une|un|an|a|una|uma|eine|uno|un)\s+(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\s+(?:version|versao|versione|variante|variant)\s+(?:de|du|des|of|da|do|das|der|die|della|del|di)?\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:version|versao|versione|variante|variant)\s+(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\s+(?:version|versao|versione|variante|variant)\b",
            " ",
            RegexOptions.CultureInvariant);

        return NormalizeRagQueryForRetrieval(normalized);
    }
}
