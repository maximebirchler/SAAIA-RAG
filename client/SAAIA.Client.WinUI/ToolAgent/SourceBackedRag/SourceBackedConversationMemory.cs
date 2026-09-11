using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedConversationActionMemory(
    string ActionKey,
    string ToolName,
    string Query,
    string? CategoryPath,
    string? DocId,
    string? DocPath,
    string? ChunkId,
    int? PageStart,
    int? PageEnd,
    int? Offset,
    int? Limit,
    string Purpose);

public sealed record SourceBackedConversationEvidenceMemory(
    string StableEvidenceKey,
    string EvidenceIdAtTurn,
    string Decision,
    string DecisionReason,
    string SourceKind,
    string? ItemIdentity,
    string? DisplayLabel,
    string? DocId,
    string? DocPath,
    string? SourceHash,
    string? RevisionId,
    string? ChunkId,
    int? PageStart,
    int? PageEnd);

public sealed record SourceBackedConversationUsedItemMemory(
    string ItemIdentity,
    string DisplayLabel,
    string StableEvidenceKey,
    string EvidenceIdAtTurn,
    string? DocPath,
    int? PageStart,
    int? PageEnd);

public sealed record SourceBackedConversationTurnMemory(
    int SchemaVersion,
    string TurnId,
    DateTimeOffset CreatedAtUtc,
    string TaskKind,
    string UserQuestion,
    string Outcome,
    IReadOnlyList<SourceBackedConversationActionMemory> ExecutedActions,
    IReadOnlyList<SourceBackedConversationEvidenceMemory> EvidenceDecisions,
    IReadOnlyList<SourceBackedConversationUsedItemMemory> UsedItems,
    IReadOnlyList<string> SemanticReviewNotes);

internal static class SourceBackedConversationMemoryFactory
{
    private const int MaximumRememberedActionsPerTurn = 48;
    private const int MaximumRememberedEvidencePerTurn = 160;
    private const int MaximumSemanticReviewNotes = 12;

    internal static SourceBackedConversationTurnMemory Create(
        string turnId,
        SourceBackedIntake intake,
        IReadOnlyList<RetrievalRequest> executedRequests,
        EvidenceBundle bundle,
        IReadOnlyCollection<string> citedEvidenceIds,
        IReadOnlyCollection<string> semanticallyRejectedEvidenceIds,
        bool clarificationRequested,
        bool answerReady,
        IReadOnlyList<string> semanticReviewNotes)
    {
        var cited = citedEvidenceIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = semanticallyRejectedEvidenceIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actions = executedRequests
            .Where(static request => request is not null)
            .Select(ToActionMemory)
            .GroupBy(static action => action.ActionKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(MaximumRememberedActionsPerTurn)
            .ToArray();
        var evidence = bundle.Items
            .Where(static item => item is not null)
            .Select(item => ToEvidenceMemory(
                item,
                cited.Contains(item.EvidenceId),
                rejected.Contains(item.EvidenceId),
                intake.Language))
            .Take(MaximumRememberedEvidencePerTurn)
            .ToArray();
        var usedItems = evidence
            .Where(static item =>
                string.Equals(item.Decision, "used", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.ItemIdentity)
                && !string.IsNullOrWhiteSpace(item.DisplayLabel))
            .Select(static item => new SourceBackedConversationUsedItemMemory(
                item.ItemIdentity!,
                item.DisplayLabel!,
                item.StableEvidenceKey,
                item.EvidenceIdAtTurn,
                item.DocPath,
                item.PageStart,
                item.PageEnd))
            .GroupBy(static item => item.ItemIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        return new SourceBackedConversationTurnMemory(
            SchemaVersion: 1,
            TurnId: turnId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            TaskKind: intake.TaskKind,
            UserQuestion: intake.UserQuestion,
            Outcome: clarificationRequested
                ? "clarification_requested"
                : answerReady
                    ? "answer_ready"
                    : "insufficient_evidence",
            ExecutedActions: actions,
            EvidenceDecisions: evidence,
            UsedItems: usedItems,
            SemanticReviewNotes: semanticReviewNotes
                .Where(static note => !string.IsNullOrWhiteSpace(note))
                .Select(static note => note.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumSemanticReviewNotes)
                .ToArray());
    }

    private static SourceBackedConversationActionMemory ToActionMemory(RetrievalRequest request)
    {
        var actionKey = string.Join(
            "|",
            new[]
            {
                request.ToolName,
                request.Query,
                request.CategoryPath,
                request.DocId,
                request.DocPath,
                request.ChunkId,
                request.PageStart?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.PageEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.Offset?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }.Select(NormalizeMechanicalIdentityPart));
        return new SourceBackedConversationActionMemory(
            actionKey,
            request.ToolName,
            request.Query,
            request.CategoryPath,
            request.DocId,
            request.DocPath,
            request.ChunkId,
            request.PageStart,
            request.PageEnd,
            request.Offset,
            request.Limit,
            request.Purpose);
    }

    private static SourceBackedConversationEvidenceMemory ToEvidenceMemory(
        EvidenceItem item,
        bool cited,
        bool semanticallyRejected,
        string language)
    {
        var stableKey = BuildStableEvidenceKey(item);
        var itemIdentity = TryGetCanonicalItemIdentity(item, stableKey);
        var displayLabel = TryGetCanonicalItemLabel(item, language);
        var decision = cited
            ? "used"
            : semanticallyRejected
                ? "rejected"
                : "observed";
        var reason = cited
            ? "cited_in_verified_answer"
            : semanticallyRejected
                ? "llm_semantic_rejection"
                : "observed_not_used";
        return new SourceBackedConversationEvidenceMemory(
            stableKey,
            item.EvidenceId,
            decision,
            reason,
            item.SourceKind,
            itemIdentity,
            displayLabel,
            item.DocId,
            item.DocPath,
            item.SourceHash,
            item.RevisionId,
            item.ChunkId,
            item.PageStart,
            item.PageEnd);
    }

    private static string BuildStableEvidenceKey(EvidenceItem item)
        => string.Join(
            "|",
            new[]
            {
                item.SourceHash,
                item.RevisionId,
                item.DocId,
                item.DocPath,
                item.ChunkId,
                item.AnchorId,
                item.ContentCardId,
                item.PageStart?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.PageEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.SourceKind
            }.Select(NormalizeMechanicalIdentityPart));

    private static string? TryGetCanonicalItemIdentity(
        EvidenceItem item,
        string stableEvidenceKey)
    {
        if (string.Equals(
                item.SourceKind,
                "canonical_content_card",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(item.ContentCardId))
                return "content-card:" + item.ContentCardId.Trim();
            if (item.SelectionHints.TryGetValue("contentCardId", out var cardId)
                && !string.IsNullOrWhiteSpace(cardId))
            {
                return "content-card:" + cardId.Trim();
            }

            return null;
        }

        var hasDocumentIdentity = !string.IsNullOrWhiteSpace(item.SourceHash)
                                  || !string.IsNullOrWhiteSpace(item.RevisionId)
                                  || !string.IsNullOrWhiteSpace(item.DocId)
                                  || !string.IsNullOrWhiteSpace(item.DocPath);
        var hasEvidenceAnchor = !string.IsNullOrWhiteSpace(item.ChunkId)
                                || !string.IsNullOrWhiteSpace(item.AnchorId)
                                || item.PageStart is > 0;
        return hasDocumentIdentity
               && hasEvidenceAnchor
               && !string.IsNullOrWhiteSpace(stableEvidenceKey)
            ? "evidence:" + stableEvidenceKey
            : null;
    }

    private static string? TryGetCanonicalItemLabel(
        EvidenceItem item,
        string language)
    {
        if (string.Equals(
                item.SourceKind,
                "canonical_content_card",
                StringComparison.OrdinalIgnoreCase))
        {
            return TryGetContentCardTitle(item.MatchedContentCards);
        }

        var documentLabel = !string.IsNullOrWhiteSpace(item.DocName)
            ? item.DocName.Trim()
            : !string.IsNullOrWhiteSpace(item.DocPath)
                ? Path.GetFileName(item.DocPath.Replace('\\', '/')).Trim()
                : item.DocId?.Trim();
        if (string.IsNullOrWhiteSpace(documentLabel))
            return null;

        if (item.PageStart is not > 0)
            return documentLabel;

        var pagePrefix = LocalizedPagePrefix(language);
        return item.PageEnd is > 0 && item.PageEnd != item.PageStart
            ? $"{documentLabel} {pagePrefix}{item.PageStart}-{item.PageEnd}"
            : $"{documentLabel} {pagePrefix}{item.PageStart}";
    }

    private static string LocalizedPagePrefix(string language)
        => string.Equals(
            (language ?? string.Empty).Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries).FirstOrDefault(),
            "de",
            StringComparison.OrdinalIgnoreCase)
            ? "S."
            : "p.";

    private static string? TryGetContentCardTitle(JsonElement? matchedContentCards)
    {
        if (matchedContentCards is not { } cards
            || cards.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in card.EnumerateObject())
            {
                if (string.Equals(property.Name, "title", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString()!.Trim();
                }
            }
        }

        return null;
    }

    private static string NormalizeMechanicalIdentityPart(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                    ' ',
                    value.Trim().Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();
}
