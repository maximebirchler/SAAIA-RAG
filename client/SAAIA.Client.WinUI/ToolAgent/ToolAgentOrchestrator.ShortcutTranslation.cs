using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string> TryTranslateLastAnswerOneShotAsync(string language, CancellationToken ct, Action<string>? onDelta)
    {
        if (string.IsNullOrWhiteSpace(_mem.LastAssistantAnswer))
            return string.Empty;

        if (ShouldTranslateFromDeterministicRender())
        {
            var deterministic = TryRenderLastDeterministicAnswer(language);
            if (!string.IsNullOrWhiteSpace(deterministic))
            {
                await EmitDeterministicTextAsync(deterministic, onDelta, ct).ConfigureAwait(false);
                return deterministic;
            }
        }

        var protectedTerms = CollectProtectedTranslationTerms(_mem.LastAssistantAnswer);
        var protectedAnswer = ApplyProtectedTranslationTerms(_mem.LastAssistantAnswer, protectedTerms, out var placeholders);
        var protectedTermsBlock = protectedTerms.Count == 0
            ? string.Empty
            : $@"
Protected canonical values (keep them exactly as written; do not translate, rename or alter them):
- {string.Join("\n- ", protectedTerms)}
";

        var system = $@"
You are SAAIA assistant.
Translate the provided assistant answer faithfully.
Target language: {language}
Rules:
- Keep the same factual content.
- Do not invent, add, remove or merge list entries.
- Preserve tokens like [[open|...]] exactly.
- Preserve placeholders like __KEEP_0001__ exactly and do not translate them.
- Return plain text only.
{protectedTermsBlock}
";

        var user = $@"
ASSISTANT_ANSWER_TO_TRANSLATE:
{protectedAnswer}
";

        var translated = await CompleteTextResponseAsync(system, user, ct, onDelta: null).ConfigureAwait(false);
        translated = RestoreProtectedTranslationTerms(translated, placeholders);
        await EmitDeterministicTextAsync(translated, onDelta, ct).ConfigureAwait(false);
        return translated;
    }

    private bool ShouldTranslateFromDeterministicRender()
    {
        if (_mem.LastDeterministicRender is null || !IsInventoryIntent(_mem.LastDeterministicRender.RouterIntent ?? _mem.LastRouterIntent))
            return false;

        var lastAnswer = (_mem.LastAssistantAnswer ?? string.Empty).Trim();
        if (lastAnswer.Length == 0)
            return false;

        var renderedInLastLanguage = TryRenderLastDeterministicAnswer(_mem.LastAnswerLanguage ?? _mem.LastLanguage);
        if (string.IsNullOrWhiteSpace(renderedInLastLanguage))
            return false;

        return NormalizeReplayComparableText(lastAnswer) == NormalizeReplayComparableText(renderedInLastLanguage);
    }

    private static string NormalizeReplayComparableText(string text)
        => Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");

    private IReadOnlyList<string> CollectProtectedTranslationTerms(string assistantAnswer)
    {
        var answer = assistantAnswer ?? string.Empty;
        var terms = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
                return;
            if (value.Contains("\n", StringComparison.Ordinal) || value.Contains("\r", StringComparison.Ordinal))
                return;
            if (answer.IndexOf(value, StringComparison.Ordinal) < 0)
                return;
            terms.Add(value);
        }

        foreach (var category in _mem.LastPresentedCategories)
        {
            AddTerm(category.DisplayName);
            AddTerm(category.CategoryPath);
        }

        if (_mem.LastResolvedCategory is not null)
        {
            AddTerm(_mem.LastResolvedCategory.DisplayName);
            AddTerm(_mem.LastResolvedCategory.CategoryPath);
        }

        foreach (var doc in _mem.LastListedDocuments)
        {
            AddTerm(doc.DocName);
            AddTerm(doc.DocPath);
            AddTerm(doc.CategoryPath);
            AddTerm(doc.Category);
        }

        if (_mem.LastFocusedDocument is not null)
        {
            AddTerm(_mem.LastFocusedDocument.DocName);
            AddTerm(_mem.LastFocusedDocument.DocPath);
            AddTerm(_mem.LastFocusedDocument.CategoryPath);
            AddTerm(_mem.LastFocusedDocument.Category);
        }

        if (_mem.LastDeterministicRender is not null && !string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.DataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(_mem.LastDeterministicRender.DataJson);
                CollectProtectedTermsFromJson(doc.RootElement, AddTerm);
            }
            catch
            {
                // best effort only
            }
        }

        return terms
            .OrderByDescending(x => x.Length)
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectProtectedTermsFromJson(JsonElement element, Action<string?> addTerm)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (property.NameEquals("name")
                            || property.NameEquals("path")
                            || property.NameEquals("docName")
                            || property.NameEquals("docPath")
                            || property.NameEquals("category")
                            || property.NameEquals("categoryPath")
                            || property.NameEquals("DocName")
                            || property.NameEquals("DocPath")
                            || property.NameEquals("Category")
                            || property.NameEquals("CategoryPath"))
                        {
                            addTerm(property.Value.GetString());
                        }
                    }
                    else
                    {
                        CollectProtectedTermsFromJson(property.Value, addTerm);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectProtectedTermsFromJson(item, addTerm);
                break;
        }
    }

    private static string ApplyProtectedTranslationTerms(string text, IReadOnlyList<string> terms, out Dictionary<string, string> placeholders)
    {
        placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
        var protectedText = text ?? string.Empty;
        if (terms.Count == 0)
            return protectedText;

        var index = 1;
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term) || protectedText.IndexOf(term, StringComparison.Ordinal) < 0)
                continue;

            var token = $"__KEEP_{index:0000}__";
            placeholders[token] = term;
            protectedText = protectedText.Replace(term, token, StringComparison.Ordinal);
            index++;
        }

        return protectedText;
    }

    private static string RestoreProtectedTranslationTerms(string text, IReadOnlyDictionary<string, string> placeholders)
    {
        var restored = text ?? string.Empty;
        foreach (var pair in placeholders)
            restored = restored.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return restored;
    }
}
