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
        ["fr"] = new[] { "bonjour", "salut", "coucou", "donne", "liste", "serveur", "arborescence", "résumé", "resume", "français", "francais", "merci", "stp", "comment", "documents", "document", "quels", "quelles", "présents", "present", "présent", "combien", "qui", "tu", "quoi", "categorie", "catégorie", "statistiques", "sans", "vient", "viens", "lister", "atex" },
        ["en"] = new[] { "hello", "hi", "hey", "give", "list", "server", "tree", "summary", "please", "what", "how", "english", "document", "file", "documents", "category", "categories", "which", "many", "present", "thank", "thanks", "who", "are", "you" },
        ["es"] = new[] { "hola", "dame", "lista", "servidor", "árbol", "arbol", "resumen", "español", "espanol", "archivo", "qué", "significa", "cuántos", "cuantos", "documentos", "hay", "estadísticas", "estadisticas", "categoria", "categoría", "quien", "eres" },
        ["pt"] = new[] { "olá", "ola", "lista", "servidor", "árvore", "arvore", "resumo", "português", "portugues", "arquivo", "quantos", "documentos", "estatísticas", "estatisticas", "categoria", "quem", "és", "voce" },
        ["de"] = new[] { "hallo", "bitte", "baum", "server", "zusammenfassung", "deutsch", "dokument", "dokumente", "datei", "was", "bedeutet", "liste", "wieviele", "wie viele", "vorhanden", "wer", "bist", "du" },
        ["it"] = new[] { "ciao", "elenco", "server", "albero", "riassunto", "italiano", "documento", "documenti", "file", "significa", "quali", "quanti", "presenti", "sono", "sul", "chi", "sei", "categoria", "statistiche" }
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

        return "fr";
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