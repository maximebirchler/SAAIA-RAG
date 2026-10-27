using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentTitleNavigationCanonicalTests
{
    [Theory]
    [InlineData("VII", "roman")]
    [InlineData("3", "arabic")]
    public void Canonical_navigation_preserves_printed_label_and_joins_published_fragments(string label, string kind)
    {
        var (document, chunks, sections) = Fixture(label);
        var result = Project(document, chunks, sections);
        var entry = Assert.Single(result.NavigationEntries);
        Assert.Equal("1.0 Safe valve operation", entry.Label);
        Assert.Equal(7, entry.TargetPageStart);
        Assert.Equal(7, entry.TargetPageEnd);
        var source = Assert.IsType<CanonicalNavigationSourceEvidence>(entry.CanonicalSource);
        Assert.Equal(label, source.PrintedPageLabel);
        Assert.Equal(kind, source.PrintedPageLabelKind);
        Assert.Equal(new[] { "title", "page" }, source.SourceCellIds);
        Assert.Equal(new[] { 0, 1 }, source.SourceChunkIndices);
        Assert.Equal("heading", source.TargetCanonicalBlockId);
        Assert.Equal(1, result.CanonicalDiagnostics?.EntriesResolved);
    }

    [Fact]
    public void Canonical_navigation_keeps_unresolved_printed_page_without_using_physical_chunk()
    {
        var (document, chunks, _) = Fixture("3");
        document.Pages[1].Blocks.Clear();
        chunks = [.. chunks, Body(4, 3, "An unrelated substantive passage on physical page three.")];
        var entry = Assert.Single(Project(document, chunks, []).NavigationEntries);
        Assert.Null(entry.TargetPageStart);
        Assert.Null(entry.TargetChunkIndex);
        Assert.Equal("canonical_title_unresolved", entry.ResolutionMethod);
        Assert.Equal("3", entry.CanonicalSource?.PrintedPageLabel);
    }

    [Fact]
    public void Canonical_navigation_duplicate_title_beyond_legacy_anchor_cap_stays_ambiguous()
    {
        var (document, chunks, sections) = Fixture("3");
        for (var i = 0; i < 601; i++)
            document.Pages[1].Blocks.Add(Heading($"other{i}", $"A different heading {i}"));
        document.Pages.Add(new CanonicalPage { PageNumber = 8, Blocks = [Heading("duplicate", "1.0 Safe valve operation")] });
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Null(entry.TargetPageStart);
        Assert.Equal("canonical_title_ambiguous", entry.ResolutionMethod);
    }

    [Theory]
    [InlineData("2.0 Safe valve operation")]
    [InlineData("1.0 Sàfe valve operation")]
    public void Canonical_navigation_does_not_fold_section_identifiers_or_accents_for_target(string target)
    {
        var (document, chunks, sections) = Fixture("3");
        document.Pages[1].Blocks[0].Text = Text(target);
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Null(entry.TargetPageStart);
    }

    [Fact]
    public void Canonical_navigation_does_not_complete_row_from_unpublished_cell()
    {
        var (document, chunks, sections) = Fixture("3");
        chunks = [chunks[0], chunks[2]];
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.RowsRefused);
        Assert.True(result.CanonicalDiagnostics?.ReasonCounts.ContainsKey("unpublished_row_cell"));
    }

    [Fact]
    public void Canonical_navigation_does_not_promote_xml_attribute_numbers_if_source_is_unavailable()
    {
        var (_, chunks, sections) = Fixture("3");
        var result = Project(null, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.True(result.CanonicalDiagnostics?.ReasonCounts.ContainsKey("canonical_document_unavailable"));
    }

    [Fact]
    public void Canonical_navigation_legacy_plaintext_remains_available()
    {
        var sections = new[] { new ExtractedDocumentSection(0, "Release Validation Checklist", 1, 7, 7, 1, null) };
        var chunk = new ProjectedRetrievalChunk(0, null, null, 1, 1, "Release Validation Checklist 7", 8, [1], "navigation",
            ContentRole: RetrievalContentClassifier.NavigationRole, NavigationReason: "table_of_contents", NavigationScore: 1);
        Assert.Contains(Project(null, [chunk, Body(2, 7, "Release Validation Checklist\nConfirm logs, owners, rollout state, rollback criteria, validation evidence, support handoff, release notes, monitoring windows, escalation contacts and final signoff before release.")], sections).NavigationEntries,
            x => x.TargetPageStart == 7);
    }

    private static ProjectedDocumentTitleNavigationIndex Project(CanonicalDocument? document, ProjectedRetrievalChunk[] chunks,
        ExtractedDocumentSection[] sections) => DocumentTitleNavigationProjector.Project(sections, [], chunks, chunks,
            DocumentProfileProjector.BuildProfile("deterministic_v1", "en", "A source document.", [], [], [], [], [], "doc.pdf", "doc.pdf"), document);

    internal static (CanonicalDocument Document, ProjectedRetrievalChunk[] Chunks, ExtractedDocumentSection[] Sections) Fixture(string page)
    {
        var document = new CanonicalDocument
        {
            DocumentId = Guid.Parse("91942710-323a-44de-a962-2fec75bc7b6c"),
            RevisionId = Guid.Parse("29ab0914-5439-4d74-b3ae-338ea6ecfb2b"),
            Source = new CanonicalSourceIdentity { Sha256 = new string('a', 64) }, PageCount = 8,
            Pages = [new CanonicalPage { PageNumber = 1, Tables = [new CanonicalTable
            {
                TableId = "toc", Ordinal = 0, RowCount = 1, ColumnCount = 2,
                Provenance = new CanonicalExtractionProvenance { Attributes = new() { ["doclingLabel"] = "document_index" } },
                Cells = [new CanonicalTableCell { CellId = "title", RowIndex = 0, ColumnIndex = 0, Text = Text("1.0 Safe valve operation ................"), Polygon = Box(.1,.2,.7,.23) },
                         new CanonicalTableCell { CellId = "page", RowIndex = 0, ColumnIndex = 1, Text = Text(page), Polygon = Box(.8,.2,.9,.23) }]
            }] }, new CanonicalPage { PageNumber = 7, Blocks = [Heading("heading", "1.0 Safe valve operation")] }]
        };
        return (document, [Navigation(0, "title"), Navigation(1, "page"), Body(2, 7, "1.0 Safe valve operation\nKeep the valves closed during the operation.")],
            [new ExtractedDocumentSection(0, "1.0 Safe valve operation", 1, 7, 7, 1, null)]);
    }

    private static ProjectedRetrievalChunk Navigation(int index, string id) => new(index, null, null, 1, 1,
        $"<canonical-table-observations data-total-rows=\"100\"><cell-observation row=\"0\" column=\"{index}\">{new string('x', 240)}</cell-observation></canonical-table-observations>",
        20, [1], "navigation_index_v1", ContentRole: RetrievalContentClassifier.NavigationRole,
        OriginalChunkType: DoclingCanonicalRetrievalProjector.TableChunkType, NavigationReason: "docling_structural_navigation",
        NavigationScore: 1, CanonicalTableCellIds: [id], ChunkComposition: "canonical_table_overlap_observations_v1");

    private static ProjectedRetrievalChunk Body(int index, int page, string text) => new(index, 0, null, page, page, text,
        20, [2], "section", ContentDensityScore: .95, CanonicalBlockIds: ["heading"]);
    private static CanonicalBlock Heading(string id, string text) => new() { BlockId = id, BlockType = "heading", Text = Text(text) };
    private static CanonicalTextVariants Text(string text) => new() { Raw = text, Canonical = text, Retrieval = text, Display = text };
    private static CanonicalPolygon Box(double l, double t, double r, double b) => new()
    { Points = [new() { X=l,Y=t }, new() { X=r,Y=t }, new() { X=r,Y=b }, new() { X=l,Y=b }] };
}
