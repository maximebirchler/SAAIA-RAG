using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Contracts.DocumentIntelligence;

public static partial class CanonicalTableRegionObservationValidator
{
    public const int MaximumObservations = 8;
    public const int MaximumLines = 5_000;
    public const int MaximumBands = 32;
    public const int MaximumRawBytes = 2_000_000;
    public const int MaximumCanonicalBytes = 4_000_000;
    public const int MaximumDocumentBytes = 16_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<CanonicalValidationIssue> Validate(
        CanonicalTableRegionObservation observation, bool requireRawObservation = false)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var issues = new List<CanonicalValidationIssue>();
        Check(observation.SchemaVersion == "table_region_observation_v1", "schemaVersion", "schema_version", issues);
        Check(observation.DocumentId != Guid.Empty && observation.RevisionId != Guid.Empty, "documentId", "identity", issues);
        Check(Sha(observation.SourceSha256) && Sha(observation.RawObservationSha256) && Sha(observation.SettingsSha256), "sourceSha256", "sha256", issues);
        Check(Id(observation.ObservationId) && Id(observation.TableId) && TableRef(observation.SourceTableRef)
            && observation.PhysicalPage > 0, "observationId", "identity", issues);
        Check(observation.SemanticAccuracy == "not_verified" && !observation.CanonicalGridRepaired,
            "semanticAccuracy", "unearned_semantics", issues);
        Polygon(observation.Region, "region", issues);
        var frame = observation.CoordinateFrame;
        Check(frame is not null && frame.CoordinateOrigin == "TOPLEFT"
            && FinitePositive(frame.PageWidthPoints) && FinitePositive(frame.PageHeightPoints),
            "coordinateFrame", "coordinate_frame", issues);
        var provenance = observation.EngineProvenance;
        Check(provenance is not null && provenance.Engine == "rapidocr" && Token(provenance.EngineVersion, 128)
            && Token(provenance.ModelId, 256), "engineProvenance", "engine_provenance", issues);
        if (provenance is not null)
        {
            Check(provenance.ModelArtifacts is { Count: > 0 and <= 16 }
                && provenance.ModelArtifacts.All(kv => Regex.IsMatch(kv.Key, @"\A[A-Za-z0-9_.-]{1,128}\z") && Sha(kv.Value)),
                "engineProvenance.modelArtifacts", "model_provenance", issues);
            Check(provenance.DurationMs is null || double.IsFinite(provenance.DurationMs.Value)
                && provenance.DurationMs is >= 0 and <= 15_000, "engineProvenance.durationMs", "range", issues);
            var runtime = provenance.RuntimeSettings;
            Check(runtime is not null && runtime.OnnxruntimeIntraOpNumThreads == 2
                && runtime.OnnxruntimeInterOpNumThreads == 1 && runtime.ObservedSessionCount is > 0 and <= 16
                && runtime.Providers is ["CPUExecutionProvider"], "engineProvenance.runtimeSettings", "cpu_runtime_provenance", issues);
        }
        if (observation.Lines is null || observation.Lines.Count > MaximumLines)
        {
            Check(false, "lines", "line_budget", issues);
            return issues;
        }
        var lines = new Dictionary<string, CanonicalTableRegionObservationLine>(StringComparer.Ordinal);
        for (var index = 0; index < observation.Lines.Count; index++)
        {
            var line = observation.Lines[index];
            if (line is null) { Check(false, "lines", "required", issues); continue; }
            Check(Id(line.LineId) && lines.TryAdd(line.LineId, line), $"lines[{index}].lineId", "duplicate_or_invalid_id", issues);
            Check(line.RawOrdinal == index, $"lines[{index}].rawOrdinal", "raw_order", issues);
            Check(line.Text is not null && Sha(line.TextSha256) && TryHash(line.Text, out var textSha)
                && textSha == line.TextSha256, $"lines[{index}].textSha256", "literal_hash", issues);
            Check(line.Confidence is null || double.IsFinite(line.Confidence.Value) && line.Confidence is >= 0 and <= 1,
                $"lines[{index}].confidence", "range", issues);
            Polygon(line.Polygon, $"lines[{index}].polygon", issues);
        }
        Check(observation.QualitySignals is { Count: <= 64 }
            && observation.QualitySignals.Distinct(StringComparer.Ordinal).Count() == observation.QualitySignals.Count
            && observation.QualitySignals.All(s => Regex.IsMatch(s ?? "", @"\A[a-z0-9_]{1,128}\z"))
            && new[] { "alternative_observation", "cell_membership_not_proven", "semantic_parent_links_not_asserted", "semantic_accuracy_not_verified" }
                .All(s => observation.QualitySignals.Contains(s, StringComparer.Ordinal)), "qualitySignals", "quality_contract", issues);
        ValidateReading(observation.PhysicalReading, lines, issues);
        ValidateVectorEvidence(observation, lines, issues);
        ValidateRaw(observation, requireRawObservation, issues);
        if (issues.Count == 0)
        {
            try { Check(StrictUtf8.GetByteCount(CanonicalContractJson.Serialize(observation)) <= MaximumCanonicalBytes,
                    "payload", "canonical_byte_budget", issues); }
            catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
            { Check(false, "payload", "invalid_canonical_encoding", issues); }
        }
        return issues;
    }

    public static void ValidateOrThrow(CanonicalTableRegionObservation observation, bool requireRawObservation = false)
    {
        var issues = Validate(observation, requireRawObservation);
        if (issues.Count > 0) throw new CanonicalContractException(issues);
    }

    private static void ValidateReading(CanonicalTablePhysicalReading? reading,
        Dictionary<string, CanonicalTableRegionObservationLine> lines, List<CanonicalValidationIssue> issues)
    {
        if (reading is null) { Check(false, "physicalReading", "required", issues); return; }
        Check(reading.Algorithm == "observed_bottom_rule_bands_v1" && !reading.CellMembershipProven
            && !reading.SemanticParentLinksAsserted, "physicalReading", "unearned_semantics", issues);
        Check(reading.Reason is null || Token(reading.Reason, 128), "physicalReading.reason", "reason", issues);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        Check(reading.Availability is "candidate_only" or "unavailable", "physicalReading.availability", "availability", issues);
        if (reading.Availability == "unavailable")
            Check(reading.Bands is null && Token(reading.Reason, 128), "physicalReading.bands", "unknown_not_empty", issues);
        else
            Check(reading.Bands is { Count: > 0 and <= MaximumBands }, "physicalReading.bands", "band_budget", issues);
        if (reading.Bands is { Count: <= MaximumBands } bands)
        {
            for (var index = 0; index < bands.Count; index++)
            {
                var band = bands[index];
                if (band is null) { Check(false, "physicalReading.bands", "required", issues); continue; }
                Check(band.BandOrdinal == index, "physicalReading.bands", "band_order", issues);
                Polygon(band.Polygon, "physicalReading.bands.polygon", issues);
                Check(band.SupportingRuleIds is { Count: > 0 and <= 10_000 }
                    && band.SupportingRuleIds.All(Id) && band.SupportingRuleIds.Distinct(StringComparer.Ordinal).Count() == band.SupportingRuleIds.Count,
                    "physicalReading.bands.supportingRuleIds", "rule_provenance", issues);
                if (band.OrderedLineIds is null || band.OrderedLineIds.Count > MaximumLines)
                { Check(false, "physicalReading.bands.orderedLineIds", "line_budget", issues); continue; }
                foreach (var id in band.OrderedLineIds)
                    Check(id is not null && lines.ContainsKey(id) && assigned.Add(id), "physicalReading.bands.orderedLineIds", "line_bijection", issues);
            }
        }
        if (reading.UnassignedLineIds is null || reading.UnassignedLineIds.Count > MaximumLines)
            Check(false, "physicalReading.unassignedLineIds", "line_budget", issues);
        else
            foreach (var id in reading.UnassignedLineIds)
                Check(id is not null && lines.ContainsKey(id) && assigned.Add(id), "physicalReading.unassignedLineIds", "line_bijection", issues);
        Check(assigned.SetEquals(lines.Keys), "physicalReading", "line_bijection", issues);
    }

    private static bool Sha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Token(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Id(string? value) => Token(value, 200);
    private static bool TableRef(string? value) => value is not null && Regex.IsMatch(value, @"\A#/tables/[0-9]{1,9}\z");
    private static bool FinitePositive(double value) => double.IsFinite(value) && value is > 0 and <= 1_000_000;
    private static bool TryHash(string? text, out string hash)
    {
        hash = "";
        try { if (text is null) return false; hash = Hash(StrictUtf8.GetBytes(text)); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static void Check(bool valid, string path, string code, List<CanonicalValidationIssue> issues)
    { if (!valid) issues.Add(new("$.tableRegionObservations." + path, code, "Secondary table observation contract violated.")); }
    private static void Polygon(CanonicalPolygon? polygon, string path, List<CanonicalValidationIssue> issues)
    {
        Check(polygon is { Points.Count: 4 } && polygon.Points.All(p => p is not null && double.IsFinite(p.X)
            && double.IsFinite(p.Y) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1)
            && polygon.Points.Max(p => p.X) > polygon.Points.Min(p => p.X)
            && polygon.Points.Max(p => p.Y) > polygon.Points.Min(p => p.Y), path, "normalized_polygon", issues);
    }
}
