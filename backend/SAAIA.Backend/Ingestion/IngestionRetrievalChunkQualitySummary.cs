internal sealed record IngestionRetrievalChunkQualitySummary(
    int TotalChunkCount,
    int SearchableChunkCount,
    int NavigationChunkCount,
    int SparseRejectedChunkCount,
    int ReplacementCharRejectedChunkCount,
    int EmptyTextRejectedChunkCount,
    int OtherRejectedChunkCount)
{
    public int RejectedChunkCount
        => Math.Max(0, TotalChunkCount - SearchableChunkCount);

    public int QualityRejectedChunkCount
        => SparseRejectedChunkCount
           + ReplacementCharRejectedChunkCount
           + EmptyTextRejectedChunkCount
           + OtherRejectedChunkCount;

    public bool ManualReviewRecommended
        => TotalChunkCount == 0 || SearchableChunkCount == 0;
}
