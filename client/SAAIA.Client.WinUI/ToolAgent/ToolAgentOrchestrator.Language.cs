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
        ["fr"] = new[] { "bonjour", "salut", "coucou", "donne", "liste", "serveur", "arborescence", "résumé", "resume", "français", "francais", "merci", "stp", "comment", "documents", "document", "quels", "quelles", "présents", "present", "présent", "combien" },
        ["en"] = new[] { "hello", "hi", "hey", "give", "list", "server", "tree", "summary", "please", "what", "how", "english", "document", "file", "documents", "which", "many", "present" },
        ["es"] = new[] { "hola", "dame", "lista", "servidor", "árbol", "arbol", "resumen", "español", "espanol", "por", "favor", "archivo", "qué", "que", "significa", "cuántos", "cuantos", "documentos", "hay", "presentes" },
        ["pt"] = new[] { "olá", "ola", "liste", "lista", "servidor", "árvore", "arvore", "resumo", "português", "portugues", "por", "favor", "arquivo", "o", "que", "quantos", "documentos", "presentes" },
        ["de"] = new[] { "hallo", "bitte", "baum", "server", "zusammenfassung", "deutsch", "dokument", "dokumente", "datei", "was", "bedeutet", "liste", "wieviele", "wie viele", "vorhanden" },
        ["it"] = new[] { "ciao", "elenco", "server", "albero", "riassunto", "italiano", "per", "favore", "documento", "documenti", "file", "che", "significa", "quali", "quanti", "presenti", "sono", "sul" }
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

        var patterns = new[]
        {
            @"^(?:please\s+)?(?:respond|answer|reply|talk|speak|continue|write|reponds|réponds|parle|continue|continuer|ecris|écris|responde|contesta|habla|sigue|escribe|fale|continua|escreva|antworte|beantworte|sprich|schreibe|rispondi|parla|scrivi)\s+(?:in|en|em|auf)?\s*(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?[!.?]*$",
            @"^(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?[!.?]*$",
            @"^(?:non\s+|not\s+)?(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?[!.?]*$",
            @"^(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?[!.?]*$"
        };

        foreach (var pattern in patterns)
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
        return string.IsNullOrWhiteSpace(detected) ? NormalizeLanguageCode(_mem.LastLanguage) : detected;
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