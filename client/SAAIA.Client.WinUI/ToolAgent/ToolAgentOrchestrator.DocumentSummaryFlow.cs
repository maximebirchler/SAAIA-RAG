using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private enum DocumentSummaryRequestKind
    {
        About,
        SummaryReadOrLive,
        SummaryReadStoredExact,
        SummaryCheckOnly,
        SummaryStore
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleRouterDrivenDocumentSummaryFlowAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string semanticUserMessage,
        RouterPlan plan,
        DocumentRefResolver.AnalysisResult docResolution,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        if (!TryDetermineSummaryRequestKind(semanticUserMessage, plan, docResolution, out var requestKind))
            return (false, string.Empty, null);

        var docRef = ResolveDocumentReferenceForSummaryPlan(plan, docResolution);
        if (string.IsNullOrWhiteSpace(docRef))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseClarification(plan.Language));
            var clarification = await GenerateClarificationResponseAsync(
                chatHistory,
                displayUserMessage,
                plan.Language,
                "doc_reference",
                docResolution.ClarificationHint ?? "document_reference",
                ct,
                onDelta).ConfigureAwait(false);

            RememberPendingClarification("doc_reference", displayUserMessage, docResolution.ClarificationHint ?? "document_reference", plan.Language);
            onProgress?.Invoke(string.Empty);
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            _lastAnswerSource = "summary.flow:clarification";
            EmitRagTrace(
                "summary.flow.end",
                ("handled", true),
                ("result", "clarification"),
                ("reason", "missing_doc_ref"),
                ("kind", requestKind.ToString()));
            var finalizedClarification = FinalizeAndReturn(
                swTotalPipeline,
                displayUserMessage,
                clarification,
                null,
                "clarification",
                Array.Empty<string>(),
                _mem.LastReasoningTracePublic,
                clearPendingClarification: false);
            return (true, finalizedClarification.finalAnswer, finalizedClarification.sourcesPayload);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseSummary(plan.Language));
        EmitRagTrace(
            "summary.flow.start",
            ("doc_ref", docRef),
            ("kind", requestKind.ToString()),
            ("intent", plan.Intent));
        var swSummary = Stopwatch.StartNew();
        var plannedLiveSummaryArgs = plan.ToolCalls
            .FirstOrDefault(static call => string.Equals(
                call.Name,
                "rag.summarize_live",
                StringComparison.OrdinalIgnoreCase))
            ?.Args;
        var summaryAnswer = await RunKnownDocumentSummaryFlowAsync(
                semanticUserMessage,
                docRef,
                requestKind,
                ct,
                onDelta,
                onProgress,
                plannedLiveSummaryArgs)
            .ConfigureAwait(false);
        swSummary.Stop();

        var rememberedIntent = requestKind switch
        {
            DocumentSummaryRequestKind.About => "rag.summarize_doc",
            DocumentSummaryRequestKind.SummaryReadStoredExact => "summary.get",
            DocumentSummaryRequestKind.SummaryCheckOnly => "summary.exists",
            DocumentSummaryRequestKind.SummaryStore => "admin.summary.generate",
            _ => "rag.summarize_doc"
        };

        ClearPendingClarification();
        _lastUsedSummaryFlow = true;
        _lastWriterToolNames = new List<string> { "summary.flow" };
        _lastToolsMs = swSummary.ElapsedMilliseconds;
        _lastWriterMs = 0;
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("summary.flow", swSummary.ElapsedMilliseconds, true) };
        _lastAnswerSource = $"summary.flow:{rememberedIntent}";
        EmitRagTrace(
            "summary.flow.end",
            ("handled", true),
            ("result", "answer"),
            ("intent", rememberedIntent),
            ("answer_source", _lastAnswerSource),
            ("answer_chars", summaryAnswer.finalAnswer?.Length ?? 0),
            ("sources_payload", summaryAnswer.sourcesPayload is not null),
            ("ms", swSummary.ElapsedMilliseconds));
        var finalizedSummary = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            summaryAnswer.finalAnswer ?? string.Empty,
            summaryAnswer.sourcesPayload,
            rememberedIntent,
            new[] { "summary.flow" },
            _mem.LastReasoningTracePublic);
        return (true, finalizedSummary.finalAnswer, finalizedSummary.sourcesPayload);
    }

    private bool TryDetermineSummaryRequestKind(string userMessage, RouterPlan plan, DocumentRefResolver.AnalysisResult docResolution, out DocumentSummaryRequestKind requestKind)
    {
        requestKind = default;

        var intent = NormalizeRouterIntent(plan.Intent);
        if (intent == "summary.exists")
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        if (intent is "admin.summary.generate" or "admin.summary.submit")
        {
            requestKind = DocumentSummaryRequestKind.SummaryStore;
            return true;
        }

        if (intent is "document.about" or "document_about" or "rag.about_doc")
        {
            requestKind = DocumentSummaryRequestKind.About;
            return true;
        }

        if (intent == "rag.summarize_doc")
        {
            requestKind = IsAboutResponseFormat(plan.ResponseFormat) || (docResolution.WantsAbout && !docResolution.WantsSummary)
                ? DocumentSummaryRequestKind.About
                : DocumentSummaryRequestKind.SummaryReadOrLive;
            return true;
        }

        var hasSummaryTool = plan.ToolCalls.Any(call => call.Name is "summary.exists" or "summary.get" or "rag.summarize_live" or "admin.summary.generate" or "admin.summary.request");
        if (!hasSummaryTool)
            return false;

        if (docResolution.WantsStoredSummaryStore)
        {
            requestKind = DocumentSummaryRequestKind.SummaryStore;
            return true;
        }

        if (IsExplicitStoredSummaryReadRequest(userMessage, docResolution))
        {
            requestKind = DocumentSummaryRequestKind.SummaryReadStoredExact;
            return true;
        }

        if (docResolution.WantsStoredSummaryCheck)
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        requestKind = IsAboutResponseFormat(plan.ResponseFormat) || (docResolution.WantsAbout && !docResolution.WantsSummary)
            ? DocumentSummaryRequestKind.About
            : DocumentSummaryRequestKind.SummaryReadOrLive;
        return true;
    }

    private string? ResolveDocumentReferenceForSummaryPlan(RouterPlan plan, DocumentRefResolver.AnalysisResult docResolution)
    {
        foreach (var call in plan.ToolCalls)
        {
            var docRef = GetStringArg(call.Args, "docRef")
                         ?? GetStringArg(call.Args, "pdfRef")
                         ?? GetStringArg(call.Args, "docId")
                         ?? GetStringArg(call.Args, "docPath");
            if (!string.IsNullOrWhiteSpace(docRef))
                return docRef;
        }

        if (!string.IsNullOrWhiteSpace(docResolution.ResolvedDocRef))
            return docResolution.ResolvedDocRef;

        if (_mem.LastFocusedDocument is not null)
        {
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocId))
                return _mem.LastFocusedDocument.DocId;
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocPath))
                return _mem.LastFocusedDocument.DocPath;
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocName))
                return _mem.LastFocusedDocument.DocName;
        }

        return null;
    }
}
