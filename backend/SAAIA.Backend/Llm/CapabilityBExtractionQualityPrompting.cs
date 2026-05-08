using System.Globalization;
using System.Text;

namespace SAAIA.Backend;

internal static class CapabilityBExtractionQualityPrompting
{
    internal static string BuildPromptBlock(CapabilityBExtractionQualitySnapshot? quality)
    {
        if (quality is null)
            return "Extraction/OCR quality: no revision quality snapshot was available.";

        var confidence = quality.ExtractionConfidence.HasValue
            ? quality.ExtractionConfidence.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "unknown";
        var pageStats = quality.PageCount is > 0
            ? $"pages={quality.PageCount}; textPages={FormatNullable(quality.TextPageCount)}; emptyPages={FormatNullable(quality.EmptyPageCount)}; sparsePages={FormatNullable(quality.SparsePageCount)}; textPageRatio={FormatNullable(quality.TextPageRatio)}"
            : "unknown";
        var signals = quality.Signals.Count == 0
            ? "none"
            : string.Join(", ", quality.Signals.Take(12));

        var sb = new StringBuilder();
        sb.AppendLine("Extraction/OCR quality:");
        sb.AppendLine($"- Source quality status: {quality.Status}");
        sb.AppendLine($"- Text status: {quality.TextStatus}");
        sb.AppendLine($"- Extraction confidence: {confidence}");
        sb.AppendLine($"- Manual review recommended: {quality.ManualReviewRecommended.ToString().ToLowerInvariant()}");
        sb.AppendLine($"- OCR attempted/applied/recommended: {quality.OcrAttempted.ToString().ToLowerInvariant()}/{quality.OcrApplied.ToString().ToLowerInvariant()}/{quality.OcrRecommended.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(quality.OcrFailureReason))
            sb.AppendLine($"- OCR failure reason: {quality.OcrFailureReason}");
        sb.AppendLine($"- Page stats: {pageStats}");
        sb.AppendLine($"- Signals: {signals}");
        if (quality.RequiresCaution)
        {
            sb.AppendLine("- Instruction: Treat available section titles and excerpts as incomplete or uncertain; do not treat them as a complete normal source.");
        }
        else
        {
            sb.AppendLine("- Instruction: No quality caveat is required by this snapshot.");
        }

        return sb.ToString().TrimEnd();
    }

    internal static Dictionary<string, object?>? BuildMeta(
        CapabilityBExtractionQualitySnapshot? quality,
        string? outputLanguage)
    {
        if (quality is null)
            return null;

        return new Dictionary<string, object?>
        {
            ["status"] = quality.Status,
            ["textStatus"] = quality.TextStatus,
            ["extractionConfidence"] = quality.ExtractionConfidence,
            ["manualReviewRecommended"] = quality.ManualReviewRecommended,
            ["requiresCaution"] = quality.RequiresCaution,
            ["ocrAttempted"] = quality.OcrAttempted,
            ["ocrApplied"] = quality.OcrApplied,
            ["ocrRecommended"] = quality.OcrRecommended,
            ["ocrFailureReason"] = quality.OcrFailureReason,
            ["pageCount"] = quality.PageCount,
            ["textPageCount"] = quality.TextPageCount,
            ["emptyPageCount"] = quality.EmptyPageCount,
            ["sparsePageCount"] = quality.SparsePageCount,
            ["totalWordCount"] = quality.TotalWordCount,
            ["totalCharCount"] = quality.TotalCharCount,
            ["textPageRatio"] = quality.TextPageRatio,
            ["signals"] = quality.Signals.ToArray(),
            ["source"] = quality.Source,
            ["caveat"] = BuildCaveat(quality, outputLanguage)
        };
    }

    internal static string ApplySummaryCaveat(
        string summaryText,
        CapabilityBExtractionQualitySnapshot? quality,
        string? outputLanguage)
    {
        var caveat = BuildCaveat(quality, outputLanguage);
        if (string.IsNullOrWhiteSpace(caveat) || ContainsQualityCaveat(summaryText))
            return summaryText;

        return string.IsNullOrWhiteSpace(summaryText)
            ? caveat
            : summaryText.TrimEnd() + Environment.NewLine + caveat;
    }

    internal static IReadOnlyList<string> BuildProfileLimits(
        CapabilityBExtractionQualitySnapshot? quality,
        string? outputLanguage)
    {
        var caveat = BuildCaveat(quality, outputLanguage);
        return string.IsNullOrWhiteSpace(caveat) ? [] : [caveat];
    }

    private static string? BuildCaveat(CapabilityBExtractionQualitySnapshot? quality, string? outputLanguage)
    {
        if (quality?.RequiresCaution != true)
            return null;

        return DocumentLanguageResolver.PrimarySubtag(outputLanguage) switch
        {
            "fr" => "Reserve source : le texte extrait peut etre incomplet ou incertain car la qualite OCR/extraction demande verification.",
            "es" => "Nota sobre la fuente: el texto extraido puede estar incompleto o ser incierto porque OCR/extraccion requiere revision.",
            "pt" => "Nota sobre a fonte: o texto extraido pode estar incompleto ou incerto porque OCR/extracao requer revisao.",
            "de" => "Quellenhinweis: Der extrahierte Text kann unvollstaendig oder unsicher sein, weil OCR/Extraktion Pruefung erfordert.",
            "it" => "Nota sulla fonte: il testo estratto puo essere incompleto o incerto perche OCR/estrazione richiede verifica.",
            "nl" => "Bronkwaliteit: de geextraheerde tekst kan onvolledig of onzeker zijn omdat OCR/extractie controle vereist.",
            "en" => "Source quality note: the extracted text may be incomplete or uncertain because OCR/extraction requires review.",
            _ => "source_quality_requires_review: extracted text may be incomplete or uncertain because OCR/extraction requires review."
        };
    }

    private static bool ContainsQualityCaveat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return (value.Contains("OCR", StringComparison.OrdinalIgnoreCase)
                || value.Contains("extraction", StringComparison.OrdinalIgnoreCase)
                || value.Contains("extraccion", StringComparison.OrdinalIgnoreCase)
                || value.Contains("extracao", StringComparison.OrdinalIgnoreCase)
                || value.Contains("extraktion", StringComparison.OrdinalIgnoreCase)
                || value.Contains("estrazione", StringComparison.OrdinalIgnoreCase)
                || value.Contains("extractie", StringComparison.OrdinalIgnoreCase)
                || value.Contains("extrait", StringComparison.OrdinalIgnoreCase))
            && (value.Contains("incomplete", StringComparison.OrdinalIgnoreCase)
                || value.Contains("incomplet", StringComparison.OrdinalIgnoreCase)
                || value.Contains("onvolledig", StringComparison.OrdinalIgnoreCase)
                || value.Contains("uncertain", StringComparison.OrdinalIgnoreCase)
                || value.Contains("incertain", StringComparison.OrdinalIgnoreCase)
                || value.Contains("onzeker", StringComparison.OrdinalIgnoreCase)
                || value.Contains("review", StringComparison.OrdinalIgnoreCase)
                || value.Contains("verification", StringComparison.OrdinalIgnoreCase)
                || value.Contains("verifica", StringComparison.OrdinalIgnoreCase)
                || value.Contains("pruefung", StringComparison.OrdinalIgnoreCase)
                || value.Contains("reserve", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatNullable(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

    private static string FormatNullable(double? value)
        => value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "unknown";
}
