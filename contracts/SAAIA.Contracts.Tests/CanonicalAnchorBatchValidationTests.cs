using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class CanonicalAnchorBatchValidationTests
{
    [Fact]
    public void Bundle_reports_each_document_issue_once_instead_of_revalidating_for_every_anchor()
    {
        var document = CanonicalContractFixture.CreateDocument();
        document.SchemaVersion = "invalid_document_schema";
        var anchors = Enumerable.Range(0, 32).Select(index =>
        {
            var anchor = CanonicalContractFixture.CreateAnchor(document);
            anchor.AnchorId = "anchor-" + index;
            anchor.ProjectionId = "projection-" + index;
            return anchor;
        }).ToList();
        var issues = CanonicalIngestionBundleValidator.Validate(new() { Document = document, SourceAnchors = anchors });
        Assert.Single(issues, issue => issue.Code == "schema_version"
            && issue.Message == "Unsupported canonical document schema.");
    }

    [Fact]
    public void Public_anchor_validation_still_requires_a_valid_document()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var anchor = CanonicalContractFixture.CreateAnchor(document);
        document.SchemaVersion = "invalid_document_schema";
        Assert.Contains(CanonicalContractValidator.Validate(anchor, document), issue => issue.Code == "schema_version");
    }

    [Fact]
    public void Secondary_table_maximum_line_observation_validates_all_5000_anchors_in_one_bounded_batch()
    {
        var document = CanonicalTableRegionObservationFixture.Attach();
        var set = document.Pages[0].TableRegionObservations!;
        var observation = set.Observations![0];
        var original = observation.Lines[0];
        var raw = JsonNode.Parse(observation.PublicationRawBytes!)!.AsObject();
        var polygon = raw["lines"]![0]!["polygon"]!.DeepClone();
        raw["lines"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 5_000).Select(index => new
        {
            text = "Literal observation " + index, confidence = .99, polygon
        }).ToArray());
        observation.PublicationRawBytes = Encoding.UTF8.GetBytes(raw.ToJsonString());
        observation.RawObservationSha256 = CanonicalTableRegionObservationFixture.Sha(observation.PublicationRawBytes);
        observation.ObservationId = CanonicalStableId.Create("table_observation", document.DocumentId.ToString("D"),
            document.RevisionId.ToString("D"), document.Source.Sha256, "1", observation.TableId,
            observation.SourceTableRef, observation.SettingsSha256, observation.RawObservationSha256);
        observation.Lines = Enumerable.Range(0, 5_000).Select(index =>
        {
            var text = "Literal observation " + index;
            var hash = CanonicalTableRegionObservationFixture.Sha(Encoding.UTF8.GetBytes(text));
            return new CanonicalTableRegionObservationLine
            {
                LineId = CanonicalStableId.Create("table_observation_line", observation.ObservationId, index.ToString(), hash),
                RawOrdinal = index, Text = text, TextSha256 = hash, Polygon = original.Polygon, Confidence = .99
            };
        }).ToList();
        observation.PhysicalReading.UnassignedLineIds = observation.Lines.Select(line => line.LineId).ToList();
        set.Attempts![0].ObservationId = observation.ObservationId;
        set.Attempts[0].RawObservationSha256 = observation.RawObservationSha256;
        var envelope = CanonicalContractJson.CreateEnvelope(CanonicalIngestionBundleTests.CreateManifest(document));
        document.ManifestSha256 = envelope.ManifestSha256;
        var anchors = CanonicalTableObservationAnchors.Build(document).ToList();
        Assert.Equal(5_000, anchors.Count);
        var stopwatch = Stopwatch.StartNew();
        CanonicalIngestionBundleValidator.ValidateOrThrow(new()
        {
            Document = document, ManifestEnvelope = envelope, SourceAnchors = anchors
        });
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "A single maximum-line observation must use one document validation and an indexed anchor batch.");
    }
}
