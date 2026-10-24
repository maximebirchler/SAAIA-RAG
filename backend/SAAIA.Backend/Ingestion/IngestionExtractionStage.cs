using System.Diagnostics;
using SAAIA.Backend;

internal sealed record IngestionExtractionResult(PdfExtractionResult Extraction,
    PdfExtractionResult? NativeTextLayer, DoclingConvertResponse? Conversion,
    bool UsesDocling, bool ForceOcr, long NativeTextExtractionMilliseconds);

/// <summary>Owns source extraction and the optional native audit; the worker supplies lease-aware conversion.</summary>
internal static class IngestionExtractionStage
{
    internal delegate Task<DoclingConvertResponse> ConvertDocument(bool forceOcr, CancellationToken cancellationToken);

    internal static async Task<IngestionExtractionResult> ExtractAsync(string absolutePath,
        DocumentIntelligenceOptions options, ConvertDocument convert, ILogger logger, CancellationToken cancellationToken)
    {
        using var probe = BackendDiagnostics.Start("ingestion.source_extraction", logger);
        try
        {
            var usesDocling = DocumentExtractionPolicy.ResolveUseDocling(options);
            PdfExtractionResult? native = null;
            long nativeMilliseconds = 0;
            if (usesDocling && (options.NativeTextCoverageReconciliationEnabled || options.NativePdfImageInventoryEnabled))
            {
                var started = Stopwatch.GetTimestamp();
                using var nativeProbe = BackendDiagnostics.Start("ingestion.native_text_audit", logger);
                try
                {
                    native = PdfExtractor.Extract(absolutePath, cancellationToken);
                    nativeProbe.Set("pages", native.Pages.Count);
                    nativeProbe.Set("tokens", native.Tokens.Count);
                    nativeProbe.Complete();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { nativeProbe.Complete("cancelled"); throw; }
                catch (Exception exception)
                {
                    nativeProbe.Complete("degraded", "native_text_audit_failed");
                    logger.LogWarning("Native text coverage audit unavailable error_type={ErrorType}", exception.GetType().Name);
                }
                finally { nativeMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }
            var forceOcr = usesDocling && DocumentExtractionPolicy.ResolveDoclingForceOcr(options, native);
            DoclingConvertResponse? conversion = null;
            PdfExtractionResult extraction;
            if (usesDocling)
            {
                conversion = await convert(forceOcr, cancellationToken).ConfigureAwait(false);
                extraction = DoclingLegacyExtractionAdapter.Project(conversion);
            }
            else extraction = PdfExtractor.Extract(absolutePath, cancellationToken);
            probe.Set("engine", usesDocling ? "docling" : "native");
            probe.Set("forced_ocr", forceOcr);
            probe.Set("pages", extraction.Pages.Count);
            probe.Set("tokens", extraction.Tokens.Count);
            probe.Complete();
            return new(extraction, native, conversion, usesDocling, forceOcr, nativeMilliseconds);
        }
        catch (Exception exception) { probe.Fail(exception); throw; }
    }
}
