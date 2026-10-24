using System.Text.RegularExpressions;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Retrieval;

internal sealed class RagRetrievalDiagnosticsBuilder(
    string query,
    string retrievalQuery,
    string selectionQuery,
    string mode,
    string? category,
    string? categoryPath,
    int topK,
    int candidates,
    int maxPerDoc,
    int maxPerPage,
    bool includeContent = true)
{
    private const int MaxPhaseCount = 128;
    private const int MaxCandidatesPerPhase = 12;
    private const int MaxPhaseCandidateSamples = 192;
    private readonly List<RagRetrievalPhaseDiagnosticsDto> _phases = new();
    private readonly List<RagRetrievalRejectionDiagnosticsDto> _rejections = new();
    private readonly Dictionary<string, int> _rejectionCounts = new(StringComparer.Ordinal);
    private int _droppedPhases;
    private int _droppedRejections;
    private int _capturedPhaseCandidates;

    public void Reject(string phase, string reason, RagMatch match)
    {
        _rejectionCounts[reason] = _rejectionCounts.GetValueOrDefault(reason) + 1;
        if (_rejections.Count >= 96) { _droppedRejections++; return; }
        _rejections.Add(new(phase, reason, match.DocId, match.ChunkId, match.PageStart, match.PageEnd));
    }

    public void CapturePhase(string name, string retriever, long? durationMs, IReadOnlyList<RagMatch> matches)
    {
        if (_phases.Count >= MaxPhaseCount)
        {
            _droppedPhases++;
            return;
        }

        var limit = Math.Min(MaxCandidatesPerPhase, MaxPhaseCandidateSamples - _capturedPhaseCandidates);
        var sample = limit > 0 ? BuildCandidates(matches, limit) : Array.Empty<RagRetrievalCandidateDiagnosticsDto>();
        _capturedPhaseCandidates += sample.Count;
        _phases.Add(new RagRetrievalPhaseDiagnosticsDto(
            Name: name,
            Retriever: retriever,
            Returned: matches.Count,
            DurationMs: durationMs,
            TopCandidates: sample,
            OmittedCandidates: Math.Max(0, matches.Count - sample.Count)));
    }

    public RagRetrievalDiagnosticsDto Build(IReadOnlyList<RagMatch> selected)
        => new(
            Mode: mode,
            Query: includeContent ? query : string.Empty,
            RetrievalQuery: includeContent ? retrievalQuery : string.Empty,
            SelectionQuery: includeContent ? selectionQuery : string.Empty,
            Category: includeContent ? category : null,
            CategoryPath: includeContent ? categoryPath : null,
            TopK: topK,
            Candidates: candidates,
            MaxPerDoc: maxPerDoc,
            MaxPerPage: maxPerPage,
            Phases: _phases.ToArray(),
            Selection: new RagRetrievalSelectionDiagnosticsDto(
                Returned: selected.Count,
                TopK: topK,
                MaxPerDoc: maxPerDoc,
                MaxPerPage: maxPerPage,
                DuplicatePageOrDocPressure: ComputeDuplicatePageOrDocPressure(selected),
                Items: BuildCandidates(selected, Math.Min(MaxCandidatesPerPhase, Math.Max(topK, 1)))),
            CaptureMode: includeContent ? "content" : "metadata",
            DroppedPhases: _droppedPhases,
            Rejections: _rejections.ToArray(),
            DroppedRejections: _droppedRejections,
            RejectionCounts: new Dictionary<string, int>(_rejectionCounts, StringComparer.Ordinal),
            CandidateSampleBudget: MaxPhaseCandidateSamples,
            CapturedPhaseCandidates: _capturedPhaseCandidates);

    private IReadOnlyList<RagRetrievalCandidateDiagnosticsDto> BuildCandidates(IReadOnlyList<RagMatch> matches, int limit)
        => matches
            .Take(Math.Clamp(limit, 1, MaxCandidatesPerPhase))
            .Select((match, index) => new RagRetrievalCandidateDiagnosticsDto(
                Rank: index + 1,
                Score: Math.Round(match.Score, 6),
                RerankScore: match.RerankScore.HasValue ? Math.Round(match.RerankScore.Value, 6) : null,
                Retriever: RetrievalNavigation.ResolveRetriever(match),
                EmbeddingBasis: match.EmbeddingBasis,
                DocId: match.DocId,
                DocName: includeContent ? match.DocName : null,
                DocPath: includeContent ? match.DocPath : null,
                PageStart: match.PageStart,
                PageEnd: match.PageEnd,
                ChunkId: match.ChunkId,
                ChunkIndex: match.ChunkIndex,
                HeadingPath: includeContent ? TrimForDiagnostics(match.HeadingPath, 180) : null,
                SectionTitle: includeContent ? TrimForDiagnostics(match.SectionTitle, 160) : null,
                ContentRole: match.ContentRole,
                Snippet: includeContent ? TrimForDiagnostics(match.Text, 260) : null))
            .ToArray();

    private static int ComputeDuplicatePageOrDocPressure(IReadOnlyList<RagMatch> selected)
    {
        var distinctPages = selected
            .Select(static match =>
            {
                var doc = string.IsNullOrWhiteSpace(match.DocPath)
                    ? match.DocId ?? string.Empty
                    : match.DocPath.Trim().Replace('\\', '/');
                return $"{doc}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return Math.Max(0, selected.Count - distinctPages);
    }

    private static string? TrimForDiagnostics(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return collapsed.Length <= maxLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, maxLength), "...");
    }
}
