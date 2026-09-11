using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record CandidateCollectionApproval(
        string EvidenceId,
        string DisplayValue,
        IReadOnlyList<string>? CompatibleColumnLabels = null);

    private sealed record CandidateCollectionClassification(
        string EvidenceId,
        string Classification,
        string DisplayValue,
        bool DisplayValueIndexNormalized = false);

    private sealed record CandidateCollectionAuditDecision(
        bool ProtocolValid,
        IReadOnlyList<CandidateCollectionApproval> ApprovedCandidates,
        IReadOnlyList<string> RejectedEvidenceIds,
        IReadOnlyList<CandidateCollectionClassification> Classifications,
        string? FailureReason)
    {
        public IReadOnlyList<string> ApprovedEvidenceIds
            => ApprovedCandidates
                .Select(static candidate => candidate.EvidenceId)
                .ToArray();
    }

    private sealed record CandidateCollectionAuditApplication(
        int ApprovedSourceCount,
        int MechanicallySuppressedDuplicateCount,
        bool HasRequiredEvidence,
        string Feedback,
        string ToolResult);

    private void AddCandidateCollectionStateTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        bool collectionOpen,
        int observedEvidenceCount,
        int visibleCandidateCount,
        int pendingCandidateCount,
        int auditedEvidenceCount,
        int rejectedEvidenceCount,
        int approvedSourceCount)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.EvidenceJudge,
            "source_backed_agent_v2.candidate_collection.state",
            ("turn", turn),
            ("collection_open", collectionOpen),
            ("observed_evidence", observedEvidenceCount),
            ("visible_distinct_candidates", visibleCandidateCount),
            ("pending_evidence", pendingCandidateCount),
            ("audited_evidence", auditedEvidenceCount),
            ("rejected_evidence", rejectedEvidenceCount),
            ("approved_sources", approvedSourceCount),
            ("audit_available", collectionOpen
                && visibleCandidateCount > 0)));

    private static IReadOnlyList<EvidenceItem> SelectCandidateCollectionItems(
        EvidenceBundle bundle,
        IEnumerable<string> observedEvidenceIds,
        IReadOnlySet<string> pendingSemanticCandidateIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds,
        int maximumItems)
        => observedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(pendingSemanticCandidateIds.Contains)
            .Where(id => !semanticallyRejectedEvidenceIds.Contains(id))
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null
                                  && IsMechanicallyCitableCandidate(item))
            .Cast<EvidenceItem>()
            .DistinctBy(
                static item => item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maximumItems))
            .ToArray();

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildCandidateCollectionTools(
            IReadOnlyList<SourceBackedAgentToolDefinition> turnTools,
            IReadOnlyList<string> candidateScopePaths,
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            int maximumWorkingEvidenceItems,
            SourceBackedIntake? intake = null)
    {
        var scopedToolSource = candidateScopePaths.Any(static path =>
                !string.IsNullOrWhiteSpace(path))
            ? SourceBackedAgentToolCatalog.Build(
                useConstrainedContextDescriptions: true,
                allowedCategoryPaths: candidateScopePaths,
                includeCategoryPathEnums: true)
            : turnTools;
        var documentaryTools = scopedToolSource
            .Where(static tool => tool.Name is
                "rag_search"
                or "documents_navigation"
                or "documents_content_cards"
                or "documents_context")
            .Select(static tool => tool with
            {
                Description = tool.Name switch
                {
                    "documents_content_cards" =>
                        "Inventorie beaucoup d'instances nommees, canoniques et paginees. Sans q, decouvre des noms inconnus; avec q, filtre litteralement. Juge ensuite titre, kind, headingPath et evidence.",
                    "rag_search" =>
                        "Recherche une cible source precise. query vise une intention; queries regroupe des intentions complementaires. N'utilise que des termes susceptibles d'apparaitre dans les documents.",
                    "documents_navigation" =>
                        "Explore rapidement titres, sommaires, index et pages pour decouvrir des noms inconnus. kind=navigation_entry cible les entrees structurees de sommaire/index; kind=title_anchor cible les autres titres; omets kind pour comparer les deux. Les pointeurs sont non citables: choisis les ancres utiles, puis transforme-les en preuves par recherche groupee ou lecture ciblee.",
                    "documents_context" =>
                        "Lit le passage autour d'un document, d'une page ou d'un chunk deja localise.",
                    _ => tool.Description
                }
            })
            .ToList();
        var researchTransitionTools = BuildResearchTransitionTools(
            bundle,
            resolvedNavigationEvidenceIds,
            executedRequests,
            maximumWorkingEvidenceItems);
        if (researchTransitionTools.Count > 0)
        {
            // The current navigation observation must be acknowledged explicitly.
            // Every semantic transition remains available inside this atomic LLM
            // decision; code only translates the selected transition afterward.
            return AddResolvedDocumentReadingTool(researchTransitionTools, intake);
        }
        return documentaryTools;
    }

    private static EvidenceBundle ApplyCandidateDisplayValues(
        EvidenceBundle bundle,
        IReadOnlyList<CandidateCollectionApproval> approvals)
    {
        if (approvals.Count == 0)
            return bundle;

        var approvalByEvidenceId = approvals.ToDictionary(
            static approval => approval.EvidenceId,
            static approval => approval,
            StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var items = bundle.Items
            .Select(item =>
            {
                if (!approvalByEvidenceId.TryGetValue(
                        item.EvidenceId,
                        out var approval))
                {
                    return item;
                }

                var hints = new Dictionary<string, string>(
                    item.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [SemanticDisplayValueHint] = approval.DisplayValue
                };
                if (approval.CompatibleColumnLabels is not null)
                {
                    hints[SemanticCompatibleColumnLabelsHint] =
                        JsonSerializer.Serialize(
                            approval.CompatibleColumnLabels
                                .Where(static label =>
                                    !string.IsNullOrWhiteSpace(label))
                                .Select(static label => label.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray(),
                            ClientJson.CamelCase);
                }
                changed = true;
                return item with { SelectionHints = hints };
            })
            .ToArray();
        return changed ? bundle with { Items = items } : bundle;
    }

    private static CandidateCollectionAuditApplication
        ApplyCandidateCollectionAudit(
            CandidateCollectionAuditDecision decision,
            IReadOnlyList<EvidenceItem> collectionCandidates,
            EvidenceBundle bundle,
            LlmEvidenceWorkspace evidenceWorkspace,
            ISet<string> semanticallyAuditedEvidenceIds,
            ISet<string> semanticallyRejectedEvidenceIds,
            ISet<string> observedEvidenceIdSet,
            List<string> observedEvidenceIds,
            ISet<string> pendingSemanticCandidateIds,
            int requiredEvidenceCount)
    {
        if (!decision.ProtocolValid)
        {
            var invalidFeedback =
                "AUDIT GLOBAL REFUSE PAR LE CONTRAT: "
                + decision.FailureReason
                + ". Aucun candidat de ce passage n'est accepte; poursuis seulement "
                + "avec des preuves deja auditees ou une autre recherche.";
            return new CandidateCollectionAuditApplication(
                CountApprovedCandidateSources(
                    bundle,
                    observedEvidenceIds,
                    semanticallyAuditedEvidenceIds,
                    semanticallyRejectedEvidenceIds),
                0,
                false,
                invalidFeedback,
                JsonSerializer.Serialize(new
                {
                    accepted = false,
                    error = decision.FailureReason
                }, ClientJson.CamelCase));
        }

        foreach (var candidate in collectionCandidates)
            semanticallyAuditedEvidenceIds.Add(candidate.EvidenceId);
        var classificationByEvidenceId = decision.Classifications
            .ToDictionary(
                static item => item.EvidenceId,
                static item => item.Classification,
                StringComparer.OrdinalIgnoreCase);
        foreach (var rejectedEvidenceId in decision.RejectedEvidenceIds)
        {
            evidenceWorkspace.Reject(
                rejectedEvidenceId,
                "Classement du juge semantique: "
                + classificationByEvidenceId.GetValueOrDefault(
                    rejectedEvidenceId,
                    OtherWrongTypeClassification)
                + ".");
            semanticallyRejectedEvidenceIds.Add(rejectedEvidenceId);
            observedEvidenceIdSet.Remove(rejectedEvidenceId);
            observedEvidenceIds.RemoveAll(id => string.Equals(
                id,
                rejectedEvidenceId,
                StringComparison.OrdinalIgnoreCase));
        }
        foreach (var candidate in collectionCandidates)
            pendingSemanticCandidateIds.Remove(candidate.EvidenceId);
        var auditedVisibleSourceKeys = collectionCandidates
            .Select(static candidate => candidate.VisibleSourceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mechanicallySuppressedDuplicateIds = pendingSemanticCandidateIds
            .Where(id => bundle.ById.TryGetValue(id, out var item)
                         && auditedVisibleSourceKeys.Contains(item.VisibleSourceKey))
            .ToArray();
        foreach (var duplicateEvidenceId in mechanicallySuppressedDuplicateIds)
        {
            pendingSemanticCandidateIds.Remove(duplicateEvidenceId);
            semanticallyRejectedEvidenceIds.Add(duplicateEvidenceId);
            evidenceWorkspace.Reject(
                duplicateEvidenceId,
                "Doublon mecanique d'une source visible deja jugee.");
            observedEvidenceIdSet.Remove(duplicateEvidenceId);
            observedEvidenceIds.RemoveAll(id => string.Equals(
                id,
                duplicateEvidenceId,
                StringComparison.OrdinalIgnoreCase));
        }

        var approvedSourceCount = CountApprovedCandidateSources(
            bundle,
            observedEvidenceIds,
            semanticallyAuditedEvidenceIds,
            semanticallyRejectedEvidenceIds);
        var hasRequiredEvidence = approvedSourceCount >= requiredEvidenceCount;
        var feedback = hasRequiredEvidence
            ? "AUDIT GLOBAL ACCEPTE: le dernier lot a fourni "
              + decision.ApprovedEvidenceIds.Count
              + " candidat(s) approuve(s) sur "
              + collectionCandidates.Count
              + "; le vivier couvre maintenant le besoin. Passe a la selection "
              + "semantique finale."
            : "AUDIT GLOBAL ACCEPTE MAIS VIVIER INSUFFISANT: le dernier lot a fourni "
              + decision.ApprovedEvidenceIds.Count
              + " candidat(s) approuve(s) sur "
              + collectionCandidates.Count
              + "; total actuel "
              + approvedSourceCount
              + " sources distinctes approuvees sur "
              + requiredEvidenceCount
              + ". Compare ce rendement aux autres capacites puis poursuis librement "
              + "la collecte selon les lacunes semantiques.";
        return new CandidateCollectionAuditApplication(
            approvedSourceCount,
            mechanicallySuppressedDuplicateIds.Length,
            hasRequiredEvidence,
            feedback,
            JsonSerializer.Serialize(new
            {
                accepted = true,
                approvedEvidenceIds = decision.ApprovedEvidenceIds,
                rejectedEvidenceIds = decision.RejectedEvidenceIds,
                approvedSourceCount,
                requiredEvidenceCount,
                hasRequiredEvidence
            }, ClientJson.CamelCase));
    }

}
