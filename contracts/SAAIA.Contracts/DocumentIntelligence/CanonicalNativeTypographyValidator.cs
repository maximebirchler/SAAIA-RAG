using System.Text;

namespace SAAIA.Contracts.DocumentIntelligence;

public static class CanonicalNativeTypographyValidator
{
    public const int MaximumPageBytes = 2_000_000;
    public const int MaximumDocumentBytes = 8_000_000;
    public const int MaximumGlyphsPerPage = 20_000;
    public const int MaximumGlyphsPerDocument = 100_000;
    public const int MaximumRunsPerPage = 2_000;
    public const int MaximumRunsPerDocument = 10_000;
    public const int MaximumResourcesPerPage = 128;
    public const int MaximumResourcesPerDocument = 512;

    public static IReadOnlyList<CanonicalValidationIssue> Validate(CanonicalNativeTypographySet set)
    {
        var issues = new List<CanonicalValidationIssue>();
        void Check(bool condition, string code) { if (!condition) issues.Add(new("$.nativeTypographyObservations", code, "Native typography observation contract is inconsistent.")); }
        if (set.Resources?.Count > MaximumResourcesPerPage || set.Runs?.Count > MaximumRunsPerPage
            || (set.Runs?.Sum(run => (long)run.Glyphs.Count) ?? 0) > MaximumGlyphsPerPage
            || (set.Runs?.Any(run => run.Text.Length > MaximumGlyphsPerPage * 32) ?? false))
        { Check(false, "native_typography_limit"); return issues; }
        Check(set.SchemaVersion == "native_typography_observation_set_v1" && set.Algorithm == "pdfpig_native_typography_v1", "native_typography_schema");
        Check(set.DocumentId != Guid.Empty && set.RevisionId != Guid.Empty && set.PhysicalPage > 0, "native_typography_identity");
        Check(Hex(set.SourceSha256) && set.SourceSha256 == set.SourceHandleSha256Before
            && set.SourceSha256 == set.SourceHandleSha256After && Hex(set.PdfPigAssemblySha256)
            && set.PdfPigAssemblyVersion.Length is > 0 and <= 64, "native_typography_source_provenance");
        Check(!set.RenderedStyleVerified && !set.SemanticRelationshipsAsserted && !set.PrimaryTextModified && !set.GlyphQuadsCaptured
            && set.MappingAvailability == "not_attempted", "native_typography_unearned_qualification");
        var frame = set.CoordinateFrame;
        Check(frame.RawOrigin == "BOTTOMLEFT" && frame.Availability is "available" or "unavailable"
            && Positive(frame.PageWidthPoints) && Positive(frame.PageHeightPoints), "native_typography_frame");
        Check(new[] { frame.ObservedCropBoxLeft, frame.ObservedCropBoxBottom, frame.ObservedMediaBoxLeft, frame.ObservedMediaBoxBottom }
            .All(value => value is null || double.IsFinite(value.Value)), "native_typography_frame");
        if (frame.Availability == "available") Check(frame.ObservedRotationDegrees.HasValue
            && frame.ObservedCropBoxLeft.HasValue && frame.ObservedCropBoxBottom.HasValue
            && frame.ObservedMediaBoxLeft.HasValue && frame.ObservedMediaBoxBottom.HasValue, "native_typography_frame");
        Check(State(set.ResourcesAvailability, set.ResourcesReason, set.Resources), "native_typography_resource_state");
        Check(State(set.RunAvailability, set.RunReason, set.Runs), "native_typography_run_state");
        Check(set.Resources?.Count is null or <= MaximumResourcesPerPage && set.Runs?.Count is null or <= MaximumRunsPerPage, "native_typography_limit");
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in set.Resources ?? [])
        {
            Check(resourceIds.Add(resource.ResourceId) && resource.ResourceScopePath.Length is > 0 and <= 512
                && resource.ResourceName.Length is > 0 and <= 512, "native_typography_resource_identity");
            Check(Pair(resource.FontObjectNumber, resource.FontObjectGeneration) && Pair(resource.DescriptorObjectNumber, resource.DescriptorObjectGeneration), "native_typography_resource_reference");
            Check(resource.DescriptorAvailability is "observed" or "absent" or "unavailable"
                && resource.FontProgramDeclaration is "declared" or "absent" or "unavailable", "native_typography_descriptor_state");
            Check(resource.DescriptorReason is null or { Length: > 0 and <= 160 }
                && (resource.DescriptorAvailability == "unavailable" || resource.DescriptorReason is null), "native_typography_descriptor_state");
            if (resource.Subtype == "Type0") Check(resource.DescriptorAvailability == "unavailable"
                && resource.FontProgramDeclaration == "unavailable"
                && resource.DescriptorReason == "native_typography_composite_descendant_not_observed", "native_typography_unobserved_composite");
            Check(new[] { resource.BaseFont, resource.DescriptorName, resource.FontFamily, resource.Subtype }.All(value => value is null || value.Length <= 512)
                && (resource.ItalicAngle is null || double.IsFinite(resource.ItalicAngle.Value))
                && (resource.FontWeight is null || double.IsFinite(resource.FontWeight.Value)), "native_typography_descriptor_fields");
            Check(resource.FieldAvailability.Count <= 8 && resource.FieldAvailability.All(pair => pair.Key is "FontName" or "FontFamily" or "Flags" or "ItalicAngle" or "FontWeight"
                && pair.Value is "observed" or "absent" or "invalid_type" or "unavailable"), "native_typography_descriptor_fields");
            if (issues.Count > 0) return issues;
            Check(resource.ResourceSnapshotSha256 == CanonicalNativeTypographyIdentity.ResourceSnapshot(resource)
                && resource.ResourceId == CanonicalNativeTypographyIdentity.ResourceId(set.SourceSha256, set.PhysicalPage, resource), "native_typography_resource_hash");
            foreach (var pair in resource.FieldAvailability)
            {
                var present = pair.Key switch { "FontName" => resource.DescriptorName is not null, "FontFamily" => resource.FontFamily is not null,
                    "Flags" => resource.Flags.HasValue, "ItalicAngle" => resource.ItalicAngle.HasValue, "FontWeight" => resource.FontWeight.HasValue, _ => false };
                Check(present == (pair.Value == "observed"), "native_typography_descriptor_presence");
            }
        }
        var ordinal = 0; var runOrdinal = 0; var runIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in set.Runs ?? [])
        {
            Check(run.RunOrdinal == runOrdinal++ && runIds.Add(run.RunId) && run.Glyphs.Count > 0
                && run.SourceLetterOrdinalStart == ordinal && run.SourceLetterOrdinalEnd == ordinal + run.Glyphs.Count - 1, "native_typography_run_sequence");
            Check(run.TextSha256 == CanonicalNativeTypographyIdentity.Hash(run.Text)
                && run.RunId == CanonicalNativeTypographyIdentity.RunId(set.SourceSha256, set.PhysicalPage, run), "native_typography_run_hash");
            Check(run.ReportedFontName is null || run.ReportedFontName.Length <= 512, "native_typography_reported_style");
            Check(run.ReportedFontDetailsAvailability is "observed" or "unavailable" && run.RenderingMode.Length is > 0 and <= 64
                && (run.ReportedFontSize is null || Positive(run.ReportedFontSize.Value)), "native_typography_reported_style");
            Check(Coordinate(run.BaselineStartY) && Coordinate(run.BaselineEndY)
                && run.GeometryMetricsAvailability is "available" or "unavailable" or "unknown"
                && run.GeometryMetricsMethod.Length is > 0 and <= 64, "native_typography_glyph_geometry");
            if (run.ReportedFontDetailsAvailability == "observed") Check(run.ReportedBold.HasValue && run.ReportedItalic.HasValue && run.ReportedWeight.HasValue, "native_typography_reported_style");
            var candidates = run.CandidateResourceIds;
            Check(run.ResourceAssociation switch
            {
                "unavailable" => candidates is null,
                "unique_name_candidate" => candidates is { Count: 1 },
                "ambiguous" => candidates is { Count: > 1 },
                _ => false
            }, "native_typography_resource_association");
            Check(candidates is null || candidates.Count <= MaximumResourcesPerPage
                && candidates.Distinct(StringComparer.Ordinal).Count() == candidates.Count && candidates.All(resourceIds.Contains), "native_typography_resource_association");
            var expectedCandidates = (set.Resources ?? []).Where(resource => run.ReportedFontName is not null
                    && (resource.BaseFont == run.ReportedFontName || resource.DescriptorName == run.ReportedFontName))
                .Select(resource => resource.ResourceId).Order(StringComparer.Ordinal).ToArray();
            Check(expectedCandidates.SequenceEqual((candidates ?? []).Order(StringComparer.Ordinal)), "native_typography_resource_candidates");
            var offset = 0;
            foreach (var glyph in run.Glyphs)
            {
                Check(glyph.NativeLetterOrdinal == ordinal++ && glyph.RunTextOffsetUtf16 == offset
                    && glyph.RunTextLengthUtf16 is >= 0 and <= 32, "native_typography_glyph_sequence");
                offset += glyph.RunTextLengthUtf16;
                Check(Coordinate(glyph.StartBaselineX) && Coordinate(glyph.EndBaselineX), "native_typography_glyph_geometry");
            }
            Check(offset == run.Text.Length, "native_typography_literal_offsets");
        }
        Check(ordinal <= MaximumGlyphsPerPage, "native_typography_limit");
        if (set.RunAvailability == "observed") Check(set.ObservedGlyphCount == ordinal && set.TotalGlyphCount == ordinal, "native_typography_glyph_count");
        else Check(set.ObservedGlyphCount is null && set.TotalGlyphCount is null, "native_typography_unknown_count");
        Check(Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(set)) <= MaximumPageBytes, "native_typography_limit");
        return issues;
    }

    public static void ValidateOrThrow(CanonicalNativeTypographySet set)
    { var issues = Validate(set); if (issues.Count > 0) throw new CanonicalContractException(issues); }

    internal static IEnumerable<CanonicalValidationIssue> ValidateDocument(CanonicalDocument document)
    {
        long bytes = 0; long glyphs = 0; long runs = 0; long resources = 0;
        var channelPresent = document.Pages.Any(page => page.NativeTypographyObservations is not null
            || page.QualityFlags.Any(flag => flag is "native_typography_document_budget_refused" or "native_typography_source_observation_unavailable"));
        foreach (var page in document.Pages)
        {
            if (page.NativeTypographyObservations is not { } set)
            {
                if (channelPresent && !page.QualityFlags.Any(flag => flag is "native_typography_document_budget_refused" or "native_typography_source_observation_unavailable"))
                    yield return new("$.pages", "native_typography_missing_channel_state", "Missing native typography payload requires an explicit unavailable/refused state.");
                continue;
            }
            foreach (var issue in Validate(set)) yield return issue;
            if (set.DocumentId != document.DocumentId || set.RevisionId != document.RevisionId
                || set.SourceSha256 != document.Source.Sha256 || set.PhysicalPage != page.PageNumber)
                yield return new("$.pages", "native_typography_document_identity", "Native typography source identity does not match its document/page.");
            bytes += Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(set));
            glyphs += set.ObservedGlyphCount ?? 0; runs += set.Runs?.Count ?? 0; resources += set.Resources?.Count ?? 0;
        }
        if (bytes > MaximumDocumentBytes || glyphs > MaximumGlyphsPerDocument || runs > MaximumRunsPerDocument || resources > MaximumResourcesPerDocument)
            yield return new("$.pages", "native_typography_document_limit", "Native typography document budget exceeded.");
    }

    private static bool State<T>(string status, string? reason, List<T>? values)
        => status == "observed" ? values is not null && reason is null
            : status is "unavailable" or "refused" && values is null && reason is { Length: > 0 and <= 160 };
    private static bool Hex(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Pair(int? number, int? generation) => number is null && generation is null || number > 0 && generation is >= 0;
    private static bool Coordinate(double? value) => value is null || double.IsFinite(value.Value) && Math.Abs(value.Value) <= 10_000_000;
}
