using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig.Content;

/// <summary>Persisted diagnostic inventory, never an eligibility decision for a table region.</summary>
internal static class PdfNativeVectorDiagnosticsStage
{
    internal const int MaximumPageDiagnostics = 128;
    internal const int MaximumPageDiagnosticsBytes = 65_536;
    private static readonly Lazy<string> AssemblySha = new(() =>
    {
        using var stream = File.OpenRead(typeof(Page).Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    });

    internal static IngestionStageManifest? Create(PdfExtractionResult? extraction)
    {
        var pages = extraction?.Pages.Select(page => page.NativeVectorObservation)
            .OfType<PdfNativeVectorObservation>().ToArray();
        if (pages is not { Length: > 0 }) return null;
        var inspected = pages.Where(page => page.Diagnostics?.PrimitivesInspected is not null).ToArray();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["countsAvailability"] = inspected.Length == 0 ? "unavailable" : inspected.Length == pages.Length ? "observed" : "partial",
            ["countScope"] = "inspected_source_primitives_not_painted_visibility_or_region_eligibility",
            ["pageCount"] = Count(pages.Length), ["pagesWithInspectedCounts"] = Count(inspected.Length),
            ["pagesWithCompleteInspection"] = Count(pages.Count(page => page.Diagnostics?.InspectionCoverage == "complete")),
            ["primitivesEntered"] = Sum(inspected, value => value.PrimitivesEntered),
            ["primitivesInspected"] = Sum(inspected, value => value.PrimitivesInspected),
            ["supportedPrimitives"] = Sum(inspected, value => value.SupportedPrimitives),
            ["rulesEmittedBeforeStatus"] = Sum(inspected, value => value.RulesEmittedBeforeStatus),
            ["rulesRetained"] = Count(pages.Sum(page => (long)page.Rules.Count)),
            ["paintVisibility"] = "unknown", ["effectiveClip"] = "unknown",
            ["sampleScope"] = "first_rejection_events_per_page_not_exhaustive_primitive_inventory",
            ["geometryBoundsAvailability"] = "not_captured",
            ["engineAssemblySha256"] = AssemblySha.Value
        };
        foreach (var code in PdfNativeVectorDiagnosticCollector.RejectionCodes)
            attributes["rejection_" + code] = inspected.Length == 0 ? "unavailable" : Count(inspected.Sum(page =>
                (long)(page.Diagnostics!.RejectionCounts?.GetValueOrDefault(code) ?? 0)));

        // Whole page summaries only. Never truncate a JSON value or hide inventory omissions.
        var retained = new List<object>();
        var samplesRemaining = 32;
        foreach (var page in pages.OrderBy(page => page.Availability == "observed" ? 1 : 0).ThenBy(page => page.PhysicalPage))
        {
            if (retained.Count == MaximumPageDiagnostics) break;
            var diagnostic = page.Diagnostics;
            var samples = diagnostic?.Samples.Take(samplesRemaining).ToArray() ?? [];
            var item = new { page.PhysicalPage, page.SourceSha256, page.Availability, page.Reason,
                diagnostic?.InspectionCoverage, diagnostic?.PrimitivesEntered, diagnostic?.PrimitivesInspected,
                diagnostic?.SupportedPrimitives, diagnostic?.RulesEmittedBeforeStatus, diagnostic?.RejectionCounts,
                samples, samplesOmitted = diagnostic is null ? (int?)null : diagnostic.SamplesOmitted + diagnostic.Samples.Count - samples.Length };
            retained.Add(item);
            if (Encoding.UTF8.GetByteCount(CanonicalContractJson.Serialize(retained)) > MaximumPageDiagnosticsBytes)
            { retained.RemoveAt(retained.Count - 1); break; }
            samplesRemaining -= samples.Length;
        }
        attributes["pageDiagnosticsJson"] = CanonicalContractJson.Serialize(retained);
        attributes["pageDiagnosticsOmitted"] = Count(pages.Length - retained.Count);
        attributes["pageDiagnosticsInventoryComplete"] = retained.Count == pages.Length ? "true" : "false";
        return new()
        {
            StageId = "native_pdf_vector_observation", StageType = "source_observation", Engine = "PdfPig",
            EngineVersion = typeof(Page).Assembly.GetName().Version?.ToString() ?? "unattested",
            DeviceId = "cpu:0", Concurrency = 1,
            OptionsSha256 = CanonicalContractJson.ComputeSha256(new
            {
                algorithm = "pdfpig_native_vector_geometry_v1", diagnostics = "pdfpig_native_vector_diagnostics_v1",
                pathsPerPage = PdfNativeVectorGeometryObserver.MaximumPathsPerPage,
                commandsPerPage = PdfNativeVectorGeometryObserver.MaximumCommandsPerPage,
                commandsPerDocument = PdfNativeVectorGeometryObserver.MaximumCommandsPerDocument,
                samplesPerPage = PdfNativeVectorDiagnosticCollector.MaximumSamples,
                pageDiagnostics = MaximumPageDiagnostics, pageDiagnosticsBytes = MaximumPageDiagnosticsBytes,
                stageSamples = 32, geometryBounds = "not_captured", regionEligibility = "unchanged"
            }),
            Attributes = attributes
        };
    }

    internal static void RecordDiagnostics(BackendDiagnostics.Operation operation, PdfExtractionResult extraction)
    {
        var diagnostics = extraction.Pages.Select(page => page.NativeVectorObservation?.Diagnostics)
            .OfType<PdfNativeVectorDiagnostics>().Where(value => value.PrimitivesInspected.HasValue).ToArray();
        operation.Set("vector_diagnostic_pages_with_counts", diagnostics.Length);
        operation.Set("vector_diagnostic_counts_availability", diagnostics.Length == 0 ? "unavailable" : "observed_work_only");
        operation.Set("vector_primitives_inspected", diagnostics.Length == 0 ? null : diagnostics.Sum(value => (long?)value.PrimitivesInspected));
        foreach (var code in PdfNativeVectorDiagnosticCollector.RejectionCodes)
            operation.Set("vector_rejection_" + code, diagnostics.Length == 0 ? null : diagnostics.Sum(value =>
                (long?)(value.RejectionCounts?.GetValueOrDefault(code) ?? 0)));
        operation.Set("vector_paint_visibility", "unknown");
        operation.Set("vector_effective_clip", "unknown");
    }

    private static string Sum(IReadOnlyList<PdfNativeVectorObservation> pages, Func<PdfNativeVectorDiagnostics, int?> select)
        => pages.Count == 0 ? "unavailable" : Count(pages.Sum(page => (long)(select(page.Diagnostics!) ?? 0)));
    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);
}
