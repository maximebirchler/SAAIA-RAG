using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private (bool handled, string answer, List<ToolMemory.SourceRef>? sources, string? requestedItemTitle)
        TryBuildEarlyWriterBypassAnswer(
            RouterPlan plan,
            ToolResults toolResults,
            ToolResults writerToolResults,
            string userMessage,
            string writerUserMessage,
            string? inventoryRenderedText)
    {
        var backendClarification = TryBuildBackendGuidanceClarificationAnswer(writerToolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(backendClarification))
        {
            RememberPendingClarification("rag_guidance", userMessage, "backend_ask_clarification", plan.Language);
            _lastAnswerSource = $"backend_guidance_ask_clarification:{plan.Intent}";
            EmitRagTrace(
                "writer.bypass",
                ("reason", "backend_guidance_ask_clarification"),
                ("answer_source", _lastAnswerSource));
            return (true, backendClarification, null, null);
        }

        var versionTraceabilityAnswer = TryBuildDocumentVersionTraceabilityAnswer(toolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(versionTraceabilityAnswer))
        {
            var traceabilitySources = DeriveSourcesFromDocumentVersionTraceabilityHits(toolResults, userMessage);
            _lastAnswerSource = $"writer_bypass_document_version_traceability:{plan.Intent}";
            EmitRagTrace(
                "writer.bypass",
                ("reason", "document_version_traceability"),
                ("answer_source", _lastAnswerSource),
                ("sources", traceabilitySources.Count));
            return (true, versionTraceabilityAnswer, traceabilitySources.Count > 0 ? traceabilitySources : null, null);
        }

        var sourcePolicyGuard = TryBuildSourcePolicyGuardAnswer(writerToolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(sourcePolicyGuard))
        {
            var guardSources = LooksLikeDocumentInstructionPolicyRequest(userMessage)
                ? new List<ToolMemory.SourceRef>()
                : DeriveSourcesFromRagHits(writerToolResults).Take(5).ToList();
            _lastAnswerSource = $"writer_bypass_source_policy:{plan.Intent}";
            EmitRagTrace(
                "writer.bypass",
                ("reason", "source_policy_guard"),
                ("answer_source", _lastAnswerSource),
                ("sources", guardSources.Count));
            return (true, sourcePolicyGuard, guardSources.Count > 0 ? guardSources : null, null);
        }

        if (ShouldBypassWriterForDeterministicInventory(plan, writerToolResults, inventoryRenderedText))
        {
            var deterministicAnswer = (inventoryRenderedText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
            {
                _lastAnswerSource = $"writer_bypass_deterministic_inventory:{plan.Intent}";
                EmitRagTrace(
                    "writer.bypass",
                    ("reason", "deterministic_inventory"),
                    ("answer_source", _lastAnswerSource));
                return (true, deterministicAnswer, null, null);
            }
        }

        var writerEvidenceQuery = BuildRagEvidenceSelectionQuery(writerUserMessage);
        var requestedItemTitle = LooksLikeShortTechnicalEvidenceTopic(writerEvidenceQuery)
            ? null
            : TryExtractRequestedItemTitle(writerUserMessage);
        if (!string.IsNullOrWhiteSpace(requestedItemTitle))
        {
            var ragHits = EnumerateRagHitSummaries(writerToolResults).ToList();
            if (ragHits.Count > 0 && !RagHitsContainRequestedTitle(ragHits, requestedItemTitle!))
            {
                _lastAnswerSource = $"writer_bypass_missing_exact_item:{plan.Intent}";
                var missingExactAnswer = BuildMissingExactItemAnswer(plan.Language, requestedItemTitle!, ragHits);
                if (LooksLikeSourceBypassOrUnsupportedInventionRequest(userMessage))
                    missingExactAnswer = ApplySourcePolicyGuardPrefix(missingExactAnswer, plan.Language);
                var missingExactSources = DeriveSourcesFromMissingExactItemCloseLeads(requestedItemTitle!, ragHits);
                if (LooksLikeMissingExactItemWithoutSourceLeads(missingExactAnswer))
                    missingExactSources.Clear();
                EmitRagTrace(
                    "writer.bypass",
                    ("reason", "missing_exact_item"),
                    ("requested_title", requestedItemTitle),
                    ("answer_source", _lastAnswerSource),
                    ("sources", missingExactSources.Count));
                return (true, missingExactAnswer, missingExactSources, requestedItemTitle);
            }
        }

        return (false, string.Empty, null, requestedItemTitle);
    }
}
