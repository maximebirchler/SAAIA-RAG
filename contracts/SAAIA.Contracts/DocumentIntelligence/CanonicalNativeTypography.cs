using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

/// <summary>Source observations only; never a rewrite or proof of rendered typography.</summary>
public sealed class CanonicalNativeTypographySet
{
    public string SchemaVersion { get; set; } = "native_typography_observation_set_v1";
    public Guid DocumentId { get; set; }
    public Guid RevisionId { get; set; }
    public string SourceSha256 { get; set; } = "";
    public int PhysicalPage { get; set; }
    public string Algorithm { get; set; } = "pdfpig_native_typography_v1";
    public string PdfPigAssemblyVersion { get; set; } = "";
    public string PdfPigAssemblySha256 { get; set; } = "";
    public string SourceHandleSha256Before { get; set; } = "";
    public string SourceHandleSha256After { get; set; } = "";
    public CanonicalNativeTypographyFrame CoordinateFrame { get; set; } = new();
    public string ResourcesAvailability { get; set; } = "unavailable";
    public string? ResourcesReason { get; set; }
    public List<CanonicalNativeFontResource>? Resources { get; set; }
    public string RunAvailability { get; set; } = "unavailable";
    public string? RunReason { get; set; }
    public List<CanonicalNativeTypographyRun>? Runs { get; set; }
    public int? ObservedGlyphCount { get; set; }
    public int? TotalGlyphCount { get; set; }
    public string MappingAvailability { get; set; } = "not_attempted";
    public bool RenderedStyleVerified { get; set; }
    public bool SemanticRelationshipsAsserted { get; set; }
    public bool PrimaryTextModified { get; set; }
    public bool GlyphQuadsCaptured { get; set; }
}

public sealed class CanonicalNativeTypographyFrame
{
    public string Availability { get; set; } = "unavailable";
    public string RawOrigin { get; set; } = "BOTTOMLEFT";
    public double PageWidthPoints { get; set; }
    public double PageHeightPoints { get; set; }
    public int? ObservedRotationDegrees { get; set; }
    public double? ObservedCropBoxLeft { get; set; }
    public double? ObservedCropBoxBottom { get; set; }
    public double? ObservedMediaBoxLeft { get; set; }
    public double? ObservedMediaBoxBottom { get; set; }
}

public sealed class CanonicalNativeFontResource
{
    public string ResourceId { get; set; } = "";
    public string ResourceScopePath { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public int? FontObjectNumber { get; set; }
    public int? FontObjectGeneration { get; set; }
    public string? Subtype { get; set; }
    public string? BaseFont { get; set; }
    public string DescriptorAvailability { get; set; } = "absent";
    public string? DescriptorReason { get; set; }
    public int? DescriptorObjectNumber { get; set; }
    public int? DescriptorObjectGeneration { get; set; }
    public string? DescriptorName { get; set; }
    public string? FontFamily { get; set; }
    public int? Flags { get; set; }
    public double? ItalicAngle { get; set; }
    public double? FontWeight { get; set; }
    public string FontProgramDeclaration { get; set; } = "unavailable";
    public Dictionary<string, string> FieldAvailability { get; set; } = new(StringComparer.Ordinal);
    public string ResourceSnapshotSha256 { get; set; } = "";
}

public sealed class CanonicalNativeTypographyRun
{
    public string RunId { get; set; } = "";
    public int RunOrdinal { get; set; }
    public int SourceLetterOrdinalStart { get; set; }
    public int SourceLetterOrdinalEnd { get; set; }
    public string Text { get; set; } = "";
    public string TextSha256 { get; set; } = "";
    public List<CanonicalNativeTypographyGlyph> Glyphs { get; set; } = [];
    public string? ReportedFontName { get; set; }
    public string ReportedFontDetailsAvailability { get; set; } = "unavailable";
    public bool? ReportedBold { get; set; }
    public bool? ReportedItalic { get; set; }
    public int? ReportedWeight { get; set; }
    public double? ReportedFontSize { get; set; }
    public string RenderingMode { get; set; } = "";
    public int TextSequence { get; set; }
    public double? BaselineStartY { get; set; }
    public double? BaselineEndY { get; set; }
    public string GeometryMetricsAvailability { get; set; } = "unknown";
    public string GeometryMetricsMethod { get; set; } = "unverified";
    public string ResourceAssociation { get; set; } = "unavailable";
    public List<string>? CandidateResourceIds { get; set; }
}

[JsonConverter(typeof(CanonicalNativeTypographyGlyphJsonConverter))]
public sealed class CanonicalNativeTypographyGlyph
{
    public int NativeLetterOrdinal { get; set; }
    public int RunTextOffsetUtf16 { get; set; }
    public int RunTextLengthUtf16 { get; set; }
    public double? StartBaselineX { get; set; }
    public double? EndBaselineX { get; set; }
}

public static class CanonicalNativeTypographyIdentity
{
    public static string ResourceSnapshot(CanonicalNativeFontResource resource) => CanonicalContractJson.ComputeSha256(new
    {
        resource.ResourceScopePath, resource.ResourceName, resource.FontObjectNumber, resource.FontObjectGeneration,
        resource.Subtype, resource.BaseFont, resource.DescriptorAvailability, resource.DescriptorReason, resource.DescriptorObjectNumber,
        resource.DescriptorObjectGeneration, resource.DescriptorName, resource.FontFamily, resource.Flags,
        resource.ItalicAngle, resource.FontWeight, resource.FontProgramDeclaration, resource.FieldAvailability
    });

    public static string ResourceId(string source, int page, CanonicalNativeFontResource resource)
        => "native_font_" + Hash($"{source.ToLowerInvariant()}|{page}|{resource.ResourceSnapshotSha256}")[..32];

    public static string RunId(string source, int page, CanonicalNativeTypographyRun run)
        => "native_style_" + Hash($"{source.ToLowerInvariant()}|{page}|{run.RunOrdinal}|{run.SourceLetterOrdinalStart}|{run.SourceLetterOrdinalEnd}|{run.TextSha256}")[..32];

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
