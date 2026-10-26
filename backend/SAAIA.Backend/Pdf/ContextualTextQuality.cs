internal sealed record ContextualTextQuality(
    string? TextStatus,
    bool? TextSparse,
    bool? OcrCandidate,
    IReadOnlyList<string>? Signals,
    string Availability)
{
    public static ContextualTextQuality Aggregate(
        ProjectedRetrievalChunk chunk,
        IReadOnlyList<ExtractedDocumentUnit> contributorUnits)
    {
        // The excerpt is always included. Only units whose text was actually
        // appended contribute here; source membership remains a separate field.
        var observations = new List<Observation>(contributorUnits.Count + 1)
        {
            Observe(chunk.ExtractionTextStatus, chunk.ExtractionTextSparse,
                chunk.ExtractionOcrCandidate, chunk.ExtractionQualitySignals)
        };
        observations.AddRange(contributorUnits.Select(unit => Observe(
            unit.ExtractionTextStatus, unit.ExtractionTextSparse,
            unit.ExtractionOcrCandidate, unit.ExtractionQualitySignals)));

        var anyObserved = observations.Any(item => item.HasDiagnostics);
        var complete = observations.All(item => item.Status is not null && item.Signals is not null);
        var availability = !anyObserved ? "unavailable" : complete ? "complete" : "mixed_unknown";
        var statuses = observations.Where(item => item.Status is not null)
            .Select(item => item.Status!).Distinct(StringComparer.Ordinal).ToArray();
        var status = !anyObserved ? null
            : observations.Any(item => item.Status is null) ? "mixed_unknown"
            : statuses.Length == 1 ? statuses[0] : "mixed";
        // Unknown signals stay null, while an explicitly observed empty list
        // stays empty. Neither case implies semantic accuracy of the source.
        var signals = observations.Any(item => item.Signals is not null)
            ? observations.SelectMany(item => item.Signals ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : null;
        return new ContextualTextQuality(status,
            AggregateBoolean(observations.Select(item => item.Sparse)),
            AggregateBoolean(observations.Select(item => item.OcrCandidate)), signals, availability);
    }

    private static Observation Observe(string? status, bool sparse, bool ocrCandidate,
        IReadOnlyList<string>? signals)
    {
        status = string.IsNullOrWhiteSpace(status) ? null : status;
        var hasCompleteMetadata = status is not null && signals is not null;
        // Legacy input records have non-nullable false defaults. They cannot
        // establish an observed negative with absent or partial diagnostics.
        return new Observation(status, sparse ? true : hasCompleteMetadata ? false : null,
            ocrCandidate ? true : hasCompleteMetadata ? false : null, signals);
    }

    private static bool? AggregateBoolean(IEnumerable<bool?> values)
    {
        var observed = values.ToArray();
        if (observed.Any(value => value == true))
            return true;
        return observed.All(value => value == false) ? false : null;
    }

    private sealed record Observation(string? Status, bool? Sparse, bool? OcrCandidate,
        IReadOnlyList<string>? Signals)
    {
        public bool HasDiagnostics => Status is not null || Signals is not null
            || Sparse == true || OcrCandidate == true;
    }
}
