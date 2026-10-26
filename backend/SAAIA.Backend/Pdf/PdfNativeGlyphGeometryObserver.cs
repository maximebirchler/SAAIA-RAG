using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Fonts.SystemFonts;

internal sealed record PdfNativeGlyphFontObservation(string FontName, string SourceSubtype,
    string Embedding, string MetricsMethod, string Availability);

internal sealed record PdfNativeGlyphGeometryObservation(string Availability, string? Reason,
    int Standard14FontCount, int DescriptorFallbackFontCount, int ExternalFontCount,
    int UnknownFontCount, IReadOnlyList<PdfNativeGlyphFontObservation> Fonts)
{
    internal static PdfNativeGlyphGeometryObservation Unknown(string reason)
        => new("unknown", reason, 0, 0, 0, 0, []);
}

/// <summary>
/// Observes provenance only. In particular, a missing TrueType program can make
/// PdfPig use the global descriptor bbox as each glyph's bbox. The original
/// letters, words, coordinates and font resolver are never replaced here.
/// </summary>
internal static class PdfNativeGlyphGeometryObserver
{
    internal const string Algorithm = "pdfpig_font_resource_metrics_provenance_v1";
    private const int MaximumUsedFonts = 128;

    internal static PdfNativeGlyphGeometryObservation Observe(Page page, Structure? structure, CancellationToken ct)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var letter in page.Letters)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(letter.Value)) continue;
                var name = letter.GetFont()?.Name?.Data;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 512) return PdfNativeGlyphGeometryObservation.Unknown("native_glyph_font_identity_unavailable");
                names.Add(name);
                if (names.Count > MaximumUsedFonts) return PdfNativeGlyphGeometryObservation.Unknown("native_glyph_used_font_limit");
            }
            if (names.Count == 0) return PdfNativeGlyphGeometryObservation.Unknown("native_glyph_fonts_not_observed");
            var inventory = PdfNativeGlyphGeometryResources.Observe(page, structure, ct);
            if (inventory.Reason is { } resourceReason) return PdfNativeGlyphGeometryObservation.Unknown(resourceReason);
            var observations = new List<PdfNativeGlyphFontObservation>();
            var standard = 0; var fallback = 0; var external = 0; var unknown = 0;
            string? firstUnknown = null;
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var candidates = inventory.Fonts.Where(font => font.Names.Contains(name, StringComparer.Ordinal)).ToArray();
                if (candidates.Length != 1)
                {
                    unknown++; firstUnknown ??= candidates.Length > 1 ? "native_glyph_font_identity_ambiguous" : "native_glyph_font_resource_unavailable";
                    observations.Add(new(name, "unknown", "unknown", "unknown", "unknown")); continue;
                }
                var source = candidates[0];
                if (source.Subtype == "Type1" && !source.HasDescriptor && Standard14.IsFontInStandard14(source.BaseFont))
                {
                    standard++; observations.Add(new(name, source.Subtype, "absent", "standard14_afm", "available")); continue;
                }
                if (source.Subtype != "TrueType" || !source.HasDescriptor)
                {
                    unknown++; firstUnknown ??= "native_glyph_font_type_unverified";
                    observations.Add(new(name, source.Subtype, source.Embedding, "unknown", "unknown")); continue;
                }
                if (source.Embedding != "absent")
                {
                    // Public IFont has no parsed-program/source provenance.
                    // A declaration alone cannot prove a program parsed successfully.
                    unknown++; firstUnknown ??= "native_glyph_embedded_program_unverified";
                    observations.Add(new(name, source.Subtype, source.Embedding, "unknown", "unknown")); continue;
                }
                if (!source.HasTypedSimpleMetrics)
                {
                    unknown++; firstUnknown ??= "native_glyph_font_metrics_branch_unverified";
                    observations.Add(new(name, source.Subtype, "absent", "unknown", "unknown")); continue;
                }
                if (string.IsNullOrEmpty(source.DescriptorName))
                {
                    unknown++; firstUnknown ??= "native_glyph_font_identity_unavailable";
                    observations.Add(new(name, source.Subtype, "absent", "unknown", "unknown")); continue;
                }
                // Same public resolver/name used by PdfPig0.1.13's handler for
                // a nonembedded TrueType. No private-field reflection or alias guessing.
                var program = SystemFontFinder.Instance.GetTrueTypeFont(source.DescriptorName);
                ct.ThrowIfCancellationRequested();
                if (program is null)
                {
                    fallback++; observations.Add(new(name, source.Subtype, "absent", "font_descriptor_bbox", "unavailable"));
                }
                else
                {
                    external++; observations.Add(new(name, source.Subtype, "absent", "system_or_substitute_program", "available"));
                }
            }
            // An ambiguous/unsupported source is not upgraded by another resolved font.
            return new(unknown > 0 ? "unknown" : fallback > 0 ? "unavailable" : "available",
                firstUnknown ?? (fallback > 0 ? "native_glyph_metrics_unresolved" : external > 0 ? "native_glyph_metrics_external_program" : null),
                standard, fallback, external, unknown, observations);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Do not log a font path, source name or private parser exception.
            return PdfNativeGlyphGeometryObservation.Unknown("native_glyph_geometry_observation_failed");
        }
    }
}
