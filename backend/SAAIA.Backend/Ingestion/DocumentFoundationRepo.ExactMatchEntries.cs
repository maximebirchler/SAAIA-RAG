using Dapper;
using Npgsql;

internal static partial class DocumentFoundationRepo
{
    private static async Task UpsertExactMatchEntriesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        int ingestionVersion,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
        CancellationToken ct)
    {
        if (exactMatchEntries.Count == 0)
            return;

        var sectionIdsByOrdinal = sections.ToDictionary(
            section => section.Ordinal,
            section => BuildStableSectionId(revisionId, section.Ordinal));
        var unitIdsByOrdinal = units.ToDictionary(
            unit => unit.Ordinal,
            unit => BuildStableUnitId(revisionId, unit.Ordinal));
        var chunksByIndex = retrievalChunks.ToDictionary(chunk => chunk.ChunkIndex);

        const string sql = @"
INSERT INTO exact_match_entries(
    exact_match_entry_id,
    tenant_id,
    revision_id,
    section_id,
    unit_id,
    entry_index,
    page_start,
    page_end,
    text_content,
    normalized_text,
    char_count,
    token_count,
    checksum,
    metadata)
VALUES(
    @exact_match_entry_id,
    @tenant_id,
    @revision_id,
    @section_id,
    @unit_id,
    @entry_index,
    @page_start,
    @page_end,
    @text_content,
    @normalized_text,
    @char_count,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, entry_index) DO UPDATE
SET section_id = EXCLUDED.section_id,
    unit_id = EXCLUDED.unit_id,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    text_content = EXCLUDED.text_content,
    normalized_text = EXCLUDED.normalized_text,
    char_count = EXCLUDED.char_count,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata;";

        foreach (var entry in exactMatchEntries)
        {
            sectionIdsByOrdinal.TryGetValue(entry.SectionOrdinal ?? -1, out var sectionIdValue);
            Guid? sectionId = sectionIdValue == Guid.Empty ? null : sectionIdValue;
            Guid? unitId = null;
            string? sourceRetrievalChunkId = null;
            string sourceKind;
            if (entry.SourceRetrievalChunkIndex is { } chunkIndex)
            {
                if (entry.UnitOrdinal is not null || !chunksByIndex.TryGetValue(chunkIndex, out var sourceChunk)
                    || !IngestionWorker.ShouldPublishRetrievalChunk(sourceChunk)
                    || (sourceChunk.CanonicalBlockIds?.Count ?? 0) + (sourceChunk.CanonicalSpanIds?.Count ?? 0)
                        + (sourceChunk.CanonicalTableCellIds?.Count ?? 0) == 0)
                    throw new InvalidDataException("canonical_exact_source_chunk_invalid");
                if (string.IsNullOrEmpty(entry.Text)
                    || !DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(sourceChunk).Contains(entry.Text, StringComparison.Ordinal)
                    || entry.PageStart < sourceChunk.PageStart || entry.PageEnd > sourceChunk.PageEnd)
                    throw new InvalidDataException("canonical_exact_source_text_absent");
                sourceRetrievalChunkId = BuildStableRetrievalChunkId(docId, ingestionVersion, chunkIndex).ToString("D");
                sourceKind = "canonical_retrieval_chunk_v1";
            }
            else
            {
                if (entry.UnitOrdinal is not { } unitOrdinal || !unitIdsByOrdinal.TryGetValue(unitOrdinal, out var unitIdValue))
                    throw new InvalidDataException("exact_source_unit_missing");
                unitId = unitIdValue;
                sourceKind = "document_unit_v1";
            }
            var text = NormalizePostgresTextForStorage(entry.Text);
            var normalizedText = NormalizePostgresTextForStorage(entry.NormalizedText);

            var metadata = SerializePostgresJsonForStorage(new
            {
                inferred = true,
                sourceKind,
                sourceRetrievalChunkId,
                kind = NormalizePostgresTextForStorage(entry.Kind),
                offsetStart = entry.OffsetStart,
                offsetEnd = entry.OffsetEnd,
                extractionTextStatus = NormalizeOptionalPostgresTextForStorage(entry.ExtractionTextStatus),
                extractionTextSparse = entry.ExtractionTextSparse,
                extractionOcrCandidate = entry.ExtractionOcrCandidate,
                extractionQualitySignals = NormalizePostgresTextArrayForStorage(entry.ExtractionQualitySignals ?? Array.Empty<string>())
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                exact_match_entry_id = BuildStableExactMatchEntryId(revisionId, entry.EntryIndex),
                tenant_id = tenantId,
                revision_id = revisionId,
                section_id = sectionId,
                unit_id = unitId,
                entry_index = entry.EntryIndex,
                page_start = entry.PageStart,
                page_end = entry.PageEnd,
                text_content = text,
                normalized_text = normalizedText,
                char_count = text.Length,
                token_count = CountTokens(text),
                checksum = ComputeStoredTextChecksum(normalizedText),
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

}
