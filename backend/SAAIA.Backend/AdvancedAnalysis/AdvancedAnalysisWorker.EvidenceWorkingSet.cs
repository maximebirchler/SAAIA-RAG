using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class AdvancedAnalysisWorker
{
    internal static IReadOnlyList<AdvancedAnalysisEvidenceReference>
        BuildResumeEvidenceReferences(
            IReadOnlyList<AdvancedAnalysisEvidenceReference> handoffEvidence,
            IReadOnlyList<AdvancedAnalysisToolEventSummary> toolEvents,
            int maximumEvidenceItems = 2_000)
    {
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        static void Reject() => throw new InvalidDataException(
            "Advanced analysis evidence working set is invalid.");
        var sequence = 0;
        foreach (var item in toolEvents)
        {
            if (item is null || item.Request is null || item.Evidence is null
                || item.EventSequence <= sequence)
                Reject();
            sequence = item.EventSequence;
        }
        if (!toolEvents.Any(item => item.Status == "succeeded"
                                   && item.Request.EvidenceWorkingSet is not null))
            return BuildLegacyResumeEvidenceReferences(handoffEvidence, toolEvents);

        var maximum = Math.Clamp(maximumEvidenceItems, 1, 2_000);
        var known = new Dictionary<string, AdvancedAnalysisResultEvidence>(StringComparer.Ordinal);
        var active = new Dictionary<string, AdvancedAnalysisResultEvidence>(StringComparer.Ordinal);
        IReadOnlyList<AdvancedAnalysisResultEvidence>? latest = null;
        foreach (var item in toolEvents)
        {
            // Failed calls are audit records, never evidence admissions or replacements.
            if (item.Status != "succeeded")
                continue;
            foreach (var reference in item.Evidence)
            {
                ValidateWorkingSetReference(reference);
                if (known.TryGetValue(reference.EvidenceId, out var previous)
                    && !SameWorkingSetReference(previous, reference))
                    Reject();
                known[reference.EvidenceId] = reference;
            }
            var snapshot = item.Request.EvidenceWorkingSet;
            if (snapshot is null)
            {
                if (latest is not null)
                    Reject();
                continue;
            }
            if (snapshot.SchemaVersion != AdvancedAnalysisEvidenceWorkingSet.CurrentSchemaVersion
                || snapshot.MaximumEvidenceItems != maximum || snapshot.Evidence is null
                || snapshot.Evidence.Count > maximum)
                Reject();

            var admittedNow = item.Evidence.Select(reference => reference.EvidenceId)
                .ToHashSet(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var canonicalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, AdvancedAnalysisResultEvidence>(StringComparer.Ordinal);
            foreach (var reference in snapshot.Evidence)
            {
                ValidateWorkingSetReference(reference);
                if (!ids.Add(reference.EvidenceId) || !canonicalKeys.Add(WorkingSetCanonicalKey(reference)))
                    Reject();
                if (handoffEvidence.Any(initial => HandoffTargetsWorkingSetReference(initial, reference)
                    && !MatchesWorkingSetHandoff(initial, reference)))
                    Reject();
                if (latest is not null && !active.ContainsKey(reference.EvidenceId)
                    && !admittedNow.Contains(reference.EvidenceId))
                    Reject();
                if (known.TryGetValue(reference.EvidenceId, out var previous))
                {
                    if (!SameWorkingSetReference(previous, reference))
                        Reject();
                }
                else
                {
                    // The first persisted snapshot also contains initially resolved
                    // handoff locators, which may not yet have an evidence ID.
                    if (latest is not null || !handoffEvidence.Any(initial =>
                            MatchesWorkingSetHandoff(initial, reference)))
                        Reject();
                    known.Add(reference.EvidenceId, reference);
                }
                next.Add(reference.EvidenceId, reference);
            }
            active = next;
            latest = snapshot.Evidence;
        }
        return latest!.Select(ToResumeReference).ToArray();
    }

    private static void ValidateWorkingSetReference(AdvancedAnalysisResultEvidence reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.EvidenceId)
            || reference.EvidenceId.Length > 200 || reference.EvidenceId != reference.EvidenceId.Trim()
            || !Guid.TryParse(reference.DocId, out var document) || reference.DocId != document.ToString("D")
            || !Guid.TryParse(reference.RevisionId, out var revision) || reference.RevisionId != revision.ToString("D")
            || reference.SourceHash is not { Length: 64 } || !reference.SourceHash.All(Uri.IsHexDigit)
            || reference.SourceHash != reference.SourceHash.ToLowerInvariant()
            || string.IsNullOrWhiteSpace(reference.DocPath) || string.IsNullOrWhiteSpace(reference.FileName)
            || reference.PageStart < 1 || reference.PageEnd < reference.PageStart)
            throw new InvalidDataException("Advanced analysis evidence working set reference is invalid.");
    }

    private static bool SameWorkingSetReference(AdvancedAnalysisResultEvidence first,
        AdvancedAnalysisResultEvidence second)
        => JsonSerializer.Serialize(first, JsonOptions) == JsonSerializer.Serialize(second, JsonOptions);

    private static string WorkingSetCanonicalKey(AdvancedAnalysisResultEvidence reference)
        => reference.NativeObservation is { } native
            ? string.Join("|", "native", reference.DocId, reference.RevisionId, reference.SourceHash, native.LineId)
            : string.Join("|", reference.DocId, reference.RevisionId, reference.ChunkId ?? string.Empty,
                reference.AnchorId ?? string.Empty, reference.ContentCardId ?? string.Empty,
                reference.PageStart, reference.PageEnd, reference.SourceHash);

    private static bool MatchesWorkingSetHandoff(AdvancedAnalysisEvidenceReference initial,
        AdvancedAnalysisResultEvidence reference)
    {
        static bool Matches(string? expected, string? actual) => string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
        static bool MatchesGuid(string? expected, string actual) => string.IsNullOrWhiteSpace(expected)
            || Guid.TryParse(expected, out var parsed) && Guid.TryParse(actual, out var actualGuid)
                && parsed == actualGuid;
        var pageWindow = string.IsNullOrWhiteSpace(initial.ChunkId)
            && string.IsNullOrWhiteSpace(initial.AnchorId) && string.IsNullOrWhiteSpace(initial.ContentCardId)
            && Guid.TryParse(initial.RevisionId, out _) && initial.PageStart > 0
            && initial.PageEnd >= initial.PageStart;
        if ((initial.NativeObservation is null) != (reference.NativeObservation is null)
            || pageWindow && (reference.NativeObservation is not null
                || !string.IsNullOrWhiteSpace(reference.ChunkId) || !string.IsNullOrWhiteSpace(reference.AnchorId)
                || !string.IsNullOrWhiteSpace(reference.ContentCardId)))
            return false;
        if (initial.NativeObservation is null && string.IsNullOrWhiteSpace(initial.ChunkId)
            && string.IsNullOrWhiteSpace(initial.AnchorId) && string.IsNullOrWhiteSpace(initial.ContentCardId)
            && !pageWindow)
            return false;
        if (!MatchesGuid(initial.DocId, reference.DocId) || !MatchesGuid(initial.RevisionId, reference.RevisionId)
            || !Matches(initial.FileName, reference.FileName) || !Matches(initial.DocPath, reference.DocPath)
            || !Matches(initial.SourceHash?.Replace("-", string.Empty, StringComparison.Ordinal), reference.SourceHash)
            || !Matches(initial.ChunkId, reference.ChunkId)
            || !Matches(initial.AnchorId, reference.AnchorId) || !Matches(initial.ContentCardId, reference.ContentCardId)
            || (pageWindow ? reference.PageStart > initial.PageStart || reference.PageEnd < initial.PageEnd
                : initial.PageStart > 0 && initial.PageStart != reference.PageStart
                  || initial.PageEnd > 0 && initial.PageEnd != reference.PageEnd)
            || initial.NativeObservation is not null && JsonSerializer.Serialize(initial.NativeObservation, JsonOptions)
                != JsonSerializer.Serialize(reference.NativeObservation, JsonOptions))
            return false;
        if (!string.IsNullOrWhiteSpace(initial.EvidenceId))
            return initial.EvidenceId.Trim() == reference.EvidenceId;
        if (reference.NativeObservation is { } native)
            return native.LineId.StartsWith("native_line_", StringComparison.Ordinal)
                && reference.EvidenceId == "advanced-native-evidence-" + native.LineId["native_line_".Length..];
        var identity = string.Join("|", reference.DocId, reference.RevisionId, reference.ChunkId ?? string.Empty,
            reference.AnchorId ?? string.Empty, reference.ContentCardId ?? string.Empty,
            reference.PageStart, reference.PageEnd, reference.SourceHash);
        return reference.EvidenceId == "advanced-evidence-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
    }

    private static bool HandoffTargetsWorkingSetReference(AdvancedAnalysisEvidenceReference initial,
        AdvancedAnalysisResultEvidence reference)
    {
        static bool PresentAndEqual(string? expected, string? actual)
            => !string.IsNullOrWhiteSpace(expected)
                && string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
        return PresentAndEqual(initial.EvidenceId, reference.EvidenceId)
            || PresentAndEqual(initial.ChunkId, reference.ChunkId)
            || PresentAndEqual(initial.AnchorId, reference.AnchorId)
            || PresentAndEqual(initial.ContentCardId, reference.ContentCardId)
            || initial.NativeObservation is not null && reference.NativeObservation is not null
                && PresentAndEqual(initial.NativeObservation.LineId, reference.NativeObservation.LineId);
    }

    private static AdvancedAnalysisEvidenceReference ToResumeReference(AdvancedAnalysisResultEvidence reference)
        => new()
        {
            EvidenceId = reference.EvidenceId, DocId = reference.DocId, RevisionId = reference.RevisionId,
            FileName = reference.FileName, DocPath = reference.DocPath, SourceHash = reference.SourceHash,
            PageStart = reference.PageStart, PageEnd = reference.PageEnd, ChunkId = reference.ChunkId,
            AnchorId = reference.AnchorId, ContentCardId = reference.ContentCardId,
            NativeObservation = reference.NativeObservation
        };

    private static IReadOnlyList<AdvancedAnalysisEvidenceReference> BuildLegacyResumeEvidenceReferences(
        IReadOnlyList<AdvancedAnalysisEvidenceReference> handoffEvidence,
        IReadOnlyList<AdvancedAnalysisToolEventSummary> toolEvents)
    {
        var result = new List<AdvancedAnalysisEvidenceReference>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in handoffEvidence.Concat(
                     toolEvents
                         .Where(static item => item.Status == "succeeded")
                         .SelectMany(static item => item.Evidence)
                         .Select(static item => new AdvancedAnalysisEvidenceReference
                         {
                             EvidenceId = item.EvidenceId,
                             DocId = item.DocId,
                             RevisionId = item.RevisionId,
                             FileName = item.FileName,
                             DocPath = item.DocPath,
                             SourceHash = item.SourceHash,
                             PageStart = item.PageStart,
                             PageEnd = item.PageEnd,
                             ChunkId = item.ChunkId,
                             AnchorId = item.AnchorId,
                             ContentCardId = item.ContentCardId,
                             NativeObservation = item.NativeObservation
                         })))
        {
            var key = string.Join(
                "|",
                reference.DocId ?? string.Empty,
                reference.RevisionId ?? string.Empty,
                reference.ChunkId ?? string.Empty,
                reference.AnchorId ?? string.Empty,
                reference.ContentCardId ?? string.Empty,
                reference.PageStart,
                reference.PageEnd,
                reference.SourceHash ?? string.Empty,
                reference.NativeObservation?.LineId ?? string.Empty);
            if (keys.Add(key))
                result.Add(reference);
        }
        return result;
    }

}
