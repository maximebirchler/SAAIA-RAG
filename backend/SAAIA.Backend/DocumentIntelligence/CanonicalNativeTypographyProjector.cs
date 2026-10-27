using System.Diagnostics;
using System.Globalization;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record NativeTypographyProjectionSummary(int PageCount, int ObservedPages, int RefusedPages,
    int UnavailablePages, int OmittedBudgetPages, int GlyphCount, int RunCount, int ResourceCount,
    long ObserverMilliseconds, string? EngineVersion, string? EngineSha256);

/// <summary>Copies auxiliary source observations; never edits a span, cell, text or reading order.</summary>
internal static class CanonicalNativeTypographyProjector
{
    internal const string BudgetRefusedFlag = "native_typography_document_budget_refused";
    internal const string UnavailableFlag = "native_typography_source_observation_unavailable";
    internal static NativeTypographyProjectionSummary Apply(CanonicalDocument document, PdfExtractionResult? source,
        CancellationToken ct = default, long maximumBytes = CanonicalNativeTypographyValidator.MaximumDocumentBytes)
    {
        ct.ThrowIfCancellationRequested();
        var byPage = source?.Pages.GroupBy(page => page.PageNumber).ToDictionary(group => group.Key, group => group.ToArray());
        var observed = 0; var refused = 0; var unavailable = 0; var omitted = 0; var glyphs = 0; var runs = 0; var resources = 0;
        long elapsedTicks = 0; string? version = null; string? assembly = null;
        maximumBytes = Math.Clamp(maximumBytes, 0, CanonicalNativeTypographyValidator.MaximumDocumentBytes);
        foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
        {
            ct.ThrowIfCancellationRequested();
            page.NativeTypographyObservations = null;
            page.QualityFlags.RemoveAll(flag => flag is BudgetRefusedFlag or UnavailableFlag);
            var native = byPage is not null && byPage.TryGetValue(page.PageNumber, out var values) && values.Length == 1
                ? values[0].NativeTypographyObservation : null;
            if (native is null || native.Observation.SourceSha256 != document.Source.Sha256
                || native.Observation.PhysicalPage != page.PageNumber)
            { unavailable++; page.QualityFlags.Add(UnavailableFlag); continue; }
            elapsedTicks += native.ObserverTicks;
            version ??= native.Observation.PdfPigAssemblyVersion; assembly ??= native.Observation.PdfPigAssemblySha256;
            var set = CanonicalContractJson.Deserialize<CanonicalNativeTypographySet>(CanonicalContractJson.Serialize(native.Observation));
            set.DocumentId = document.DocumentId; set.RevisionId = document.RevisionId;
            if (CanonicalNativeTypographyValidator.Validate(set).Count != 0)
            { unavailable++; page.QualityFlags.Add(UnavailableFlag); continue; }
            var bytes = Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(set));
            if (bytes > maximumBytes)
            {
                set.Resources = null; set.ResourcesAvailability = "refused"; set.ResourcesReason = "native_typography_document_byte_limit";
                set.Runs = null; set.RunAvailability = "refused"; set.RunReason = "native_typography_document_byte_limit";
                set.ObservedGlyphCount = set.TotalGlyphCount = null;
                bytes = Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(set));
            }
            if (bytes > maximumBytes)
            {
                refused++; omitted++; page.QualityFlags.Add(BudgetRefusedFlag); continue;
            }
            maximumBytes -= bytes; page.NativeTypographyObservations = set;
            if (set.RunAvailability == "observed") observed++;
            else if (set.RunAvailability == "refused") refused++; else unavailable++;
            glyphs += set.ObservedGlyphCount ?? 0; runs += set.Runs?.Count ?? 0; resources += set.Resources?.Count ?? 0;
        }
        return new(document.PageCount, observed, refused, unavailable, omitted, glyphs, runs, resources,
            (long)(elapsedTicks * 1000d / Stopwatch.Frequency), version, assembly);
    }

    internal static IngestionStageManifest CreateStage(NativeTypographyProjectionSummary? summary)
        => new()
        {
            StageId = "native_pdf_typography_observation", StageType = "source_observation", Engine = "PdfPig",
            EngineVersion = summary?.EngineVersion ?? "unattested", DeviceId = "cpu:0", Concurrency = 1,
            DurationMs = summary?.ObserverMilliseconds,
            OptionsSha256 = CanonicalContractJson.ComputeSha256(new { enabled = true, algorithm = "pdfpig_native_typography_v1",
                glyphsPerPage = CanonicalNativeTypographyValidator.MaximumGlyphsPerPage, glyphsPerDocument = CanonicalNativeTypographyValidator.MaximumGlyphsPerDocument,
                pageBytes = CanonicalNativeTypographyValidator.MaximumPageBytes, documentBytes = CanonicalNativeTypographyValidator.MaximumDocumentBytes,
                observerMilliseconds = 5000, mapping = "not_attempted", budgetOmission = BudgetRefusedFlag }),
            Attributes = new(StringComparer.Ordinal)
            {
                ["enabled"] = "true", ["qualification"] = "declared_and_reported_not_render_verified",
                ["mappingAvailability"] = "not_attempted", ["engineAssemblySha256"] = summary?.EngineSha256 ?? "unattested",
                ["countsAvailability"] = summary is null ? "unavailable" : "observed",
                ["countScope"] = "retained_observations_not_unknown_source_totals",
                ["pageCount"] = Count(summary?.PageCount), ["observedPages"] = Count(summary?.ObservedPages),
                ["refusedPages"] = Count(summary?.RefusedPages), ["unavailablePages"] = Count(summary?.UnavailablePages),
                ["omittedBudgetPages"] = Count(summary?.OmittedBudgetPages), ["glyphCount"] = Count(summary?.GlyphCount),
                ["runCount"] = Count(summary?.RunCount), ["resourceCount"] = Count(summary?.ResourceCount)
            }
        };

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
}
