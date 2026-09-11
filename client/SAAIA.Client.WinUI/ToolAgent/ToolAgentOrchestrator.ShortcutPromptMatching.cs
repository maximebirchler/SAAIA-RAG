using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;
using System.Diagnostics;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool MatchesCanonicalDynamicDisplayPrompt(string? message, Func<string?, string, string> promptBuilder)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        if (rawMessage.Length == 0)
            return false;

        var normalizedMessage = NormalizeExactPromptText(rawMessage);
        var token = NormalizeExactPromptText("__VALUE__");
        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = NormalizeExactPromptText(promptBuilder(language, "__VALUE__").Trim());
            var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
            if (tokenIndex < 0)
                continue;

            var prefix = template[..tokenIndex].TrimEnd();
            var suffix = template[(tokenIndex + token.Length)..].TrimStart();
            if (!normalizedMessage.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(suffix) && !normalizedMessage.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var valueLength = normalizedMessage.Length - prefix.Length - suffix.Length;
            if (valueLength > 0)
                return true;
        }

        return false;
    }

    private static bool MatchesCanonicalStaticPrompt(string? message, Func<string?, string> promptBuilder)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        var normalizedMessage = NormalizeShortcutToken(rawMessage);
        if (normalizedMessage.Length == 0 || ContainsConversationalContentCue(normalizedMessage))
            return false;

        var canonicalMessage = NormalizeExactPromptText(rawMessage);
        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = promptBuilder(language).Trim();
            if (string.Equals(rawMessage, template, StringComparison.Ordinal)
                || string.Equals(canonicalMessage, NormalizeExactPromptText(template), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchCanonicalDynamicPrompt(string? message, Func<string?, string, string> promptBuilder, out string value)
    {
        value = string.Empty;
        var rawMessage = (message ?? string.Empty).Trim();
        var normalizedMessage = NormalizeShortcutToken(rawMessage);
        if (normalizedMessage.Length == 0 || ContainsConversationalContentCue(normalizedMessage))
            return false;

        const string token = "__value__";
        var canonicalMessage = NormalizeExactPromptText(rawMessage);

        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = promptBuilder(language, token).Trim();
            var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
            if (tokenIndex < 0)
                continue;

            var prefix = template[..tokenIndex];
            var suffix = template[(tokenIndex + token.Length)..];
            if (prefix.Length > 0 && rawMessage.StartsWith(prefix, StringComparison.Ordinal)
                && (suffix.Length == 0 || rawMessage.EndsWith(suffix, StringComparison.Ordinal)))
            {
                var length = rawMessage.Length - prefix.Length - suffix.Length;
                if (length > 0)
                {
                    var captured = rawMessage.Substring(prefix.Length, length).Trim();
                    if (captured.Length > 0)
                    {
                        value = captured;
                        return true;
                    }
                }
            }

            var canonicalTemplate = NormalizeExactPromptText(template);
            var canonicalTokenIndex = canonicalTemplate.IndexOf(token, StringComparison.Ordinal);
            if (canonicalTokenIndex < 0)
                continue;

            var canonicalPrefix = canonicalTemplate[..canonicalTokenIndex];
            var canonicalSuffix = canonicalTemplate[(canonicalTokenIndex + token.Length)..];
            if (canonicalPrefix.Length > 0 && !canonicalMessage.StartsWith(canonicalPrefix, StringComparison.Ordinal))
                continue;
            if (canonicalSuffix.Length > 0 && !canonicalMessage.EndsWith(canonicalSuffix, StringComparison.Ordinal))
                continue;

            var canonicalLength = canonicalMessage.Length - canonicalPrefix.Length - canonicalSuffix.Length;
            if (canonicalLength <= 0)
                continue;

            var canonicalCaptured = canonicalMessage.Substring(canonicalPrefix.Length, canonicalLength).Trim();
            if (canonicalCaptured.Length == 0)
                continue;

            value = canonicalCaptured;
            return true;
        }

        return false;
    }

    private static string NormalizeExactPromptText(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\u00E2\u20AC\u2122", "'")
            .Replace("\u00E2\u20AC\u02DC", "'")
            .Replace("\u2019", "'")
            .Replace("\u2018", "'")
            .Replace("\u00C2\u00A0", " ")
            .Replace("\u00A0", " ")
            .Replace("\u00E2\u20AC\u00A6", "...")
            .Replace("\u2026", "...")
            .Trim();

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static string NormalizeShortcutToken(string? value)
    {
        var normalized = StripDiacritics(value ?? string.Empty).Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}/_-]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }
}
