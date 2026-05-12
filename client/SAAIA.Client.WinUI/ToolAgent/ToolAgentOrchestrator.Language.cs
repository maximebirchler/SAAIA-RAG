using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static readonly Dictionary<string, string[]> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = new[] { "fr", "french", "français", "francais", "francese" },
        ["en"] = new[] { "en", "english", "anglais", "inglés", "ingles", "inglês", "inglese" },
        ["es"] = new[] { "es", "spanish", "español", "espanol", "espagnol", "spagnolo" },
        ["pt"] = new[] { "pt", "portuguese", "português", "portugues", "portugais", "portoghese" },
        ["de"] = new[] { "de", "german", "deutsch", "deutch", "allemand", "tedesco" },
        ["it"] = new[] { "it", "italian", "italiano", "italien" }
    };

    private static readonly Dictionary<string, string[]> LanguageSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = new[] { "bonjour", "salut", "coucou", "merci", "stp", "svp", "je", "tu", "vous", "nous", "peux", "pouvez", "donne", "explique", "montre", "trouve", "cherche", "comment", "combien", "pourquoi", "quoi", "qui", "quel", "quels", "quelle", "quelles", "ou", "avec", "sans", "pour", "dans", "sur", "entre", "source", "sources", "document", "documents", "page", "pages", "reponds", "francais" },
        ["en"] = new[] { "hello", "hi", "hey", "please", "thanks", "thank", "i", "you", "we", "can", "could", "should", "must", "give", "show", "find", "explain", "search", "what", "how", "many", "why", "which", "who", "where", "with", "without", "for", "from", "about", "source", "sources", "document", "documents", "page", "pages", "answer", "english" },
        ["es"] = new[] { "hola", "gracias", "por favor", "yo", "tu", "usted", "puedes", "puede", "dame", "muestra", "busca", "busco", "consejo", "consejos", "coccion", "asado", "puntos", "hablan", "explica", "encuentra", "que", "como", "cuantos", "cuantas", "por que", "cual", "cuales", "quien", "donde", "con", "sin", "para", "desde", "sobre", "fuente", "fuentes", "documento", "documentos", "pagina", "paginas", "responde", "espanol" },
        ["pt"] = new[] { "ola", "obrigado", "obrigada", "por favor", "eu", "tu", "voce", "podes", "pode", "da", "mostra", "procura", "procuro", "conselho", "conselhos", "cozedura", "assar", "pontos", "falam", "explica", "encontra", "que", "como", "quantos", "quantas", "porque", "qual", "quais", "quem", "onde", "com", "sem", "para", "desde", "sobre", "fonte", "fontes", "documento", "documentos", "pagina", "paginas", "responde", "portugues" },
        ["de"] = new[] { "hallo", "bitte", "danke", "ich", "du", "sie", "wir", "kannst", "konnen", "gib", "zeige", "suche", "hinweise", "bratenthermometer", "garstufen", "darueber", "finde", "erklaere", "was", "wie", "wieviele", "warum", "welche", "wer", "wo", "mit", "ohne", "fur", "aus", "uber", "quelle", "quellen", "dokument", "dokumente", "seite", "seiten", "antworte", "deutsch", "mache", "wartung" },
        ["it"] = new[] { "ciao", "grazie", "per favore", "io", "tu", "lei", "noi", "puoi", "puo", "dammi", "mostra", "cerca", "cerco", "consiglio", "consigli", "cottura", "arrosti", "livelli", "parlano", "trova", "spiega", "cosa", "come", "quanti", "quante", "perche", "quale", "quali", "chi", "dove", "con", "senza", "per", "da", "su", "fonte", "fonti", "documento", "documenti", "pagina", "pagine", "rispondi", "italiano" }
    };

    private static string StripDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool TryMapLanguageAlias(string token, out string language)
    {
        var normalized = StripDiacritics(token).Trim().ToLowerInvariant();

        foreach (var pair in LanguageAliases)
        {
            if (pair.Value.Any(x => StripDiacritics(x) == normalized))
            {
                language = pair.Key;
                return true;
            }
        }

        language = string.Empty;
        return false;
    }

    private bool TryDetectExplicitLanguageSwitch(string message, out string language)
    {
        var s = StripDiacritics(message ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            language = string.Empty;
            return false;
        }

        if (LocalizedStrings.TryDetectLanguagePreferenceChange(s, out language))
            return true;

        var informalPatterns = new[]
        {
            @"^(?:ca|ça)\s+donne\s+quoi\s+(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)\s*[!.?]*$",
            @"^(?:what\s+about|how\s+about|and|et|alors|donc|du\s+coup|maintenant|now)\s+(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)\s*[!.?]*$",
            @"^(?:comment|how)\s+(?:le|la|that|this|ca|ça|cela|ce\s+message)\s+(?:se\s+dit|says|looks|sounds)?\s*(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)\s*[!.?]*$"
        };

        foreach (var pattern in informalPatterns)
        {
            var match = Regex.Match(s, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryMapLanguageAlias(match.Groups["lang"].Value, out language))
                return true;
        }

        language = string.Empty;
        return false;
    }

    private string ResolveInteractionLanguage(string userMessage)
    {
        if (TryDetectExplicitLanguageSwitch(userMessage, out var requested))
            return requested;

        var detected = DetectMessageLanguage(userMessage);
        if (!string.IsNullOrWhiteSpace(detected))
            return detected;

        return IsSupportedClientLanguageCode(_mem.LastLanguage) ? NormalizeLanguageCode(_mem.LastLanguage) : "fr";
    }

    private string ResolveTurnLanguage(string userMessage, string? routerLanguage, string interactionLanguage)
    {
        if (TryDetectExplicitLanguageSwitch(userMessage, out var requested))
            return requested;

        var detected = DetectMessageLanguage(userMessage);
        if (!string.IsNullOrWhiteSpace(detected))
            return NormalizeLanguageCode(detected);

        if (IsSupportedClientLanguageCode(routerLanguage))
            return NormalizeLanguageCode(routerLanguage);

        if (IsSupportedClientLanguageCode(interactionLanguage))
            return NormalizeLanguageCode(interactionLanguage);

        return "fr";
    }

    private static bool IsSupportedClientLanguageCode(string? language)
    {
        var normalized = StripDiacritics(language ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "fr" or "en" or "es" or "pt" or "de" or "it";
    }

    private static string DetectMessageLanguage(string? message)
    {
        var s = StripDiacritics(message ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in LanguageSignals)
        {
            var score = 0;

            foreach (var token in pair.Value)
            {
                var normalizedToken = StripDiacritics(token);
                if (Regex.IsMatch(s, $@"\b{Regex.Escape(normalizedToken)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    score += normalizedToken.Length <= 3 ? 1 : 2;
            }

            scores[pair.Key] = score;
        }

        var best = scores.OrderByDescending(x => x.Value).First();
        if (best.Value < 2) return string.Empty;

        var second = scores.OrderByDescending(x => x.Value).Skip(1).First();
        if (best.Value == second.Value) return string.Empty;

        return best.Key;
    }
}
