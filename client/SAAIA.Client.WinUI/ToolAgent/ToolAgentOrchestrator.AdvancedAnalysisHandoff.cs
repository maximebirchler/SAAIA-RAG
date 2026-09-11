using SAAIA.Contracts;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int AdvancedHandoffMaximumResearchItems = 32;
    private const int AdvancedHandoffMaximumEvidenceReferences = 64;

    private AdvancedAnalysisHandoffEnvelope? _lastAdvancedAnalysisHandoff;

    public AdvancedAnalysisHandoffEnvelope? LastAdvancedAnalysisHandoff
        => _lastAdvancedAnalysisHandoff;

    private AdvancedAnalysisHandoffEnvelope BuildAdvancedAnalysisHandoff(
        RouterPlan plan,
        string requestText,
        string reasonCode,
        string transferStage,
        int answerUnitCount,
        AdvancedAnalysisLocalBudgetSnapshot? localBudget = null)
    {
        var mission = plan.SourceBackedMission;
        var attempts = (_mem.ResearchWorkingNotes ?? new List<ToolMemory.ResearchWorkingNote>())
            .TakeLast(AdvancedHandoffMaximumResearchItems)
            .Select(static note => new AdvancedAnalysisResearchAttempt
            {
                CreatedAtUtc = note.CreatedAtUtc,
                Queries = DistinctNonBlank(note.Queries, AdvancedHandoffMaximumResearchItems),
                CategoryScope = NullIfBlank(note.CategoryScope),
                DocPath = NullIfBlank(note.DocPath),
                PageStart = note.PageStart,
                PageEnd = note.PageEnd,
                Outcome = note.Outcome ?? string.Empty,
                Accepted = note.Accepted,
                RejectReason = NullIfBlank(note.RejectReason),
                CandidateCountBefore = note.CandidateCountBefore,
                CandidateCountAfter = note.CandidateCountAfter,
                UsableHitsBefore = note.UsableHitsBefore,
                UsableHitsAfter = note.UsableHitsAfter,
                ElapsedMilliseconds = note.ElapsedMs
            })
            .ToList();
        var executedQueries = DistinctNonBlank(
            (_mem.LastRagQueries ?? new List<string>())
                .Concat(attempts.SelectMany(static attempt => attempt.Queries)),
            AdvancedHandoffMaximumResearchItems);
        var evidenceReferences = (_mem.LastSourcesUsed ?? new List<ToolMemory.SourceRef>())
            .Where(static source => HasCanonicalAdvancedEvidenceIdentity(source))
            .GroupBy(
                static source => BuildAdvancedEvidenceIdentityKey(source),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(AdvancedHandoffMaximumEvidenceReferences)
            .Select(static source => new AdvancedAnalysisEvidenceReference
            {
                EvidenceId = NullIfBlank(source.EvidenceId),
                DocId = NullIfBlank(source.DocId),
                RevisionId = NullIfBlank(source.RevisionId),
                FileName = NullIfBlank(source.DocName),
                DocPath = NullIfBlank(source.DocPath),
                SourceHash = NullIfBlank(source.SourceHash),
                PageStart = Math.Max(1, source.PageStart),
                PageEnd = Math.Max(Math.Max(1, source.PageStart), source.PageEnd),
                ChunkId = NullIfBlank(source.ChunkId),
                AnchorId = NullIfBlank(source.AnchorId),
                ContentCardId = NullIfBlank(source.ContentCardId)
            })
            .ToList();

        return new AdvancedAnalysisHandoffEnvelope
        {
            HandoffId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestText = requestText ?? string.Empty,
            Language = string.IsNullOrWhiteSpace(plan.Language) ? "fr" : plan.Language.Trim(),
            OriginIntent = plan.Intent ?? string.Empty,
            ReasonCode = reasonCode ?? string.Empty,
            TransferStage = transferStage ?? string.Empty,
            Load = new AdvancedAnalysisLoadDescriptor
            {
                PlanKind = mission?.PlanKind ?? string.Empty,
                Deliverable = mission?.Deliverable ?? string.Empty,
                AnswerUnitCount = Math.Max(0, answerUnitCount),
                AtomicEvidenceCount = Math.Max(0, mission?.AtomicEvidenceCount ?? 0),
                RowCount = Math.Max(0, mission?.RowCount ?? 0),
                ColumnCount = Math.Max(0, mission?.ColumnCount ?? 0),
                StructuredLayout = mission?.StructuredLayout == true,
                AtomicEvidenceType = mission?.AtomicEvidenceType ?? string.Empty,
                AtomicEvidenceMode = mission?.AtomicEvidenceMode ?? string.Empty,
                SelectionPolicy = mission?.SelectionPolicy ?? string.Empty,
                QuestionFocus = mission?.QuestionFocus ?? string.Empty,
                RequestedDocumentName = mission?.RequestedDocumentName ?? string.Empty,
                BoundedNamedDocumentExtraction = mission?.BoundedNamedDocumentExtraction == true,
                CandidateScopePaths = DistinctNonBlank(
                    mission?.CandidateScopePaths,
                    AdvancedHandoffMaximumResearchItems),
                RowLabels = DistinctNonBlank(
                    mission?.RowLabels,
                    AdvancedHandoffMaximumResearchItems),
                Columns = DistinctNonBlank(
                    mission?.Columns,
                    AdvancedHandoffMaximumResearchItems)
            },
            ResearchState = new AdvancedAnalysisResearchState
            {
                MemoryIsEvidence = false,
                EvidenceRevalidationRequired = true,
                ExecutedTools = DistinctNonBlank(
                    _mem.LastToolNames,
                    AdvancedHandoffMaximumResearchItems),
                ExecutedQueries = executedQueries,
                Attempts = attempts,
                EvidenceReferences = evidenceReferences
            },
            LocalBudget = localBudget,
            DataPolicy = new AdvancedAnalysisDataPolicy
            {
                ExternalProviderContentAuthorized = false,
                ExternalProviderMetadataAuthorized = false,
                AuthorizationSource = "server_policy_required"
            }
        };
    }

    private AdvancedAnalysisHandoffEnvelope BuildAndRememberAdvancedAnalysisHandoff(
        RouterPlan plan,
        string requestText,
        string reasonCode,
        string transferStage,
        int answerUnitCount,
        AdvancedAnalysisLocalBudgetSnapshot? localBudget = null)
    {
        var handoff = BuildAdvancedAnalysisHandoff(
            plan,
            requestText,
            reasonCode,
            transferStage,
            answerUnitCount,
            localBudget);
        _lastAdvancedAnalysisHandoff = handoff;
        return handoff;
    }

    private static AdvancedAnalysisLocalBudgetSnapshot MapAdvancedAnalysisBudgetSnapshot(
        SourceBackedLlmBudgetExceededException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var snapshot = exception.Snapshot;
        return new AdvancedAnalysisLocalBudgetSnapshot
        {
            StopReason = exception.Reason,
            MaximumTokens = snapshot.MaximumTokens,
            MaximumElapsedMilliseconds = snapshot.MaximumElapsedMilliseconds,
            TerminalReserveTokens = snapshot.TerminalReserveTokens,
            TerminalReserveMilliseconds = snapshot.TerminalReserveMilliseconds,
            ChargedTokens = snapshot.ChargedTokens,
            ReservedTokens = snapshot.ReservedTokens,
            RemainingTokens = snapshot.RemainingTokens,
            RemainingMilliseconds = snapshot.RemainingMilliseconds,
            NormalBudgetClosed = snapshot.NormalBudgetClosed,
            AdmittedCalls = snapshot.AdmittedCalls,
            CompletedCalls = snapshot.CompletedCalls,
            FailedCalls = snapshot.FailedCalls,
            LastUsageSource = snapshot.LastUsageSource,
            LastAdmissionReason = snapshot.LastAdmissionReason,
            LastCallClass = snapshot.LastCallClass
        };
    }

    private static List<string> DistinctNonBlank(
        IEnumerable<string>? values,
        int maximum)
        => (values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maximum))
            .ToList();

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasCanonicalAdvancedEvidenceIdentity(ToolMemory.SourceRef source)
        => !string.IsNullOrWhiteSpace(source.DocId)
           || !string.IsNullOrWhiteSpace(source.RevisionId)
           || !string.IsNullOrWhiteSpace(source.ChunkId)
           || !string.IsNullOrWhiteSpace(source.AnchorId)
           || !string.IsNullOrWhiteSpace(source.ContentCardId)
           || !string.IsNullOrWhiteSpace(source.SourceHash);

    private static string BuildAdvancedEvidenceIdentityKey(ToolMemory.SourceRef source)
        => string.Join(
            '|',
            source.DocId ?? string.Empty,
            source.RevisionId ?? string.Empty,
            source.ChunkId ?? string.Empty,
            source.AnchorId ?? string.Empty,
            source.ContentCardId ?? string.Empty,
            source.SourceHash ?? string.Empty,
            source.PageStart,
            source.PageEnd);

    internal static AdvancedAnalysisHandoffEnvelope BuildAdvancedAnalysisHandoffForTests(
        RouterPlan plan,
        ToolMemory memory,
        string requestText,
        string reasonCode,
        string transferStage,
        int answerUnitCount,
        AdvancedAnalysisLocalBudgetSnapshot? localBudget = null)
    {
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            new AdvancedHandoffTestLlmClient(),
            memory);
        return orchestrator.BuildAdvancedAnalysisHandoff(
            plan,
            requestText,
            reasonCode,
            transferStage,
            answerUnitCount,
            localBudget);
    }

    internal void BuildAndRememberAdvancedAnalysisHandoffForTests(
        RouterPlan plan,
        string requestText,
        string reasonCode,
        string transferStage,
        int answerUnitCount)
        => BuildAndRememberAdvancedAnalysisHandoff(
            plan,
            requestText,
            reasonCode,
            transferStage,
            answerUnitCount);

    internal void ResetLastTurnDiagnosticsForTests()
        => ResetLastTurnDiagnostics();

    private sealed class AdvancedHandoffTestLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct)
            => throw new NotSupportedException("A756 handoff construction performs no LLM call.");

        public Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct)
            => throw new NotSupportedException("A756 handoff construction performs no LLM call.");
    }
}
