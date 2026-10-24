namespace SAAIA.Client.Core.Execution;

public sealed record EvidenceAdequacyReview<TCompletion>(bool ProtocolValid, string Decision, string Reason,
    TCompletion? RoutedCompletion, IReadOnlyList<string>? TournamentSelectionIds) where TCompletion : class;
public sealed record EvidenceAdequacyCheckpointResult<TCompletion>(bool Evaluated,
    EvidenceAdequacyReview<TCompletion>? Review, bool RequiresFeedback) where TCompletion : class;

/// <summary>One run's checkpoint state. Invalid protocol output cannot replace the last valid selection or gap decision.</summary>
public sealed class EvidenceAdequacyCheckpoint<TCompletion> where TCompletion : class
{
    private string _lastSignature = string.Empty;
    public IReadOnlyList<string> IncumbentIds { get; private set; } = Array.Empty<string>();
    public HashSet<string> ReviewedEvidenceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool GapActive { get; private set; }

    public async Task<EvidenceAdequacyCheckpointResult<TCompletion>> EvaluateAsync(
        IReadOnlyList<string> candidateIds,
        Func<EvidenceAdequacyCheckpoint<TCompletion>, CancellationToken, Task<EvidenceAdequacyReview<TCompletion>>> review,
        CancellationToken cancellationToken)
    {
        var signature = string.Join(',', candidateIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        if (signature.Length == 0 || string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            return new(false, null, false);
        _lastSignature = signature;
        var result = await review(this, cancellationToken).ConfigureAwait(false);
        if (result.ProtocolValid)
        {
            if (result.TournamentSelectionIds is not null)
                IncumbentIds = Array.AsReadOnly(result.TournamentSelectionIds.ToArray());
            GapActive = result.Decision is "continue" or "expand_context";
        }
        return new(true, result, result.RoutedCompletion is null && (result.ProtocolValid || !GapActive));
    }
}
