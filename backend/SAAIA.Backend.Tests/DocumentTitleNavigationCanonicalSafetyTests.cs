using System.Text.Json.Nodes;
using SAAIA.Backend.Endpoints;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentTitleNavigationCanonicalTests
{
    [Fact]
    public void Canonical_navigation_ignores_source_fragment_not_in_publication_set()
    {
        var (document, chunks, sections) = Fixture("3");
        var published = new[] { chunks[0], chunks[2] };
        var result = DocumentTitleNavigationProjector.Project(sections, [], published, chunks,
            Profile(), document);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.ReasonCounts["source_chunk_not_published"]);
        Assert.Equal(1, result.CanonicalDiagnostics?.ReasonCounts["unpublished_row_cell"]);
    }

    [Fact]
    public void Canonical_navigation_does_not_accept_another_chunk_reusing_a_published_index()
    {
        var (document, chunks, sections) = Fixture("3");
        var published = new[] { chunks[0] with { Text = "Another source representation." }, chunks[1], chunks[2] };
        var result = DocumentTitleNavigationProjector.Project(sections, [], published, chunks, Profile(), document);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.RowsRefused);
    }

    [Fact]
    public void Canonical_navigation_keeps_disjoint_same_line_observations_with_overlapping_grid_slots()
    {
        var (document, chunks, sections) = Fixture("3");
        var table = document.Pages[0].Tables[0];
        table.ColumnCount = 3;
        table.Cells[0].ColumnSpan = 2;
        table.Cells[1].ColumnIndex = 1;
        Assert.Single(Project(document, chunks, sections).NavigationEntries);
        table.Cells[1].Polygon = Box(.6, .2, .9, .23);
        var refused = Project(document, chunks, sections);
        Assert.Empty(refused.NavigationEntries);
        Assert.Equal(1, refused.CanonicalDiagnostics?.ReasonCounts["ambiguous_source_row"]);
    }

    [Fact]
    public void Canonical_navigation_refuses_physical_overlap_even_with_distinct_declared_columns()
    {
        var (document, chunks, sections) = Fixture("3");
        document.Pages[0].Tables[0].Cells[1].Polygon = Box(.6, .2, .9, .23);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.ReasonCounts["ambiguous_source_row"]);
    }

    [Fact]
    public void Canonical_navigation_does_not_join_a_printed_page_from_another_source_row()
    {
        var (document, chunks, sections) = Fixture("3");
        document.Pages[0].Tables[0].Cells[1].RowIndex = 1;
        document.Pages[0].Tables[0].RowCount = 2;
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }

    [Fact]
    public void Canonical_navigation_does_not_accept_page_mismatch_foreign_cell_or_data_table()
    {
        foreach (var mutation in new[] { "page", "cell", "purpose" })
        {
            var (document, chunks, sections) = Fixture("3");
            if (mutation == "page") chunks[1] = chunks[1] with { PageStart = 2, PageEnd = 2 };
            if (mutation == "cell") chunks[1] = chunks[1] with { CanonicalTableCellIds = ["foreign"] };
            if (mutation == "purpose") document.Pages[0].Tables[0].Provenance.Attributes["doclingLabel"] = "table";
            Assert.Empty(Project(document, chunks, sections).NavigationEntries);
        }
    }

    [Theory]
    [InlineData("furniture")]
    [InlineData("native")]
    [InlineData("navigation_page")]
    public void Canonical_navigation_does_not_resolve_against_alternative_or_navigation_titles(string kind)
    {
        var (document, chunks, sections) = Fixture("3");
        var block = document.Pages[1].Blocks[0];
        if (kind == "furniture") block.IsRepeatedFurniture = true;
        if (kind == "native") block.BlockType = CanonicalNativeTextCoverageReconciler.RecoveryBlockType;
        if (kind == "navigation_page") document.Pages[1].Tables.Add(new CanonicalTable
        { Provenance = new CanonicalExtractionProvenance { Attributes = new() { ["doclingLabel"] = "document_index" } } });
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Null(entry.TargetPageStart);
    }

    [Fact]
    public void Canonical_navigation_target_scan_limit_cannot_certify_an_early_unique_title()
    {
        var (document, chunks, sections) = Fixture("3");
        document.Pages[1].Blocks.AddRange(Enumerable.Range(0, 20_000).Select(i => Heading($"h{i}", $"Other title {i}")));
        var result = Project(document, chunks, sections);
        var entry = Assert.Single(result.NavigationEntries);
        Assert.Null(entry.TargetPageStart);
        Assert.True(result.CanonicalDiagnostics?.LimitReached);
        Assert.Equal(1, result.CanonicalDiagnostics?.ReasonCounts["target_block_limit"]);
    }

    [Fact]
    public void Canonical_navigation_source_limit_is_explicit_and_never_falls_back_to_markup()
    {
        var (document, chunks, sections) = Fixture("3");
        document.Pages[0].Tables[0].Cells.AddRange(Enumerable.Range(0, 20_000).Select(i => new CanonicalTableCell { CellId = $"c{i}" }));
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.True(result.CanonicalDiagnostics?.LimitReached);
        Assert.Equal("unavailable", result.CanonicalDiagnostics?.Availability);
    }

    [Fact]
    public void Canonical_navigation_propagates_global_cancellation()
    {
        var (document, chunks, sections) = Fixture("3");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DocumentTitleNavigationProjector.Project(sections, [], chunks, chunks,
            Profile(), document, cancellation.Token));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("sha")]
    public void Canonical_navigation_rejects_foreign_publication_identity(string field)
    {
        var (document, _, _) = Fixture("3");
        var expectedDoc = document.DocumentId; var expectedRevision = document.RevisionId;
        var expectedSha = Convert.FromHexString(document.Source.Sha256);
        if (field == "document") document.DocumentId = Guid.NewGuid();
        if (field == "revision") document.RevisionId = Guid.NewGuid();
        if (field == "sha") document.Source.Sha256 = new string('b', 64);
        var error = Assert.Throws<InvalidOperationException>(() => DocumentFoundationRepo.ValidateCanonicalNavigationIdentity(
            document, expectedDoc, expectedRevision, expectedSha));
        Assert.Equal("canonical_navigation_source_identity_mismatch", error.Message);
    }

    [Fact]
    public void Canonical_navigation_api_legacy_omits_new_fields_and_keeps_unresolved_nulls()
    {
        const string legacy = "{\"label\":\"Old title\",\"targetPageStart\":null}";
        var old = DocumentsEndpoints.AttachCanonicalNavigationMetadata(JsonNode.Parse(legacy)!.AsObject(), "{\"tokenCount\":2}");
        Assert.Equal(legacy, old.ToJsonString());
        var current = DocumentsEndpoints.AttachCanonicalNavigationMetadata(JsonNode.Parse(legacy)!.AsObject(),
            "{\"printedPageLabel\":\"VII\",\"printedPageLabelKind\":\"roman\",\"sourceCellIds\":[\"c1\",\"c2\"],\"targetCanonicalBlockId\":null,\"targetPageSpace\":\"physical\"}");
        Assert.Equal("VII", current["printedPageLabel"]?.GetValue<string>());
        Assert.Null(current["targetPageStart"]);
        Assert.Null(current["targetCanonicalBlockId"]);
        Assert.Equal(new[] { "c1", "c2" }, current["sourceCellIds"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    private static ProjectedDocumentProfile Profile() => DocumentProfileProjector.BuildProfile("deterministic_v1", "en",
        "A source document.", [], [], [], [], [], "doc.pdf", "doc.pdf");

    [Fact]
    public void Canonical_navigation_recognized_ledger_cannot_reenter_plaintext_when_its_ids_are_missing()
    {
        var (_, chunks, sections) = Fixture("3");
        var source = chunks.Select(chunk => chunk with { OriginalChunkType = null, CanonicalTableCellIds = null }).ToArray();
        var result = Project(null, source, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(2, result.CanonicalDiagnostics?.StructuredChunksRecognized);
        Assert.Equal("unavailable", result.CanonicalDiagnostics?.Availability);
    }

    [Fact]
    public void Canonical_navigation_row_cell_cap_is_reported_as_a_limit()
    {
        var (document, chunks, sections) = Fixture("3");
        var table = document.Pages[0].Tables[0];
        table.ColumnCount = 65;
        table.Cells = Enumerable.Range(0, 65).Select(i => new CanonicalTableCell
        { CellId = $"c{i}", ColumnIndex = i, Text = Text("word"), Polygon = Box(.01 + i * .01, .2, .015 + i * .01, .23) }).ToList();
        chunks = [chunks[0] with { CanonicalTableCellIds = table.Cells.Select(cell => cell.CellId).ToArray() }, chunks[2]];
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.True(result.CanonicalDiagnostics?.LimitReached);
        Assert.Equal(1, result.CanonicalDiagnostics?.ReasonCounts["source_row_cell_limit"]);
    }

    [Fact]
    public void Canonical_navigation_uninspected_long_target_cannot_prove_an_earlier_unique_match()
    {
        var (document, chunks, sections) = Fixture("3");
        document.Pages[1].Blocks.Add(Heading("long", new string(' ', 5000) + "1.0 Safe valve operation"));
        var result = Project(document, chunks, sections);
        Assert.Null(Assert.Single(result.NavigationEntries).TargetPageStart);
        Assert.True(result.CanonicalDiagnostics?.LimitReached);
        Assert.Equal(1, result.CanonicalDiagnostics?.ReasonCounts["target_title_character_limit"]);
    }

    [Fact]
    public void Canonical_navigation_crossing_polygon_is_not_a_source_rectangle()
    {
        var (document, chunks, sections) = Fixture("3");
        var polygon = document.Pages[0].Tables[0].Cells[0].Polygon!;
        (polygon.Points[1], polygon.Points[2]) = (polygon.Points[2], polygon.Points[1]);
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }
}
