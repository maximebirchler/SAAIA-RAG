using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    private static readonly JsonSerializerOptions WorkingSetJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Working_set_retry_restores_latest_committed_members_and_order_without_reviving_retired_evidence()
    {
        var a = WorkingSetEvidence("A", 1);
        var b = WorkingSetEvidence("B", 2);
        var c = WorkingSetEvidence("C", 3);
        AdvancedAnalysisToolEventSummary[] events =
        [
            WorkingSetEvent(1, [a]),
            WorkingSetEvent(2, [b], WorkingSetSnapshot(2, a, b)),
            WorkingSetEvent(3, [c], WorkingSetSnapshot(2, c, b)),
            WorkingSetEvent(4, [a], WorkingSetSnapshot(2, a), "failed")
        ];
        // Exercise the same request/result DTO round trip used by durable history.
        var resumed = JsonSerializer.Deserialize<AdvancedAnalysisToolEventSummary[]>(
            JsonSerializer.Serialize(events, WorkingSetJson), WorkingSetJson)!;
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([], resumed, 2);
        Assert.Equal(new[] { "C", "B" }, actual.Select(item => item.EvidenceId));
        Assert.DoesNotContain(actual, item => item.EvidenceId == "A");
        Assert.Equal(c.ChunkId, actual[0].ChunkId);
        Assert.Equal(c.SourceHash, actual[0].SourceHash);
    }

    [Fact]
    public void Working_set_legacy_history_keeps_existing_merge_and_ignores_failed_admissions()
    {
        var a = WorkingSetEvidence("A", 1);
        var b = WorkingSetEvidence("B", 2);
        var c = WorkingSetEvidence("C", 3);
        var handoff = WorkingSetHandoff(a);
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([handoff],
            [WorkingSetEvent(1, [a, b]), WorkingSetEvent(2, [c], WorkingSetSnapshot(1, c), "failed")], 2);
        Assert.Equal(new[] { "A", "B" }, actual.Select(item => item.EvidenceId));
    }

    [Fact]
    public void Working_set_failed_result_cannot_authorize_an_unknown_snapshot_reference()
    {
        var a = WorkingSetEvidence("A", 1);
        var b = WorkingSetEvidence("B", 2);
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [a], status: "failed"), WorkingSetEvent(2, [b], WorkingSetSnapshot(2, a, b))], 2));
    }

    [Fact]
    public void Working_set_retired_reference_requires_a_new_successful_admission_to_return()
    {
        var a = WorkingSetEvidence("A", 1);
        var b = WorkingSetEvidence("B", 2);
        var first = WorkingSetEvent(1, [a], WorkingSetSnapshot(1, a));
        var second = WorkingSetEvent(2, [b], WorkingSetSnapshot(1, b));
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [first, second, WorkingSetEvent(3, [], WorkingSetSnapshot(1, a))], 1));
        var readmitted = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [first, second, WorkingSetEvent(3, [a], WorkingSetSnapshot(1, a))], 1);
        Assert.Equal("A", Assert.Single(readmitted).EvidenceId);
    }

    [Theory]
    [InlineData("evidenceId", "invented-id")]
    [InlineData("docId", "22222222-2222-4222-8222-222222222222")]
    [InlineData("revisionId", "33333333-3333-4333-8333-333333333333")]
    [InlineData("sourceHash", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("docPath", "different.pdf")]
    [InlineData("fileName", "different.pdf")]
    [InlineData("chunkId", "44444444-4444-4444-8444-444444444444")]
    [InlineData("anchorId", "different-anchor")]
    [InlineData("contentCardId", "55555555-5555-4555-8555-555555555555")]
    public void Working_set_unknown_or_changed_identity_is_rejected(string field, string replacement)
    {
        var a = WorkingSetEvidence("A", 1);
        var changed = ChangeWorkingSetEvidence(a, node => node[field] = replacement);
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [a], WorkingSetSnapshot(2, changed))], 2));
    }

    [Theory]
    [InlineData("pageStart", 2)]
    [InlineData("pageEnd", 2)]
    public void Working_set_changed_physical_page_is_rejected(string field, int replacement)
    {
        var a = WorkingSetEvidence("A", 1);
        var changed = ChangeWorkingSetEvidence(a, node => node[field] = replacement);
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [a], WorkingSetSnapshot(2, changed))], 2));
    }

    [Fact]
    public void Working_set_preserves_secondary_anchor_and_rejects_changed_raw_hash_or_quality()
    {
        var line = ChangeWorkingSetEvidence(WorkingSetEvidence("RAW", 4), node =>
        {
            node["chunkId"] = null;
            node["anchorId"] = "table-observation-anchor-existing";
            node["sourceObservation"] = JsonSerializer.SerializeToNode(new AdvancedAnalysisTableObservationReference
            {
                ObservationId = "table-observation", TableId = "table-existing", LineId = "line-existing",
                RawOrdinal = 3, TextSha256 = new string('c', 64), RawObservationSha256 = new string('d', 64),
                QualitySignals = ["alternative_observation", "cell_membership_not_proven"]
            }, WorkingSetJson);
        });
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [line], WorkingSetSnapshot(2, line))], 2);
        Assert.Equal(line.AnchorId, Assert.Single(actual).AnchorId);
        foreach (var mutation in new[] { "rawObservationSha256", "cellMembershipProven" })
        {
            var changed = ChangeWorkingSetEvidence(line, node =>
            {
                var source = node["sourceObservation"]!.AsObject();
                if (mutation == "cellMembershipProven") source[mutation] = true;
                else source[mutation] = new string('e', 64);
            });
            Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
                [WorkingSetEvent(1, [line], WorkingSetSnapshot(2, changed))], 2));
        }
    }

    [Fact]
    public void Working_set_native_reference_is_preserved_and_changed_line_is_rejected()
    {
        var line = ChangeWorkingSetEvidence(WorkingSetEvidence("NATIVE", 4), node =>
            node["nativeObservation"] = JsonSerializer.SerializeToNode(new AdvancedAnalysisNativeObservationReference
            {
                BlockId = "block-existing", LineId = "native_line_existing", PhysicalPage = 4,
                LineOrdinal = 2, RawTextSha256 = new string('c', 64), LineTextSha256 = new string('d', 64),
                UnicodeScalarStart = 0, UnicodeScalarEndExclusive = 12
            }, WorkingSetJson));
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [line], WorkingSetSnapshot(2, line))], 2);
        Assert.Equal("native_line_existing", Assert.Single(actual).NativeObservation!.LineId);
        var changed = ChangeWorkingSetEvidence(line, node => node["nativeObservation"]!["lineTextSha256"] = new string('e', 64));
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [line], WorkingSetSnapshot(2, changed))], 2));
    }

    [Fact]
    public void Working_set_reference_remains_valid_after_title_promotion_and_native_window_changes()
    {
        var native = new AdvancedAnalysisNativeObservationReference
        {
            BlockId = "existing-block", LineId = "native_line_existing", PhysicalPage = 1,
            RawTextSha256 = new string('c', 64), LineTextSha256 = new string('d', 64),
            UnicodeScalarStart = 0, UnicodeScalarEndExclusive = 12
        };
        var reference = ChangeWorkingSetEvidence(WorkingSetEvidence("NATIVE", 1), node =>
            node["nativeObservation"] = JsonSerializer.SerializeToNode(native, WorkingSetJson));
        var original = new AdvancedAnalysisResolvedEvidence(reference, "canonical literal body")
        {
            NativeObservation = new(native, 8, 0, 1, 1, ["secondary_native_audit"])
        };
        var promoted = original with
        {
            ExactTitle = "newly verified title",
            SourceStructure = new("verified section", null, null, null),
            NativeObservation = original.NativeObservation! with { LineOffset = 1, NextLineOffset = 2 }
        };
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [original.Reference], WorkingSetSnapshot(1, original.Reference)),
             WorkingSetEvent(2, [promoted.Reference], WorkingSetSnapshot(1, promoted.Reference))], 1);
        Assert.Equal(native.LineId, Assert.Single(actual).NativeObservation!.LineId);
        var snapshotJson = JsonSerializer.Serialize(WorkingSetSnapshot(1, promoted.Reference), WorkingSetJson);
        Assert.DoesNotContain("newly verified title", snapshotJson);
        Assert.DoesNotContain("canonical literal body", snapshotJson);
    }

    [Fact]
    public void Working_set_partial_handoff_derives_id_and_keeps_all_present_identity_constraints()
    {
        var initial = WorkingSetEvidence("pending", 1);
        var identity = string.Join("|", initial.DocId, initial.RevisionId, initial.ChunkId, "", "", 1, 1, initial.SourceHash);
        var id = "advanced-evidence-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
        var resolved = ChangeWorkingSetEvidence(initial, node => node["evidenceId"] = id);
        var partial = new AdvancedAnalysisEvidenceReference { ChunkId = initial.ChunkId, DocPath = initial.DocPath };
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([partial],
            [WorkingSetEvent(1, [], WorkingSetSnapshot(2, resolved))], 2);
        Assert.Equal(id, Assert.Single(actual).EvidenceId);
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([partial],
            [WorkingSetEvent(1, [], WorkingSetSnapshot(2, initial))], 2));
        var wrongConstraint = new AdvancedAnalysisEvidenceReference { ChunkId = initial.ChunkId, SourceHash = new string('f', 64) };
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([wrongConstraint],
            [WorkingSetEvent(1, [], WorkingSetSnapshot(2, resolved))], 2));
    }

    [Fact]
    public void Working_set_partial_handoff_with_explicit_id_preserves_that_id()
    {
        var initial = WorkingSetEvidence("user-supplied-existing-id", 1);
        var partial = new AdvancedAnalysisEvidenceReference { ChunkId = initial.ChunkId, EvidenceId = initial.EvidenceId };
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([partial],
            [WorkingSetEvent(1, [], WorkingSetSnapshot(2, initial))], 2);
        Assert.Equal(initial.EvidenceId, Assert.Single(actual).EvidenceId);
    }

    [Fact]
    public void Working_set_known_history_does_not_override_an_existing_handoff_identity_constraint()
    {
        var initial = WorkingSetEvidence("existing-id", 1);
        var handoff = new AdvancedAnalysisEvidenceReference
        {
            EvidenceId = initial.EvidenceId, ChunkId = initial.ChunkId, SourceHash = new string('f', 64)
        };
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([handoff],
            [WorkingSetEvent(1, [initial], WorkingSetSnapshot(2, initial))], 2));
    }

    [Fact]
    public void Working_set_handoff_page_window_cannot_authorize_an_unobserved_chunk_or_native_line()
    {
        var chunk = WorkingSetEvidence("pending", 1);
        var window = new AdvancedAnalysisEvidenceReference
        {
            DocId = chunk.DocId, RevisionId = chunk.RevisionId, PageStart = 1, PageEnd = 1
        };
        var aggregate = ChangeWorkingSetEvidence(chunk, node => node["chunkId"] = null);
        var identity = string.Join("|", aggregate.DocId, aggregate.RevisionId, "", "", "", 1, 1, aggregate.SourceHash);
        aggregate = ChangeWorkingSetEvidence(aggregate, node => node["evidenceId"] = "advanced-evidence-"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32]);
        var actual = AdvancedAnalysisWorker.BuildResumeEvidenceReferences([window],
            [WorkingSetEvent(1, [], WorkingSetSnapshot(2, aggregate))], 2);
        Assert.Null(Assert.Single(actual).ChunkId);
        var native = ChangeWorkingSetEvidence(aggregate, node =>
        {
            node["evidenceId"] = "advanced-native-evidence-existing";
            node["nativeObservation"] = JsonSerializer.SerializeToNode(new AdvancedAnalysisNativeObservationReference
            {
                BlockId = "block-existing", LineId = "native_line_existing", PhysicalPage = 1,
                LineTextSha256 = new string('c', 64), RawTextSha256 = new string('d', 64)
            }, WorkingSetJson);
        });
        foreach (var unknown in new[] { chunk, native })
            Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([window],
                [WorkingSetEvent(1, [], WorkingSetSnapshot(2, unknown))], 2));
    }

    [Fact]
    public void Working_set_duplicate_ids_and_canonical_aliases_are_rejected()
    {
        var a = WorkingSetEvidence("A", 1);
        var alias = ChangeWorkingSetEvidence(a, node => node["evidenceId"] = "alias");
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [a], WorkingSetSnapshot(2, a, a))], 2));
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [a, alias], WorkingSetSnapshot(2, a, alias))], 2));
    }

    [Fact]
    public void Working_set_schema_capacity_and_snapshot_continuity_are_required()
    {
        var a = WorkingSetEvidence("A", 1);
        var b = WorkingSetEvidence("B", 2);
        var invalid = new[]
        {
            new AdvancedAnalysisEvidenceWorkingSet("unknown", 1, [a]),
            WorkingSetSnapshot(2, a), WorkingSetSnapshot(1, a, b),
            new AdvancedAnalysisEvidenceWorkingSet(AdvancedAnalysisEvidenceWorkingSet.CurrentSchemaVersion, 1, null!)
        };
        foreach (var snapshot in invalid)
            Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
                [WorkingSetEvent(1, [a, b], snapshot)], 1));
        Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
            [WorkingSetEvent(1, [a], WorkingSetSnapshot(1, a)), WorkingSetEvent(2, [b])], 1));
    }

    [Fact]
    public void Working_set_event_sequence_cannot_reorder_or_replace_a_committed_snapshot()
    {
        var a = WorkingSetEvidence("A", 1);
        foreach (var sequence in new[] { 0, 1, 2 })
            Assert.Throws<InvalidDataException>(() => AdvancedAnalysisWorker.BuildResumeEvidenceReferences([],
                [WorkingSetEvent(2, [a], WorkingSetSnapshot(1, a)), WorkingSetEvent(sequence, [a], WorkingSetSnapshot(1, a))], 1));
    }

    private static AdvancedAnalysisResultEvidence WorkingSetEvidence(string id, int page) => new()
    {
        EvidenceId = id, DocId = "11111111-1111-4111-8111-111111111111",
        RevisionId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", FileName = "working-set.pdf",
        DocPath = "working-set.pdf", SourceHash = new string('a', 64), PageStart = page, PageEnd = page,
        ChunkId = $"bbbbbbbb-bbbb-4bbb-8bbb-{page:000000000000}"
    };

    private static AdvancedAnalysisEvidenceReference WorkingSetHandoff(AdvancedAnalysisResultEvidence item) => new()
    {
        EvidenceId = item.EvidenceId, DocId = item.DocId, RevisionId = item.RevisionId, FileName = item.FileName,
        DocPath = item.DocPath, SourceHash = item.SourceHash, PageStart = item.PageStart,
        PageEnd = item.PageEnd, ChunkId = item.ChunkId, AnchorId = item.AnchorId, ContentCardId = item.ContentCardId,
        NativeObservation = item.NativeObservation
    };

    private static AdvancedAnalysisEvidenceWorkingSet WorkingSetSnapshot(int maximum,
        params AdvancedAnalysisResultEvidence[] evidence)
        => new(AdvancedAnalysisEvidenceWorkingSet.CurrentSchemaVersion, maximum, evidence);

    private static AdvancedAnalysisToolEventSummary WorkingSetEvent(int sequence,
        IReadOnlyList<AdvancedAnalysisResultEvidence> evidence, AdvancedAnalysisEvidenceWorkingSet? snapshot = null,
        string status = "succeeded")
        => new(sequence, 1, "search_corpus", status,
            new AdvancedAnalysisSearchRequest("working-set", EvidenceWorkingSet: snapshot), evidence, [], 1,
            status == "succeeded" ? null : "test_failure");

    private static AdvancedAnalysisResultEvidence ChangeWorkingSetEvidence(AdvancedAnalysisResultEvidence item,
        Action<JsonObject> change)
    {
        var node = JsonSerializer.SerializeToNode(item, WorkingSetJson)!.AsObject();
        change(node);
        return node.Deserialize<AdvancedAnalysisResultEvidence>(WorkingSetJson)!;
    }
}
