using System.Diagnostics;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record IngestionStructureRequest(Guid TenantId, Guid DocumentId, int Version,
    string SourceHash, long SourceBytes, string FileName, DocumentIntelligenceOptions Options,
    DoclingConvertResponse? Conversion, PdfExtractionResult? NativeTextLayer, PdfExtractionResult Extraction);

internal sealed record IngestionStructureResult(CanonicalDocument? CanonicalDocument,
    NativeTextCoverageReconciliationSummary? NativeTextReconciliation,
    NativePdfImageInventorySummary? NativeImageInventory,
    IReadOnlyList<ExtractedDocumentSection> Sections, IReadOnlyList<ExtractedDocumentUnit> Units,
    long CanonicalMilliseconds, long SectionMilliseconds, long UnitMilliseconds)
{
    internal PdfExtractionResult? CanonicalExtraction { get; init; }
    internal NativeTableCoverageSummary? NativeTableCoverage { get; init; }
}

internal sealed record IngestionIndexProjection(IReadOnlyList<ExtractedExactMatchEntry> ExactEntries,
    IReadOnlyList<ProjectedContextualTextEntry> ContextualEntries, long ExactMilliseconds, long ContextualMilliseconds);

/// <summary>Owns structure and index projections; independent from leases, transport and worker lifecycle.</summary>
internal static class IngestionProjectionStage
{
    internal static IngestionStructureResult ProjectStructure(IngestionStructureRequest request, ILogger logger,
        CancellationToken ct = default)
    {
        using var probe = BackendDiagnostics.Start("ingestion.project_structure", logger);
        try
        {
            CanonicalDocument? canonical = null;
            NativeTextCoverageReconciliationSummary? reconciliation = null;
            NativePdfImageInventorySummary? images = null;
            NativeTableCoverageSummary? tableCoverage = null;
            long canonicalMilliseconds = 0;
            if (request.Conversion?.Document.JsonContent is { } document)
            {
                var started = Stopwatch.GetTimestamp();
                canonical = DoclingCanonicalDocumentAdapter.Project(new(request.DocumentId,
                    DocumentFoundationRepo.BuildStableRevisionId(request.TenantId, request.DocumentId, request.Version),
                    request.SourceHash, request.SourceBytes, request.FileName, request.SourceHash, "canonical_projection",
                    DoclingExecutionMetadataPolicy.DescribeEngine(request.Options, request.Conversion),
                    request.Options.DeploymentRevision, request.Conversion.Confidence?.MeanScore), document);
                tableCoverage = CanonicalNativeTableCoverageAuditor.Apply(canonical, request.NativeTextLayer, ct);
                reconciliation = CanonicalNativeTextCoverageReconciler.Apply(canonical, request.NativeTextLayer,
                    request.Options.NativeTextCoverageReconciliationEnabled, request.Options.NativeTextCoverageMinimumLineCoverage);
                images = CanonicalNativePdfImageInventoryReconciler.Apply(canonical, request.NativeTextLayer,
                    request.Options.NativePdfImageInventoryEnabled);
                DoclingCanonicalVisualAssetProjector.LinkPageAssets(canonical);
                canonicalMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                probe.Set("native_recovered_lines", reconciliation.RecoveredLineCount);
                probe.Set("native_recovered_blocks", reconciliation.RecoveredBlockCount);
                probe.Set("native_recovered_characters", reconciliation.RecoveredCharacterCount);
                probe.Set("native_audited_pages", reconciliation.AuditedPageCount);
                probe.Set("native_skipped_low_quality_pages", reconciliation.SkippedLowQualityPageCount);
                probe.Set("native_added_figures", images.AddedFigureCount);
                probe.Set("retained_visual_assets", canonical.Assets.Count);
                probe.Set("retained_visual_bytes", canonical.Assets.Sum(asset => asset.SizeBytes));
                probe.Set("figures_with_pixels", canonical.Pages.SelectMany(page => page.Figures).Count(figure => figure.AssetIds.Count > 0));
                probe.Set("figures_without_pixels", canonical.Pages.SelectMany(page => page.Figures).Count(figure => figure.AssetIds.Count == 0));
                probe.Set("native_table_audited_tables", tableCoverage.AuditedTableCount);
                probe.Set("native_table_unresolved_tables", tableCoverage.UnresolvedTableCount);
                probe.Set("native_table_unavailable_tables", tableCoverage.UnavailableTableCount);
                probe.Set("native_table_refused_tables", tableCoverage.RefusedTableCount);
                probe.Set("native_table_known_unrepresented_words", tableCoverage.ObservedUnrepresentedWordCount);
                probe.Set("native_table_unrepresented_words_available", tableCoverage.UnrepresentedWordCount.HasValue);
                if (tableCoverage.UnrepresentedWordCount is { } knownWords)
                    probe.Set("native_table_unrepresented_words", knownWords);
                probe.Set("native_table_coverage_ms", tableCoverage.DurationMs);
            }
            var sectionStarted = Stopwatch.GetTimestamp();
            var sections = canonical is null ? DocumentSectionExtractor.Extract(request.Extraction.Pages)
                : CanonicalDocumentSectionProjector.Project(canonical);
            var sectionMilliseconds = (long)Stopwatch.GetElapsedTime(sectionStarted).TotalMilliseconds;
            var unitStarted = Stopwatch.GetTimestamp();
            var canonicalExtraction = canonical is null ? null : CanonicalExtractionViewProjector.Project(canonical, request.Extraction.Pages, request.Conversion!.Document.JsonContent!);
            var units = canonical is null ? DocumentUnitExtractor.Extract(request.Extraction.Pages, sections)
                : CanonicalExtractionViewProjector.ProjectUnits(canonical, request.Conversion!.Document.JsonContent!);
            var unitMilliseconds = (long)Stopwatch.GetElapsedTime(unitStarted).TotalMilliseconds;
            var effectivePages = canonicalExtraction?.Pages ?? request.Extraction.Pages;
            probe.Set("pages", effectivePages.Count);
            if (reconciliation is not null)
                probe.Set("native_audit_available", reconciliation.Availability == "available");
            probe.Set("sections", sections.Count);
            probe.Set("units", units.Count);
            probe.Set("page_text_characters", effectivePages.Sum(page => (long)page.Text.Length));
            probe.Set("input_page_text_characters", request.Extraction.Pages.Sum(page => (long)page.Text.Length));
            probe.Set("unit_text_characters", units.Sum(unit => (long)unit.Text.Length));
            probe.Set("canonical_ms", canonicalMilliseconds);
            probe.Set("section_ms", sectionMilliseconds);
            probe.Set("unit_ms", unitMilliseconds);
            probe.Complete();
            return new(canonical, reconciliation, images, sections, units,
                canonicalMilliseconds, sectionMilliseconds, unitMilliseconds)
                { CanonicalExtraction = canonicalExtraction, NativeTableCoverage = tableCoverage };
        }
        catch (Exception exception) { probe.Fail(exception); throw; }
    }

    internal static IngestionIndexProjection ProjectIndex(string relativePath,
        IReadOnlyList<ExtractedDocumentSection> sections, IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> chunks, IReadOnlyList<ExtractedDocumentUnit>? profileUnits, ILogger logger)
    {
        using var probe = BackendDiagnostics.Start("ingestion.project_index", logger);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var exact = ExactMatchEntryExtractor.Extract(profileUnits ?? units);
            var exactMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            var contextual = ContextualTextProjector.Project(relativePath, sections, units, chunks);
            var contextualMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            probe.Set("exact_entries", exact.Count);
            probe.Set("contextual_entries", contextual.Count);
            probe.Set("input_units", units.Count);
            probe.Set("input_retrieval_chunks", chunks.Count);
            var publishedChunks = chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();
            probe.Set("published_retrieval_chunks", publishedChunks.Length);
            RecordSourceUnitCoverage(probe, "projected", units, chunks);
            RecordSourceUnitCoverage(probe, "published", units, publishedChunks);
            probe.Complete();
            return new(exact, contextual, exactMilliseconds, contextualMilliseconds);
        }
        catch (Exception exception) { probe.Fail(exception); throw; }
    }

    private static void RecordSourceUnitCoverage(BackendDiagnostics.Operation probe, string projection,
        IReadOnlyList<ExtractedDocumentUnit> units, IReadOnlyList<ProjectedRetrievalChunk> chunks)
    {
        var sourceOrdinals = units.Select(unit => unit.Ordinal).ToHashSet();
        var referencedOrdinals = chunks.SelectMany(chunk => chunk.SourceUnitOrdinals ?? []).ToHashSet();
        // Rich/legacy chunks may not carry native unit provenance. Never
        // report zero coverage when that relationship is simply unknown.
        // An empty chunk set, however, has known zero represented units.
        var coverageAvailable = sourceOrdinals.Count == units.Count
            && chunks.All(chunk => chunk.SourceUnitOrdinals is { Count: > 0 })
            && referencedOrdinals.IsSubsetOf(sourceOrdinals);
        probe.Set(projection + "_source_unit_coverage_available", coverageAvailable);
        if (coverageAvailable)
        {
            probe.Set(projection + "_represented_source_units", referencedOrdinals.Count);
            probe.Set(projection + "_unrepresented_source_units", units.Count - referencedOrdinals.Count);
            probe.Set(projection + "_source_unit_characters", units.Sum(unit => (long)unit.Text.Length));
            probe.Set(projection + "_represented_source_unit_characters", units
                .Where(unit => referencedOrdinals.Contains(unit.Ordinal)).Sum(unit => (long)unit.Text.Length));
        }
    }
}
