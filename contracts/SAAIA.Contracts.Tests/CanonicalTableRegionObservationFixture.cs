using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Contracts.Tests;

internal static class CanonicalTableRegionObservationFixture
{
    internal static CanonicalTableRegionObservation Create(CanonicalDocument? document = null)
    {
        document ??= CanonicalContractFixture.CreateDocument();
        var width = document.Pages[0].Geometry.Width!.Value;
        var height = document.Pages[0].Geometry.Height!.Value;
        const string text = "Recorded limit -5 °C; do not open.";
        var polygon = new[] { new[] { width * .2, height * .35 }, new[] { width * .7, height * .35 },
                              new[] { width * .7, height * .4 }, new[] { width * .2, height * .4 } };
        var raw = JsonSerializer.SerializeToUtf8Bytes(new
        {
            engine = "rapidocr", engine_version = "fixture-1", model_id = "fixture-model",
            model_artifacts = new Dictionary<string, string> { ["fixture.onnx"] = new('c', 64) },
            runtime_settings = new { onnxruntime_intra_op_num_threads = 2, onnxruntime_inter_op_num_threads = 1,
                observed_session_count = 1, providers = new[] { "CPUExecutionProvider" } },
            duration_ms = 1, coordinate_origin = "TOPLEFT", minimum_confidence = .99,
            lines = new[] { new { text, confidence = .99, polygon } }
        });
        var rawSha = Sha(raw);
        var settingsSha = new string('d', 64);
        var table = document.Pages[0].Tables[0];
        table.Provenance.Attributes["doclingSelfRef"] = "#/tables/0";
        var observationId = CanonicalStableId.Create("table_observation", document.DocumentId.ToString("D"),
            document.RevisionId.ToString("D"), document.Source.Sha256, "1", table.TableId, "#/tables/0", settingsSha, rawSha);
        var lineId = CanonicalStableId.Create("table_observation_line", observationId, "0", Sha(Encoding.UTF8.GetBytes(text)));
        return new()
        {
            ObservationId = observationId, DocumentId = document.DocumentId, RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256, PhysicalPage = 1, TableId = table.TableId, SourceTableRef = "#/tables/0",
            RawObservationSha256 = rawSha, SettingsSha256 = settingsSha, PublicationRawBytes = raw,
            Region = table.Polygon!, CoordinateFrame = new() { PageWidthPoints = width, PageHeightPoints = height },
            EngineProvenance = new() { Engine = "rapidocr", EngineVersion = "fixture-1", ModelId = "fixture-model",
                ModelArtifacts = new() { ["fixture.onnx"] = new('c', 64) }, DurationMs = 1,
                RuntimeSettings = new() { OnnxruntimeIntraOpNumThreads = 2, OnnxruntimeInterOpNumThreads = 1,
                    ObservedSessionCount = 1, Providers = ["CPUExecutionProvider"] } },
            Lines = [new() { LineId = lineId, RawOrdinal = 0, Text = text,
                TextSha256 = Sha(Encoding.UTF8.GetBytes(text)), Confidence = .99,
                Polygon = new() { Points = polygon.Select(p => new CanonicalPoint { X = p[0] / width, Y = p[1] / height }).ToList() } }],
            PhysicalReading = new() { Reason = "no_continuous_vertical_walls", UnassignedLineIds = [lineId] },
            QualitySignals = ["alternative_observation", "cell_membership_not_proven", "semantic_parent_links_not_asserted", "semantic_accuracy_not_verified"]
        };
    }

    internal static CanonicalDocument Attach()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var observation = Create(document);
        document.Pages[0].TableRegionObservations = new()
        {
            Availability = "observed", Observations = [observation],
            Attempts = [new() { SourceTableRef = observation.SourceTableRef, PhysicalPage = 1, Attempted = true,
                Status = "completed", ObservationId = observation.ObservationId, RawObservationSha256 = observation.RawObservationSha256 }]
        };
        return document;
    }

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
