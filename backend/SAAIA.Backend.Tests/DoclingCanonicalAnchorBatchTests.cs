using System.Collections;
using System.Text.Json;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;
using SAAIA.Contracts.Tests;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DoclingCanonicalAnchorBatchTests
{
    private static CanonicalDocument Document()
    {
        var document = CanonicalContractFixture.CreateDocument();
        document.Pages[0].Blocks[0].Spans.Clear();
        document.Pages[0].Tables[0].Cells[0].SpanIds.Clear();
        return document;
    }

    private static DoclingCanonicalAnchorSelection Selection(string id = "projection", int page = 1,
        IReadOnlyList<string>? blocks = null) => new(id, "retrieval_chunk", page, page, blocks, [], []);

    private static SourceAnchor Single(CanonicalDocument document, DoclingCanonicalAnchorSelection selection)
        => DoclingCanonicalSourceAnchorProjector.ProjectSelection(document, selection.ProjectionId,
            selection.ProjectionType, selection.PageStart, selection.PageEnd, selection.BlockIds,
            selection.SpanIds, selection.TableCellIds);

    [Fact]
    public void Batch_and_individual_projection_have_identical_anchor_bytes_and_leave_document_unchanged()
    {
        var document = Document();
        var before = CanonicalContractJson.SerializeCanonical(document);
        var selections = Enumerable.Range(0, 12).Select(i => Selection("projection-" + i, blocks: [CanonicalContractFixture.BlockId])).ToArray();
        var expected = selections.Select(selection => Single(document, selection)).ToArray();
        var actual = DoclingCanonicalSourceAnchorProjector.ProjectSelections(document, selections);
        Assert.Equal(CanonicalContractJson.SerializeCanonical(expected), CanonicalContractJson.SerializeCanonical(actual));
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Fact]
    public void Invalid_earlier_anchor_is_not_hidden_by_a_later_missing_reference()
    {
        var document = Document();
        var emptyRegions = Selection("first", page: 2);
        var missingReference = Selection("second", blocks: ["missing-block"]);
        var expected = Assert.Throws<CanonicalContractException>(() => Single(document, emptyRegions));
        var actual = Assert.Throws<CanonicalContractException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(
            document, [emptyRegions, missingReference]));
        Assert.Equal(expected.Issues, actual.Issues);
        Assert.Contains(actual.Issues, issue => issue.Path == "$.regions" && issue.Code == "required");
    }

    [Fact]
    public void Later_construction_error_is_rethrown_unchanged_when_the_prefix_is_valid()
    {
        var document = Document();
        var valid = Selection("first", blocks: [CanonicalContractFixture.BlockId]);
        var missing = Selection("second", blocks: ["missing-block"]);
        var expected = Assert.Throws<InvalidDataException>(() => Single(document, missing));
        var actual = Assert.Throws<InvalidDataException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(document, [valid, missing]));
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void Empty_batch_and_ordinary_entry_points_still_reject_an_invalid_document()
    {
        var document = Document();
        document.SchemaVersion = "invalid";
        Assert.Throws<CanonicalContractException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(document, []));
        Assert.Throws<CanonicalContractException>(() => Single(document, Selection(blocks: [CanonicalContractFixture.BlockId])));
        Assert.Throws<CanonicalContractException>(() => DoclingCanonicalSourceAnchorProjector.ProjectPageRange(document, "p", "retrieval_chunk", 1, 1));
    }

    [Fact]
    public void Precancelled_batch_does_not_claim_document_or_anchor_validation_completion()
    {
        var logger = new DiagnosticRecordingLogger<DoclingCanonicalAnchorBatchTests>();
        using var parent = BackendDiagnostics.Start("test.batch", logger);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(
            Document(), [Selection()], cancellation.Token));
        var record = Assert.Single(logger.Events, item => Equals(item.Values["Operation"], "ingestion.canonical_anchor_batch"));
        Assert.Equal("cancelled", record.Values["Outcome"]);
        using var measurements = JsonDocument.Parse((string)record.Values["Measurements"]!);
        Assert.Equal(0, measurements.RootElement.GetProperty("document_validation_attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, measurements.RootElement.GetProperty("anchor_validation_completed_count").ValueKind);
    }

    [Fact]
    public void Cancellation_during_last_construction_is_preserved_after_checking_a_valid_prefix()
    {
        using var cancellation = new CancellationTokenSource();
        var selection = Selection(blocks: new CancelingReferences(cancellation, CanonicalContractFixture.BlockId));
        Assert.Throws<OperationCanceledException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(
            Document(), [selection], cancellation.Token));
    }

    [Fact]
    public void Invalid_prefix_still_wins_over_a_later_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var document = Document();
        var first = Selection("first", page: 2);
        var second = Selection("second", blocks: new CancelingReferences(cancellation, CanonicalContractFixture.BlockId));
        var expected = Assert.Throws<CanonicalContractException>(() => Single(document, first));
        var actual = Assert.Throws<CanonicalContractException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(
            document, [first, second], cancellation.Token));
        Assert.Equal(expected.Issues, actual.Issues);
    }

    [Fact]
    public void Success_probe_reports_the_batch_scope_counts_and_no_document_content()
    {
        var logger = new DiagnosticRecordingLogger<DoclingCanonicalAnchorBatchTests>();
        using var parent = BackendDiagnostics.Start("test.batch", logger);
        var document = Document();
        var selections = new[] { Selection("first", blocks: [CanonicalContractFixture.BlockId]), Selection("second", blocks: [CanonicalContractFixture.BlockId]) };
        DoclingCanonicalSourceAnchorProjector.ProjectSelections(document, selections);
        var record = Assert.Single(logger.Events, item => Equals(item.Values["Operation"], "ingestion.canonical_anchor_batch"));
        Assert.Equal("success", record.Values["Outcome"]);
        using var measurements = JsonDocument.Parse((string)record.Values["Measurements"]!);
        Assert.Equal(2, measurements.RootElement.GetProperty("requested_anchor_count").GetInt32());
        Assert.Equal(2, measurements.RootElement.GetProperty("document_validation_attempts").GetInt32());
        Assert.Equal(2, measurements.RootElement.GetProperty("anchor_validation_completed_count").GetInt32());
        Assert.Equal("projection_batch_excludes_final_bundle", measurements.RootElement.GetProperty("document_validation_scope").GetString());
        Assert.DoesNotContain("Petit-déjeuner", record.Text, StringComparison.Ordinal);
        Assert.True((double)record.Values["DurationMs"]! >= 0);
    }

    [Fact]
    public void Failure_probe_does_not_claim_completed_anchor_validation()
    {
        var logger = new DiagnosticRecordingLogger<DoclingCanonicalAnchorBatchTests>();
        using var parent = BackendDiagnostics.Start("test.batch", logger);
        Assert.Throws<CanonicalContractException>(() => DoclingCanonicalSourceAnchorProjector.ProjectSelections(Document(), [Selection(page: 2)]));
        var record = Assert.Single(logger.Events, item => Equals(item.Values["Operation"], "ingestion.canonical_anchor_batch"));
        Assert.Equal("failed", record.Values["Outcome"]);
        using var measurements = JsonDocument.Parse((string)record.Values["Measurements"]!);
        Assert.Equal(JsonValueKind.Null, measurements.RootElement.GetProperty("anchor_validation_completed_count").ValueKind);
    }

    private sealed class CancelingReferences(CancellationTokenSource cancellation, string value) : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => index == 0 ? value : throw new IndexOutOfRangeException();
        public IEnumerator<string> GetEnumerator() { cancellation.Cancel(); yield return value; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
