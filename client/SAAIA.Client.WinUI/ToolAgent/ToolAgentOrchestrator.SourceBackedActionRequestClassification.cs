using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeSourceBackedActionRequest(string? userMessage)
    {
        var s = CollapseWhitespace(userMessage ?? string.Empty);
        if (s.Length < 6)
            return false;

        var normalized = NormalizeLooseLookup(s);
        var hasActionVerb = Regex.IsMatch(
            normalized,
            @"\b(?:aide|aider|analyse|analyser|dis|donne|donner|explique|expliquer|propose|proposes|proposer|trouve|trouver|retrouve|retrouver|retrouves|cherche|chercher|faire|fais|vais|veux|voudrais|souhaite|aimerais|peux|peux-tu|pourrais|as|aurais|idee|faut|besoin|conseille|conseiller|choisir|planifie|planifier|organise|organiser|prepare|preparer|pr.?pare|pr.?parer|verifie|verifier|v.?rifie|v.?rifier|check|verify|help|explain|analyze|analyse|tell|suggest|recommend|can|could|make|plan|prepare|find|give|need|ayuda|ayudar|ayudame|explica|analiza|propone|recomienda|recomendar|puedes|puede|podrias|busca|encuentra|preparar|planificar|necesito|ajuda|ajudar|explica|analisa|recomenda|recomendar|pode|podes|procura|encontra|preparar|planejar|planeia|preciso|vorschlag|erklaere|erklaren|analysiere|empfiehl|empfehlen|kannst|konntest|suche|finde|planen|vorbereiten|helfen|brauche|aiutami|aiuta|spiega|analizza|consiglia|consigliare|puoi|cerca|trova|prepara|pianifica|bisogno)\b",
            RegexOptions.CultureInvariant);

        var asksHow = Regex.IsMatch(
            normalized,
            @"\b(?:comment|how|como|como|wie|come)\b",
            RegexOptions.CultureInvariant);
        var mentionsDocumentarySource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|sourc[\p{L}?]*|extrait|extraits|pages?|documentaire|corpus|knowledge|base|connaissance|adaptation|adapte|adapter|adapt|adaptation)\b",
            RegexOptions.CultureInvariant);
        var asksToBypassSources = Regex.IsMatch(
            normalized,
            @"\b(?:ignore|ignorer|ignorez|oublie|oublier|sans\s+source|sans\s+sources|invente|inventer|inventez|hallucine|halluciner|make\s+up|invent|ignore\s+sources?)\b",
            RegexOptions.CultureInvariant);
        var hasSignalTerms = ExtractQuerySignalTerms(normalized).Any();

        if (LooksLikeDocumentaryContentRequest(userMessage))
            return true;

        if (!hasActionVerb
            && !(asksHow && mentionsDocumentarySource && hasSignalTerms)
            && !(mentionsDocumentarySource && asksToBypassSources && hasSignalTerms))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(userMessage))
            || hasSignalTerms;
    }
}
