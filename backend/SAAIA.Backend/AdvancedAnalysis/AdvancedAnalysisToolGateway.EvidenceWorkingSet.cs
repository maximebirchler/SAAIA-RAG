using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class AdvancedAnalysisToolGateway
{
    private bool _evidenceRotationEnabled;
    private HashSet<string> _protectedEvidenceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _batchEvidenceIds = new(StringComparer.Ordinal);

    public void BeginEvidenceBatch(IReadOnlyCollection<string> protectedEvidenceIds)
    {
        var current = _evidence.Select(item => item.Reference.EvidenceId).ToHashSet(StringComparer.Ordinal);
        if (protectedEvidenceIds.Any(id => string.IsNullOrWhiteSpace(id) || !current.Contains(id)))
            throw new AdvancedAnalysisToolException("evidence_retention_reference_invalid");
        _protectedEvidenceIds = protectedEvidenceIds.ToHashSet(StringComparer.Ordinal);
        _batchEvidenceIds.Clear();
        _evidenceRotationEnabled = true;
    }

    private static string WorkingSetSourceKey(AdvancedAnalysisResolvedEvidence item)
        => string.Join("|", item.Reference.DocId, item.Reference.RevisionId,
            item.Reference.SourceHash, item.Reference.DocPath).ToLowerInvariant();

    private HashSet<string> ProtectedWorkingSetEvidenceIds()
    {
        var protectedIds = _protectedEvidenceIds.Concat(_batchEvidenceIds).ToHashSet(StringComparer.Ordinal);
        // A source handle must remain backed by an admitted canonical item.
        foreach (var source in _evidence.GroupBy(WorkingSetSourceKey, StringComparer.Ordinal))
            if (!source.Any(item => protectedIds.Contains(item.Reference.EvidenceId)))
                protectedIds.Add(source.First().Reference.EvidenceId);
        return protectedIds;
    }

    private int CountReplaceableEvidenceItems()
    {
        var protectedIds = ProtectedWorkingSetEvidenceIds();
        return _evidence.Count(item => !protectedIds.Contains(item.Reference.EvidenceId));
    }

    // Called only after canonical resolution and identity validation. No slicing
    // or partial admission is allowed, including for native and table RAW reads.
    internal IReadOnlyList<string> AdmitRevalidatedEvidence(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> resolvedEvidence)
    {
        var unique = resolvedEvidence.GroupBy(item => CanonicalKey(item.Reference), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var incomingKeys = unique.Select(item => CanonicalKey(item.Reference)).ToHashSet(StringComparer.Ordinal);
        var knownIds = _evidence.ToDictionary(item => item.Reference.EvidenceId,
            item => CanonicalKey(item.Reference), StringComparer.Ordinal);
        foreach (var item in unique)
            if (knownIds.TryGetValue(item.Reference.EvidenceId, out var existingKey)
                && existingKey != CanonicalKey(item.Reference))
                throw new AdvancedAnalysisToolException("evidence_admission_identity_changed");
        if (unique.Select(item => item.Reference.EvidenceId).Distinct(StringComparer.Ordinal).Count() != unique.Length)
            throw new AdvancedAnalysisToolException("evidence_admission_identity_changed");
        var additions = unique.Where(item => !_evidenceByCanonicalKey.ContainsKey(CanonicalKey(item.Reference))).ToArray();
        var needed = Math.Max(0, _evidence.Count + additions.Length - _maximumEvidenceItems);
        var protectedIds = ProtectedWorkingSetEvidenceIds();
        var removable = _evidenceRotationEnabled
            ? _evidence.Where(item => !protectedIds.Contains(item.Reference.EvidenceId)
                && !incomingKeys.Contains(CanonicalKey(item.Reference))).Take(needed).ToArray()
            : [];
        if (removable.Length < needed)
            throw new AdvancedAnalysisToolException("accumulated_evidence_limit_exceeded",
                new("accumulated_evidence_limit_exceeded", _maximumEvidenceItems,
                    _evidence.Count, additions.Length, StopsResearch: !_evidenceRotationEnabled));
        // Plan the entire transition before changing any retained proof.
        foreach (var item in removable)
        {
            _evidence.Remove(item);
            _evidenceByCanonicalKey.Remove(CanonicalKey(item.Reference));
        }
        foreach (var item in additions)
        {
            _evidence.Add(item);
            _evidenceByCanonicalKey.Add(CanonicalKey(item.Reference), item);
        }
        if (_evidenceRotationEnabled)
            foreach (var item in unique)
                _batchEvidenceIds.Add(_evidenceByCanonicalKey[CanonicalKey(item.Reference)].Reference.EvidenceId);
        return removable.Select(item => item.Reference.EvidenceId).ToArray();
    }

    private AdvancedAnalysisEvidenceWorkingSet? CaptureEvidenceWorkingSet()
        => !_evidenceRotationEnabled ? null : new(AdvancedAnalysisEvidenceWorkingSet.CurrentSchemaVersion,
            _maximumEvidenceItems, _evidence.Select(item => item.Reference).ToArray());

}
