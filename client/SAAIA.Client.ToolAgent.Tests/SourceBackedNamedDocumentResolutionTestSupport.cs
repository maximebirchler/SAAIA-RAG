using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    private static SourceBackedIntake NamedDocumentIntake(
        string requestedDocumentName,
        JsonElement initialArguments)
        => Intake("Quel est l'intervalle d'inspection indiqué dans ce document ?") with
        {
            RequestedDocumentName = requestedDocumentName,
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    "router-search",
                    "rag_search",
                    initialArguments,
                    "llm_router")
            },
            InitialSemanticMission = NamedDocumentMission()
        };

    private static SourceBackedAgentV2Options NamedDocumentRunnerOptions()
        => Options() with
        {
            MaximumTurns = 1,
            SeparateActionAndWriter = true,
            MaximumSemanticCorrectionTurns = 0,
            RequireEvidenceSelectionBeforeWriter = true,
            SemanticColumnRoleReviewEnabled = true,
            SemanticCandidateStrategyEnabled = false,
            SemanticCandidateDefinitionEnabled = true,
            StructuredSemanticPlanningEnabled = true
        };

    private static SourceBackedInitialSemanticMission NamedDocumentMission()
        => new(
            JsonSerializer.SerializeToElement(new
            {
                planKind = "structured_layout",
                deliverable = "four documented inspection statements",
                structuredLayout = true,
                rowCount = 2,
                columnCount = 2,
                atomicEvidenceCount = 4,
                atomicEvidenceType = "documented inspection statement",
                initialCapability = "rag_search",
                rowHeader = "Group",
                rowLabels = new[] { "A", "B" },
                columns = new[] { "Statement 1", "Statement 2" }
            }),
            "llm_router");

    private static SourceBackedDocumentResolutionCandidate Candidate(
        string docId,
        string docPath)
        => new(
            docId,
            docPath,
            Path.GetFileName(docPath),
            RevisionId: "rev-current",
            SourceHash: new string('a', 64));

    private static SourceBackedDocumentResolutionObservation Observation(
        SourceBackedDocumentResolutionStatus status,
        bool complete,
        params SourceBackedDocumentResolutionCandidate[] candidates)
        => new(
            "Service Bulletin HX-42.pdf",
            status,
            complete,
            candidates,
            status.ToString().ToLowerInvariant(),
            PagesObserved: 1,
            ItemsObserved: candidates.Length,
            ReachedSafetyLimit: false,
            ElapsedMilliseconds: 3);

    private static SourceBackedAgentCompletion NamedDocumentTerminalDecision(
        SourceBackedDocumentResolutionStatus status,
        IReadOnlyList<SourceBackedDocumentResolutionCandidate> candidates)
    {
        if (status == SourceBackedDocumentResolutionStatus.NotFound)
        {
            return Completion(Call(
                "catalog-decision",
                "declare_named_document_insufficiency",
                new
                {
                    reason =
                        "The complete current catalog contains no exact identity for the requested file."
                }));
        }

        if (status == SourceBackedDocumentResolutionStatus.Ambiguous)
        {
            return Completion(Call(
                "catalog-decision",
                "request_named_document_candidate_selection",
                new
                {
                    question = "Which exact document identity should be used?",
                    executionImpact =
                        "The selected identity determines which document can supply evidence."
                }));
        }

        return Completion(Call(
            "catalog-decision",
            "request_named_document_reference_correction",
            new
            {
                identityQuestion =
                    "Can you provide a corrected exact document reference?",
                identityCorrectionOptions = new[]
                {
                    "Provide the exact file name or document identifier",
                    "Provide the complete document path"
                },
                executionImpact =
                    "The corrected identity will be checked against the current catalog."
            }));
    }

    private sealed class RecordingNamedDocumentResolver(
        SourceBackedDocumentResolutionObservation observation)
        : ISourceBackedNamedDocumentResolver
    {
        public List<string> RequestedReferences { get; } = new();

        public Task<SourceBackedDocumentResolutionObservation> ResolveAsync(
            string requestedReference,
            CancellationToken ct)
        {
            RequestedReferences.Add(requestedReference);
            return Task.FromResult(observation);
        }
    }

    private sealed class SequencedNamedDocumentResolver(
        params SourceBackedDocumentResolutionObservation[] observations)
        : ISourceBackedNamedDocumentResolver
    {
        private readonly Queue<SourceBackedDocumentResolutionObservation> _items =
            new(observations);

        public List<string> RequestedReferences { get; } = new();

        public Task<SourceBackedDocumentResolutionObservation> ResolveAsync(
            string requestedReference,
            CancellationToken ct)
        {
            RequestedReferences.Add(requestedReference);
            return Task.FromResult(_items.Dequeue());
        }
    }
}
