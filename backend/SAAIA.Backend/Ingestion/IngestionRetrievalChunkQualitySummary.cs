internal sealed record IngestionRetrievalChunkQualitySummary(
    int TotalChunkCount,
    int SearchableChunkCount,
    int NavigationChunkCount,
    int SparseRejectedChunkCount,
    int ReplacementCharRejectedChunkCount,
    int EmptyTextRejectedChunkCount,
    int OcrNoiseRejectedChunkCount,
    int OtherRejectedChunkCount)
{
    public int RejectedChunkCount
        => Math.Max(0, TotalChunkCount - SearchableChunkCount);

    public int QualityRejectedChunkCount
        => SparseRejectedChunkCount
           + ReplacementCharRejectedChunkCount
           + EmptyTextRejectedChunkCount
           + OcrNoiseRejectedChunkCount
           + OtherRejectedChunkCount;

    public bool ManualReviewRecommended
        => TotalChunkCount == 0
           || SearchableChunkCount == 0
           || QualityRejectedChunkCount >= Math.Max(8, SearchableChunkCount * 3);
}
