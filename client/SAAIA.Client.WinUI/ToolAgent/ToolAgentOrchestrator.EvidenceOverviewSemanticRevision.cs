using System.Diagnostics;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record EvidenceOverviewSemanticRevision(
        EvidenceOverviewCandidateValidation Validation,
        SourceVerificationResult Verification,
        long ElapsedMilliseconds);

    private async Task<string> CompleteEvidenceOverviewWriterAsync(
        IReadOnlyList<(string role, string content)> messages,
        int maximumOutputTokens, CancellationToken ct)
    {
        if (_llm is not ISourceBackedAgentLlmClient nativeLlm)
            throw new InvalidOperationException("The canonical overview writer requires native LLM support.");
        var nativeMessages = messages.Select(message => new SourceBackedAgentMessage(message.role, message.content)).ToArray();
        var tools = Array.Empty<SourceBackedAgentToolDefinition>();
        var runtimeContext = nativeLlm is ISourceBackedAgentRuntimeContextProvider runtimeProvider
            ? await runtimeProvider.GetRuntimeContextTokensAsync(ct).ConfigureAwait(false) : null;
        var contextTokens = runtimeContext is > 0 ? runtimeContext.Value : ResolveActiveLlmContextTokens(_settings);
        var measuredInput = nativeLlm is ISourceBackedAgentInputTokenCounter counter
            ? await counter.CountInputTokensAsync(nativeMessages, tools, ct, requireToolCall: false).ConfigureAwait(false) : null;
        var inputTokens = measuredInput is >= 0 ? measuredInput.Value
            : SourceBackedLlmCumulativeBudgetContext.EstimateInputTokens(nativeMessages, tools);
        var fits = (long)inputTokens + maximumOutputTokens + SourceBackedAgentV2Runner.ContextSafetyReserveTokens <= contextTokens;
        EmitRagTrace("document_overview.writer.context_admission",
            ("admitted", fits), ("input_tokens", inputTokens), ("maximum_output_tokens", maximumOutputTokens),
            ("context_tokens", contextTokens), ("input_source", measuredInput is >= 0 ? "measured" : "estimated"),
            ("context_source", runtimeContext is > 0 ? "runtime_models" : "application_settings"));
        if (!fits)
            throw new InvalidOperationException($"The overview writer request exceeds the {(measuredInput is >= 0 ? "measured" : "estimated")} context budget: input={inputTokens}, output={maximumOutputTokens}, context={contextTokens}.");
        using var terminalWriter = SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
        var completion = await nativeLlm.CompleteAsync(
            nativeMessages, tools, maximumOutputTokens, ct,
            requireToolCall: false).ConfigureAwait(false);
        EmitRagTrace("document_overview.writer.native_completed",
            ("maximum_output_tokens", maximumOutputTokens),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("finish_reason", completion.FinishReason));
        if (completion.ToolCalls.Count != 0
            || !string.IsNullOrWhiteSpace(completion.ProtocolError)
            || string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The overview writer did not return a complete text draft.");
        return completion.Content;
    }

    private async Task<EvidenceOverviewSemanticRevision?> TryReviseEvidenceOverviewDraftAsync(
        string userRequest, string responseLanguage, ResolvedDocRef document,
        WriterDraft draft, IReadOnlyList<string> reasons, IReadOnlyList<EvidenceItem> evidence,
        EvidenceBundle bundle, int requiredCandidateCount, CancellationToken ct)
    {
        var prompt = BuildEvidenceOverviewWriterPrompt(
            userRequest, document, responseLanguage, requiredCandidateCount, evidence)
            + "\nBROUILLON A CORRIGER:\n" + draft.Answer
            + "\nAVIS DE REVUE A CONFRONTER AUX PREUVES:\n" + string.Join("\n", reasons)
            + "\nReecris le resume complet dans le meme contrat. Corrige les critiques justifiees par les extraits. "
            + "L'avis du juge et le brouillon ne sont pas des preuves ; n'ajoute aucun fait provenant seulement de cet avis.";
        var watch = Stopwatch.StartNew();
        var raw = await CompleteEvidenceOverviewWriterAsync(
            new[] { ("system", BuildEvidenceOverviewWriterSystemPrompt(responseLanguage)), ("user", prompt) },
            maximumOutputTokens: 320, ct).ConfigureAwait(false);
        watch.Stop();
        if (!string.Equals(NormalizeLanguageCode(LocalizedStrings.DetectLanguage(raw, responseLanguage)),
                NormalizeLanguageCode(responseLanguage), StringComparison.OrdinalIgnoreCase))
        {
            EmitRagTrace("document_overview.semantic_revision.rejected", ("reason", "language_mismatch"));
            return null;
        }
        var validation = ValidateAndRenderEvidenceOverviewCandidates(raw, evidence, evidence.Count, responseLanguage);
        var revisedDraft = new WriterDraft(validation.RenderedAnswer, validation.CitedEvidenceIds);
        var verification = SourceContractVerifier.Verify(revisedDraft, bundle,
            intake: null, allowedEvidenceIds: validation.CitedEvidenceIds, enforceRequestedShape: false,
            requireEveryAllowedEvidenceIdExactlyOnce: true, allowMultipleEvidencePerVisibleSource: false,
            requireSeparateAtomicClaims: true);
        if (validation.ValidCandidateCount != requiredCandidateCount || !verification.IsValid)
        {
            EmitRagTrace("document_overview.semantic_revision.rejected",
                ("reason", "source_or_candidate_contract"), ("valid_count", validation.ValidCandidateCount),
                ("rejections", validation.Rejections), ("verification_errors", verification.Errors));
            return null;
        }
        EmitRagTrace("document_overview.semantic_revision.ready_for_review",
            ("candidate_count", validation.ValidCandidateCount), ("repair_ms", watch.ElapsedMilliseconds));
        return new EvidenceOverviewSemanticRevision(validation, verification, watch.ElapsedMilliseconds);
    }
}
