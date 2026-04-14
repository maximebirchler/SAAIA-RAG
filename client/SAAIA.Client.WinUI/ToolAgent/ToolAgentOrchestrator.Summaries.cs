using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string> RenderSummaryForDisplayAsync(string summaryText, string language, string mode, Action<string>? onDelta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return string.Empty;

        if (onDelta is null)
            return summaryText.Trim();

        var system = $@"
You are SAAIA assistant.
Rewrite the provided summary faithfully.
Language: {language}
Mode: {mode}
Rules:
- Keep all concrete facts already present.
- Do not invent any additional information.
- Do not mention internal processing.
- Never output a partial URL, partial file path or visibly truncated token such as ""www"" when the source value is incomplete.
- If a value is incomplete in the source summary, omit it instead of guessing or truncating it.
- If mode=about: keep 2 to 4 short sentences maximum.
- If mode=summary: keep 2 to 4 compact paragraphs maximum.
- Return plain text only.
";

        var user = $@"SOURCE_SUMMARY:
{summaryText}";
        var streamed = new StringBuilder();

        try
        {
            await _llm.StreamAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);

            var rendered = streamed.ToString().Replace("**", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(rendered))
                return rendered;
        }
        catch
        {
            if (streamed.Length == 0)
            {
                await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
                return summaryText.Trim();
            }
        }

        if (streamed.Length == 0)
        {
            await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
            return summaryText.Trim();
        }

        return streamed.ToString().Replace("**", string.Empty).Trim();
    }

    private string BuildQuestionsListAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "meta.list_questions");
            if (item is null) return string.Empty;

            if (item.Result.ValueKind != JsonValueKind.Object || !item.Result.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var i = 1;
            var sb = new StringBuilder();
            foreach (var q in arr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (s.Length == 0) continue;
                sb.AppendLine($"{i}. {s}");
                i++;
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private (string answer, object? sourcesPayload) TryBuildSummaryAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName is "summary.get" or "rag.summarize_live");
            if (item is null || item.Result.ValueKind != JsonValueKind.Object)
                return (string.Empty, null);

            if (!item.Result.TryGetProperty("summaryText", out var st) || st.ValueKind != JsonValueKind.String)
                return (string.Empty, null);

            var answer = (st.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(answer))
                return (string.Empty, null);

            var anchors = new List<object>();
            if (item.Result.TryGetProperty("anchors", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var docPath = a.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? string.Empty) : string.Empty;
                    var pageStart = a.TryGetProperty("pageStart", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 1;
                    var pageEnd = a.TryGetProperty("pageEnd", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : pageStart;
                    var label = a.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(docPath))
                        anchors.Add(new { docPath, pageStart, pageEnd, label });
                }
            }

            if (anchors.Count == 0
                && item.Result.TryGetProperty("docPath", out var dp2) && dp2.ValueKind == JsonValueKind.String)
            {
                var docPath = (dp2.GetString() ?? string.Empty).Trim();
                var label = item.Result.TryGetProperty("docName", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? (dn.GetString() ?? string.Empty)
                    : Path.GetFileName(docPath);
                if (!string.IsNullOrWhiteSpace(docPath))
                    anchors.Add(new { docPath, pageStart = 1, pageEnd = 1, label });
            }

            object? payload = anchors.Count > 0 ? new { sources = anchors } : null;
            return (answer, payload);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private sealed record StoredSummaryHit(string SummaryText, object? SourcesPayload, string SourceLanguage);

    private async Task<(string finalAnswer, object? sourcesPayload)> GetStoredSummaryForDisplayAsync(
        string docRef,
        string targetLanguage,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        var hit = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        if (hit is null || string.IsNullOrWhiteSpace(hit.SummaryText))
            return (string.Empty, null);

        var sourceLanguage = NormalizeLanguageCode(hit.SourceLanguage);
        var requestedLanguage = NormalizeLanguageCode(targetLanguage);
        if (string.IsNullOrWhiteSpace(requestedLanguage) || string.Equals(requestedLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await EmitDeterministicTextAsync(hit.SummaryText, onDelta, ct).ConfigureAwait(false);
            return (hit.SummaryText, hit.SourcesPayload);
        }

        var cacheKey = $"{docRef}|{requestedLanguage}";
        if (_mem.SummaryTranslationCache.TryGetValue(cacheKey, out var cachedTranslation) && !string.IsNullOrWhiteSpace(cachedTranslation))
        {
            await EmitDeterministicTextAsync(cachedTranslation, onDelta, ct).ConfigureAwait(false);
            return (cachedTranslation, hit.SourcesPayload);
        }

        var translated = await TranslateStoredSummaryAsync(hit.SummaryText, sourceLanguage, requestedLanguage, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            _mem.SummaryTranslationCache[cacheKey] = translated;
            return (translated, hit.SourcesPayload);
        }

        await EmitDeterministicTextAsync(hit.SummaryText, onDelta, ct).ConfigureAwait(false);
        return (hit.SummaryText, hit.SourcesPayload);
    }

    private async Task<string> TranslateStoredSummaryAsync(
        string summaryText,
        string sourceLanguage,
        string targetLanguage,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return string.Empty;

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await EmitDeterministicTextAsync(summaryText, onDelta, ct).ConfigureAwait(false);
            return summaryText.Trim();
        }

        var streamed = new StringBuilder();
        try
        {
            var system = $@"You are SAAIA assistant.
Translate the stored summary faithfully.
Source language: {sourceLanguage}
Target language: {targetLanguage}
Rules:
- Preserve all concrete facts.
- Preserve the structure and level of detail.
- Do not shorten the text.
- Do not add any information.
- Never output a partial URL, partial file path or visibly truncated token such as ""www"".
- If a value is incomplete in the source text, omit it instead of guessing or truncating it.
- Return plain text only.";

            await _llm.StreamAsync(new[]
            {
                ("system", system),
                ("user", summaryText)
            }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta?.Invoke(delta);
            }, ct).ConfigureAwait(false);

            var translated = streamed.ToString().Replace("**", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(translated) ? summaryText.Trim() : translated;
        }
        catch
        {
            if (streamed.Length == 0 && onDelta is not null)
                await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
            return streamed.Length == 0 ? summaryText.Trim() : streamed.ToString().Replace("**", string.Empty).Trim();
        }
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunKnownDocumentSummaryFlowAsync(
        string userMessage,
        string docRef,
        DocumentSummaryRequestKind requestKind,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!string.IsNullOrWhiteSpace(docRef))
            _mem.LastRequestedDocumentRef = docRef.Trim();

        var detectedLanguage = NormalizeLanguageCode(ResolveInteractionLanguage(userMessage));
        var language = !string.IsNullOrWhiteSpace(detectedLanguage)
            ? detectedLanguage
            : NormalizeLanguageCode(_mem.LastLanguage);
        _mem.LastLanguage = language;

        return requestKind switch
        {
            DocumentSummaryRequestKind.About => await RunDocumentAboutRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryReadStoredExact => await RunDocumentStoredSummaryReadRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryCheckOnly => await RunDocumentSummaryCheckRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryStore => await RunDocumentSummaryStoreRequestAsync(docRef, language, userMessage, ct, onDelta, onProgress).ConfigureAwait(false),
            _ => await RunDocumentSummaryRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false)
        };
    }

    private DocumentSummaryRequestKind ResolveDocumentSummaryRequestKind(string userMessage, DocumentRefResolver.AnalysisResult analysis)
    {
        if (analysis.WantsStoredSummaryStore)
            return DocumentSummaryRequestKind.SummaryStore;

        if (IsExplicitStoredSummaryReadRequest(userMessage, analysis))
            return DocumentSummaryRequestKind.SummaryReadStoredExact;

        if (analysis.WantsStoredSummaryCheck)
            return DocumentSummaryRequestKind.SummaryCheckOnly;

        if (analysis.WantsAbout && !analysis.WantsSummary)
            return DocumentSummaryRequestKind.About;

        return DocumentSummaryRequestKind.SummaryReadOrLive;
    }

    private static bool IsStoredSummaryAvailabilityQuestion(string userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:verify|check|confirm|exists?|available|availability)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:v[Ã©e]rif(?:ie|ier)|disponible|existe|existence)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:est-ce\s+que|is\s+there|does\s+the\s+document\s+have|has\s+the\s+document\s+got)\b", RegexOptions.IgnoreCase);
    }

    private static bool IsExplicitStoredSummaryReadRequest(string userMessage, DocumentRefResolver.AnalysisResult analysis)
    {
        if (analysis.WantsStoredSummaryStore)
            return false;

        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        var hasStoredCue = Regex.IsMatch(s, @"\b(?:stock[Ã©e]?|stored|saved|cached|enregistr[Ã©e]?|sauvegard[Ã©e]?)\b", RegexOptions.IgnoreCase);
        if (!hasStoredCue)
            return false;

        if (IsStoredSummaryAvailabilityQuestion(s))
            return false;

        var hasReadCue = Regex.IsMatch(s, @"\b(?:donne|give|show|display|montre|affiche|read|get|load|lis|return|renvoie)\b", RegexOptions.IgnoreCase);
        return hasReadCue || analysis.WantsSummary;
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentStoredSummaryReadRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckStoredSummaryAvailable(language));

        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReturnStoredSummary(language));
            return cached;
        }

        var missing = LocalizedStrings.SummaryNotStored(language);
        await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
        return (missing, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentAboutRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressRetrieveRepresentativePassages(language));

        var liveArgs = CreateJsonArgs(new
        {
            docRef,
            level = "short",
            strategy = "about",
            language,
            maxWords = 90,
            maxChunks = 5,
            maxBatches = 1,
            maxCharsPerBatch = 2800
        });

        var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
        var fast = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
        if (!string.IsNullOrWhiteSpace(fast.answer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressComposeShortOverview(language));
            var rendered = await RenderSummaryForDisplayAsync(fast.answer, language, "about", onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(rendered) ? fast.answer : rendered, fast.sourcesPayload);
        }

        var fallback = LocalizedStrings.ShortOverviewUnavailable(language);
        await EmitDeterministicTextAsync(fallback, onDelta, ct).ConfigureAwait(false);
        return (fallback, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryCheckRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckStoredSummaryAvailable(language));

        var cached = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        if (cached is not null && !string.IsNullOrWhiteSpace(cached.SummaryText))
        {
            var yes = LocalizedStrings.SummaryAlreadyStored(language);
            await EmitDeterministicTextAsync(yes, onDelta, ct).ConfigureAwait(false);
            return (yes, cached.SourcesPayload);
        }

        var missing = LocalizedStrings.SummaryNotStored(language);
        await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
        return (missing, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckExistingStoredSummary(language));
        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReturnStoredSummary(language));
            return cached;
        }

        onProgress?.Invoke(DeterministicAgentText.ProgressBuildLiveSummaryFromDocument(language));
        var liveArgs = CreateJsonArgs(new
        {
            docRef,
            level = "medium",
            strategy = "summary",
            language,
            maxWords = 220,
            maxChunks = 18,
            maxBatches = 4,
            maxCharsPerBatch = 6500
        });

        var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
        var fast = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
        if (!string.IsNullOrWhiteSpace(fast.answer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressWriteFinalSummary(language));
            var renderedLive = await RenderSummaryForDisplayAsync(fast.answer, language, "summary", onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(renderedLive) ? fast.answer : renderedLive, fast.sourcesPayload);
        }

        var fallback = LocalizedStrings.SummaryUnavailable(language);
        await EmitDeterministicTextAsync(fallback, onDelta, ct).ConfigureAwait(false);
        return (fallback, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryStoreRequestAsync(
        string docRef,
        string language,
        string userMessage,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!_api.HasAdminKey)
        {
            var denied = LocalizedStrings.SummaryStoreRequiresAdmin(language);
            await EmitDeterministicTextAsync(denied, onDelta, ct).ConfigureAwait(false);
            return (denied, null);
        }

        var forceRefresh = WantsSummaryRefresh(userMessage);

        onProgress?.Invoke(DeterministicAgentText.ProgressCheckReusableSummaryCache(language));

        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer) && !forceRefresh)
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReusableSummaryAlreadyAvailable(language));
            return cached;
        }

        onProgress?.Invoke(DeterministicAgentText.ProgressGenerateAndStoreReusableSummary(language));

        var storedByAdmin = await TryGenerateAndStoreAdminSummaryAsync(docRef, language, ct).ConfigureAwait(false);
        if (storedByAdmin is not null && !string.IsNullOrWhiteSpace(storedByAdmin.SummaryText))
        {
            if (string.Equals(NormalizeLanguageCode(storedByAdmin.SourceLanguage), NormalizeLanguageCode(language), StringComparison.OrdinalIgnoreCase))
            {
                await EmitDeterministicTextAsync(storedByAdmin.SummaryText, onDelta, ct).ConfigureAwait(false);
                return (storedByAdmin.SummaryText, storedByAdmin.SourcesPayload);
            }

            var translated = await TranslateStoredSummaryAsync(storedByAdmin.SummaryText, NormalizeLanguageCode(storedByAdmin.SourceLanguage), NormalizeLanguageCode(language), onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(translated) ? storedByAdmin.SummaryText : translated, storedByAdmin.SourcesPayload);
        }

        var failed = LocalizedStrings.SummaryStoreFailed(language);
        await EmitDeterministicTextAsync(failed, onDelta, ct).ConfigureAwait(false);
        return (failed, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> TryGetStoredSummaryAnswerAsync(string docRef, CancellationToken ct)
    {
        var hit = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        return hit is null || string.IsNullOrWhiteSpace(hit.SummaryText)
            ? (string.Empty, null)
            : (hit.SummaryText, hit.SourcesPayload);
    }

    private async Task<StoredSummaryHit?> TryGetStoredSummaryHitAsync(string docRef, CancellationToken ct)
    {
        try
        {
            var args = CreateJsonArgs(new { docRef, level = "medium" });
            var exists = await ExecSummaryExistsAsync(args, ct).ConfigureAwait(false);
            if (!TryGetBoolProp(exists, "exists").GetValueOrDefault())
                return null;

            var summary = await ExecSummaryGetAsync(args, ct).ConfigureAwait(false);
            var fast = TryBuildSummaryAnswer(BuildSingleToolResult("summary.get", summary));
            if (string.IsNullOrWhiteSpace(fast.answer))
                return null;

            var sourceLanguage = TryGetString(summary, "docLanguage") ?? TryGetString(summary, "DocLanguage") ?? string.Empty;
            return new StoredSummaryHit(fast.answer, fast.sourcesPayload, sourceLanguage);
        }
        catch
        {
            return null;
        }
    }

    private async Task<StoredSummaryHit?> TryGenerateAndStoreAdminSummaryAsync(
        string docRef,
        string language,
        CancellationToken ct)
    {
        string? jobId = null;

        try
        {
            ClientLog.Info($"summary.admin.generate:start docRef={docRef}");
            var generateArgs = CreateJsonArgs(new { docRef, level = "medium", force = false });
            var generate = await ExecAdminSummaryGenerateAsync(generateArgs, ct).ConfigureAwait(false);
            jobId = TryGetString(generate, "jobId");
            ClientLog.Info($"summary.admin.generate:queued docRef={docRef} jobId={jobId}");
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"summary.admin.generate:failed docRef={docRef} error={ex.Message}");
            return null;
        }

        try
        {
            var liveArgs = CreateJsonArgs(new
            {
                docRef,
                level = "long",
                strategy = "store",
                language,
                maxWords = 2400,
                maxChunks = 120,
                maxBatches = 18,
                maxCharsPerBatch = 12000
            });

            var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
            var liveSummary = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
            if (string.IsNullOrWhiteSpace(liveSummary.answer))
            {
                ClientLog.Warn($"summary.admin.live:empty docRef={docRef} jobId={jobId}");
                return null;
            }

            var submitArgs = CreateJsonArgs(new
            {
                jobId,
                docRef,
                level = "medium",
                docLanguage = language,
                summaryText = liveSummary.answer,
                meta = new
                {
                    generationMode = "client_admin_cache",
                    cachedForUsers = true,
                    generatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                }
            });

            var submit = await ExecAdminSummarySubmitAsync(submitArgs, ct).ConfigureAwait(false);
            ClientLog.Info($"summary.admin.submit:done docRef={docRef} jobId={jobId} stored={TryGetBoolProp(submit, "stored").GetValueOrDefault()}");

            var cached = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
            if (cached is not null && !string.IsNullOrWhiteSpace(cached.SummaryText))
            {
                ClientLog.Info($"summary.admin.verify:hit docRef={docRef} jobId={jobId}");
                return cached;
            }

            ClientLog.Warn($"summary.admin.verify:miss docRef={docRef} jobId={jobId}");
            return new StoredSummaryHit(liveSummary.answer, liveSummary.sourcesPayload, language);
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"summary.admin.submit:failed docRef={docRef} jobId={jobId} error={ex.Message}");
            return null;
        }
    }

    private static ToolResults BuildSingleToolResult(string toolName, JsonElement result)
    {
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = result
        });
        return toolResults;
    }

    private static JsonElement CreateJsonArgs(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    private static bool WantsSummaryRefresh(string userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:refresh|regenerate|rebuild|update)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:regenere|regÃ©nÃ¨re|met\s+a\s+jour|mise\s+a\s+jour|recr[eÃ©]e)\b", RegexOptions.IgnoreCase);
    }

    private static string PrefixSummaryMessage(string prefix, string summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return prefix;
        if (string.IsNullOrWhiteSpace(prefix))
            return summaryText;
        return $"{prefix.Trim()}\n\n{summaryText.Trim()}";
    }
}
