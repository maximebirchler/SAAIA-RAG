using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingTableRegionObservationBuilder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Apply(CanonicalDocument document, DoclingDocument source)
    {
        if (source.TableRegionObservations is not { } envelope) return;
        DoclingTableRegionObservationPolicy.ValidateEnvelope(source, envelope);
        Require(envelope.SourceSha256 == document.Source.Sha256
            || envelope.Availability == "unavailable" && envelope.SourceSha256 is null);
        foreach (var page in document.Pages)
        {
            page.TableRegionObservations = new()
            {
                Availability = envelope.Availability,
                Reason = envelope.Reason,
                Observations = envelope.Availability == "unavailable" ? null : [],
                Attempts = envelope.Availability == "unavailable" ? null : []
            };
        }
        if (envelope.Availability == "unavailable") return;
        if (envelope.Attempts!.Any(row => row.PageNumber is null
            || document.Pages.All(page => page.PageNumber != row.PageNumber)))
        {
            foreach (var page in document.Pages)
                page.TableRegionObservations = new() { Reason = "source_attempt_page_unavailable" };
            return;
        }
        var settingsSha = CanonicalContractJson.ComputeSha256(envelope.Settings);
        long publicationBytes = 0;
        long rawBytes = 0;
        long projectedBytes = 0;
        var refused = new Dictionary<string, (string Reason, string? RawSha)>(StringComparer.Ordinal);
        foreach (var row in envelope.Observations!)
        {
            var page = document.Pages.SingleOrDefault(page => page.PageNumber == row.PageNumber);
            Require(page is not null && page.Geometry.Width == row.PageWidthPoints
                && page.Geometry.Height == row.PageHeightPoints && row.CoordinateOrigin == "TOPLEFT");
            var tables = page!.Tables.Where(table => table.Provenance.Attributes.TryGetValue("doclingSelfRef", out var selfRef)
                && selfRef == row.SourceTableRef).ToArray();
            Require(tables.Length == 1);
            CanonicalTableRegionObservation observation;
            try { observation = Build(document, tables[0], row, settingsSha); }
            catch (ObservationLimitException limit)
            {
                refused.Add(row.SourceTableRef, (limit.Reason, limit.VerifiedRawSha));
                page.TableRegionObservations!.Availability = "partial";
                page.TableRegionObservations.Reason = limit.Reason;
                continue;
            }
            var canonicalBytes = StrictUtf8.GetByteCount(CanonicalContractJson.SerializeCanonical(observation));
            var candidateBytes = canonicalBytes + observation.PublicationRawBytes!.LongLength;
            if (canonicalBytes > 4_000_000 || publicationBytes + candidateBytes > 16_000_000)
            {
                var reason = canonicalBytes > 4_000_000 ? "canonical_observation_byte_limit" : "document_observation_byte_limit";
                refused.Add(row.SourceTableRef, (reason, observation.RawObservationSha256));
                page.TableRegionObservations!.Availability = "partial";
                page.TableRegionObservations.Reason = reason;
                continue;
            }
            publicationBytes += candidateBytes;
            rawBytes += observation.PublicationRawBytes.LongLength;
            projectedBytes += canonicalBytes;
            page.TableRegionObservations!.Observations!.Add(observation);
        }
        foreach (var attempt in envelope.Attempts!)
        {
            var page = document.Pages.Single(page => page.PageNumber == attempt.PageNumber);
            var observation = page.TableRegionObservations!.Observations!
                .SingleOrDefault(row => row.SourceTableRef == attempt.SourceTableRef);
            page.TableRegionObservations.Attempts!.Add(new()
            {
                SourceTableRef = attempt.SourceTableRef, PhysicalPage = attempt.PageNumber,
                Attempted = attempt.Attempted!.Value, Status = attempt.Status, Reason = attempt.Reason,
                RawObservationSha256 = attempt.RawObservationSha256, ObservationId = observation?.ObservationId
            });
            if (refused.TryGetValue(attempt.SourceTableRef, out var refusal))
            {
                var projected = page.TableRegionObservations.Attempts[^1];
                projected.Status = "refused";
                projected.Reason = refusal.Reason;
                projected.RawObservationSha256 = refusal.RawSha;
            }
        }
        source.TableObservationProjection = new(envelope.Observations.Count - refused.Count,
            refused.Count, rawBytes, projectedBytes);
    }

    private static CanonicalTableRegionObservation Build(CanonicalDocument document, CanonicalTable table,
        DoclingTableRegionObservation source, string settingsSha)
    {
        if (source.RawObservationJson.Length > 2_000_000 || StrictUtf8.GetByteCount(source.RawObservationJson) > 2_000_000)
            throw new ObservationLimitException("raw_observation_byte_limit", null);
        var raw = StrictUtf8.GetBytes(source.RawObservationJson);
        Require(raw.Length <= 2_000_000 && DoclingTableRegionObservationPolicy.Sha(source.RawObservationSha256)
            && Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() == source.RawObservationSha256);
        using var parsed = ParseRawObservation(raw);
        var value = parsed.RootElement;
        JsonElement lineValues = default;
        Require(value.ValueKind == JsonValueKind.Object && Text(value, "engine") == "rapidocr"
            && Text(value, "coordinate_origin") == "TOPLEFT"
            && value.TryGetProperty("lines", out lineValues) && lineValues.ValueKind == JsonValueKind.Array);
        if (lineValues.GetArrayLength() > 5_000)
            throw new ObservationLimitException("observation_line_limit", source.RawObservationSha256);
        var observation = new CanonicalTableRegionObservation
        {
            ObservationId = CanonicalStableId.Create("table_observation", document.Source.Sha256,
                source.SourceTableRef, source.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                settingsSha, source.RawObservationSha256),
            DocumentId = document.DocumentId, RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256, PhysicalPage = source.PageNumber,
            TableId = table.TableId, SourceTableRef = source.SourceTableRef,
            RawObservationSha256 = source.RawObservationSha256, SettingsSha256 = settingsSha,
            Region = Rectangle(source.BoundingBox, source.PageWidthPoints, source.PageHeightPoints),
            CoordinateFrame = new() { PageWidthPoints = source.PageWidthPoints, PageHeightPoints = source.PageHeightPoints },
            EngineProvenance = DoclingTableRegionObservationProvenance.Build(value),
            PublicationRawBytes = raw,
            QualitySignals = ["alternative_observation", "cell_membership_not_proven", "semantic_parent_links_not_asserted",
                "semantic_accuracy_not_verified"],
            PhysicalReading = new() { Reason = "native_vector_geometry_not_inspected" }
        };
        foreach (var line in lineValues.EnumerateArray())
        {
            var text = Text(line, "text");
            JsonElement polygon = default;
            Require(text is not null && line.TryGetProperty("polygon", out polygon));
            var ordinal = observation.Lines.Count;
            var confidence = DoclingTableRegionObservationPolicy.Number(line, "confidence");
            Require(line.TryGetProperty("confidence", out var score)
                && (score.ValueKind == JsonValueKind.Null || confidence is >= 0 and <= 1));
            observation.Lines.Add(new()
            {
                LineId = CanonicalStableId.Create("table_observation_line", observation.ObservationId,
                    ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                RawOrdinal = ordinal, Text = text!,
                TextSha256 = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(text!))).ToLowerInvariant(),
                Polygon = Polygon(polygon, source.PageWidthPoints, source.PageHeightPoints), Confidence = confidence
            });
        }
        observation.PhysicalReading.UnassignedLineIds = observation.Lines.Select(line => line.LineId).ToList();
        CanonicalTableRegionObservationValidator.ValidateOrThrow(observation, requireRawObservation: true);
        return observation;
    }

    private static CanonicalPolygon Rectangle(DoclingBoundingBox box, double width, double height)
    {
        Require(double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0
            && double.IsFinite(box.Left) && double.IsFinite(box.Right) && double.IsFinite(box.Top) && double.IsFinite(box.Bottom)
            && box.Left >= 0 && box.Left < box.Right && box.Right <= width
            && box.Top >= 0 && box.Top < box.Bottom && box.Bottom <= height);
        return new() { Points = [new() { X = box.Left / width, Y = box.Top / height },
            new() { X = box.Right / width, Y = box.Top / height }, new() { X = box.Right / width, Y = box.Bottom / height },
            new() { X = box.Left / width, Y = box.Bottom / height }] };
    }

    private static CanonicalPolygon Polygon(JsonElement value, double width, double height)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 4);
        var result = new CanonicalPolygon();
        foreach (var point in value.EnumerateArray())
        {
            Require(point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2);
            var coordinates = point.EnumerateArray().ToArray();
            Require(coordinates.All(item => item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number)
                && double.IsFinite(number)));
            var x = coordinates[0].GetDouble(); var y = coordinates[1].GetDouble();
            Require(x >= 0 && x <= width && y >= 0 && y <= height);
            result.Points.Add(new() { X = x / width, Y = y / height });
        }
        return result;
    }

    private static string? Text(JsonElement value, string name) => DoclingTableRegionObservationPolicy.Text(value, name);
    private static JsonDocument ParseRawObservation(byte[] raw)
    {
        try { return JsonDocument.Parse(raw, new() { MaxDepth = 64 }); }
        catch (JsonException) { throw new InvalidDataException("docling_table_observation_raw_invalid"); }
    }

    private static void Require(bool condition) => DoclingTableRegionObservationPolicy.Require(condition);
    private sealed class ObservationLimitException(string reason, string? verifiedRawSha) : Exception
    {
        internal string Reason { get; } = reason;
        internal string? VerifiedRawSha { get; } = verifiedRawSha;
    }
}
