using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using static DoclingRegionalTextRepairJson;

internal sealed record DoclingRegionalTextRepairProjection(string Raw, string Canonical,
    string? Attribute = null, string? Proof = null, string? Flag = null)
{
    internal void Attach(CanonicalBlock block)
    {
        if (Attribute is null || Proof is null || Flag is null) return;
        block.Provenance.Attributes[Attribute] = Proof;
        block.QualityFlags.Add(Flag);
    }
}

/// <summary>A corrected reading is admitted only with its original region and two independently observed readings.</summary>
internal static class DoclingRegionalTextRepairProvenance
{
    internal static DoclingRegionalTextRepairProjection Project(DoclingTextItem item,
        DoclingProvenance provenance, int provenanceIndex, CanonicalPageGeometry geometry)
    {
        var (raw, canonical) = DoclingTextRegionProjector.Project(item, provenance, provenanceIndex);
        if (item.RegionalTextRepair is { } applied)
        {
            if (item.RegionalTextRepairAttempt is not null || item.Label is "formula" or "code") throw Invalid();
            var json = BoundedJson(applied);
            EqualString(applied, "schema_version", Schema);
            EqualString(applied, "source_ref", item.SelfRef);
            EqualString(applied, "status", "applied");
            EqualString(applied, "reason", "independent_high_confidence_source_retaining_agreement");
            SameProvenance(applied, item);
            var original = String(applied, "original_text", 16_000);
            var replacement = String(applied, "replacement_text", 16_000);
            if (replacement != item.Text || original == replacement || replacement.EnumerateRunes().Count() > 8_000) throw Invalid();
            CheckHash(applied, "original_text_sha256", original);
            CheckHash(applied, "replacement_text_sha256", replacement);
            EqualString(applied, "agreement_normalization", "unicode_nfc_and_whitespace_only");
            DoclingRegionalTextRetentionPolicy.Validate(applied, original, replacement);
            var readings = Array(applied, "observations", 2);
            if (readings.Length != 2 || readings.Select(value => String(value, "engine")).Distinct().Count() != 2) throw Invalid();
            foreach (var observation in readings)
                ValidateReading(observation, replacement, provenance, geometry);
            // `orig` is kept verbatim. If Docling omitted it, the validated pre-repair text
            // supplies the same raw fallback the normal region projector would have used.
            return new(string.IsNullOrEmpty(item.OriginalText) ? original : raw, replacement,
                "regionalTextRepair", json, "regional_text_ocr_repair_applied");
        }
        if (item.RegionalTextRepairAttempt is not { } attempt) return new(raw, canonical);
        var attemptJson = BoundedJson(attempt);
        EqualString(attempt, "schema_version", Schema);
        EqualString(attempt, "source_ref", item.SelfRef);
        EqualString(attempt, "status", "not_applied");
        EqualString(attempt, "original_text", item.Text);
        SameProvenance(attempt, item, requireSingleRegion: false);
        _ = Bool(attempt, "attempted");
        var reason = String(attempt, "reason", 128);
        if (reason.Length == 0 || reason.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) throw Invalid();
        return new(raw, canonical, "regionalTextRepairAttempt", attemptJson, "regional_text_ocr_repair_inconclusive");
    }

    private static void ValidateReading(JsonElement observation, string replacement,
        DoclingProvenance provenance, CanonicalPageGeometry page)
    {
        var pageWidth = page.Width is > 0 && double.IsFinite(page.Width.Value) ? page.Width.Value : throw Invalid();
        var pageHeight = page.Height is > 0 && double.IsFinite(page.Height.Value) ? page.Height.Value : throw Invalid();
        var engine = String(observation, "engine");
        var threshold = engine switch { "rapidocr" => .98, "tesseract" => .95, _ => throw Invalid() };
        if (String(observation, "engine_version").Length == 0 || String(observation, "model_id").Length == 0
            || Int(observation, "duration_ms") < 0) throw Invalid();
        if (engine == "tesseract") EqualString(observation, "model_id", "eng:psm6");
        EqualString(observation, "coordinate_origin", "TOPLEFT");
        if (observation.TryGetProperty("model_sha256", out var hash) && hash.ValueKind != JsonValueKind.Null
            && (hash.ValueKind != JsonValueKind.String || !IsSha(hash.GetString()!))) throw Invalid();
        if (observation.TryGetProperty("model_artifacts", out var artifacts) && artifacts.ValueKind != JsonValueKind.Null)
        {
            if (artifacts.ValueKind != JsonValueKind.Object || artifacts.EnumerateObject().Count() > 32) throw Invalid();
            foreach (var artifact in artifacts.EnumerateObject())
                if (artifact.Name.Length > 512 || artifact.Value.ValueKind != JsonValueKind.String || !IsSha(artifact.Value.GetString()!)) throw Invalid();
        }
        var minimum = Number(observation, "minimum_confidence");
        if (minimum < threshold || minimum > 1) throw Invalid();
        var lines = Array(observation, "lines", 512);
        if (lines.Length == 0) throw Invalid();
        var texts = new List<string>();
        var actualMinimum = 1d;
        var box = provenance.BoundingBox ?? throw Invalid();
        var top = box.CoordinateOrigin switch { "BOTTOMLEFT" => pageHeight - box.Top, "TOPLEFT" => box.Top, _ => throw Invalid() };
        var bottom = box.CoordinateOrigin == "BOTTOMLEFT" ? pageHeight - box.Bottom : box.Bottom;
        if (!double.IsFinite(top) || !double.IsFinite(bottom) || !double.IsFinite(box.Left) || !double.IsFinite(box.Right)
            || box.Left < 0 || box.Right > pageWidth
            || top < 0 || bottom > pageHeight || box.Right <= box.Left || bottom <= top) throw Invalid();
        foreach (var line in lines)
        {
            var text = String(line, "text", 16_000);
            var confidence = Number(line, "confidence");
            if (string.IsNullOrWhiteSpace(text) || confidence < threshold || confidence > 1) throw Invalid();
            actualMinimum = Math.Min(actualMinimum, confidence);
            var points = Array(line, "polygon", 4);
            if (points.Length != 4) throw Invalid();
            var xs = new List<double>(); var ys = new List<double>();
            foreach (var point in points)
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2
                    || point[0].ValueKind != JsonValueKind.Number || point[1].ValueKind != JsonValueKind.Number
                    || !point[0].TryGetDouble(out var x) || !point[1].TryGetDouble(out var y)
                    || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > pageWidth || y > pageHeight) throw Invalid();
                xs.Add(x); ys.Add(y);
            }
            var centerX = (xs.Min() + xs.Max()) / 2; var centerY = (ys.Min() + ys.Max()) / 2;
            if (xs.Max() <= xs.Min() || ys.Max() <= ys.Min() || centerX < box.Left || centerX > box.Right
                || centerY < top || centerY > bottom) throw Invalid();
            texts.Add(text);
        }
        if (actualMinimum != minimum || Normalize(string.Join(' ', texts)) != Normalize(replacement)) throw Invalid();
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
