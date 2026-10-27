using System.Text;
using System.Text.Json.Nodes;
using SAAIA.Backend.Endpoints;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentTitleNavigationCanonicalTests
{
    [Theory]
    [InlineData(.6)]
    [InlineData(.69)]
    public void Native_title_navigation_links_exact_title_without_approving_overlap_or_printed_page(double pageLeft)
    {
        var (document, chunks, sections) = NativeFixture(pageLeft);
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Equal("canonical_title_native_corroborated", entry.ResolutionMethod);
        Assert.Equal(7, entry.TargetPageStart);
        Assert.Null(entry.TargetAnchorIndex);
        Assert.Equal("3", entry.CanonicalSource?.PrintedPageLabel);
        var proof = JsonNode.Parse(CanonicalContractJson.SerializeCanonical(entry.CanonicalSource!))!["nativeTitleCorroboration"]!;
        Assert.Equal("not_corroborated", proof["printedPageLabelCorroboration"]?.GetValue<string>());
        Assert.Equal("baseline_points_only", proof["geometryBasis"]?.GetValue<string>());
        Assert.Equal("source_row_physical_overlap_observed", proof["geometryWarning"]?.GetValue<string>());
        Assert.NotEmpty(proof["runSlices"]!.AsArray());
        Assert.Equal(pageLeft, document.Pages[0].Tables[0].Cells[1].Polygon!.Points[0].X);
        Assert.Equal("not_attempted", document.Pages[0].NativeTypographyObservations!.MappingAvailability);
    }

    [Fact]
    public void Native_title_navigation_context_only_target_does_not_fabricate_chunk_or_anchor()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        chunks[2] = chunks[2] with { CanonicalBlockIds = [], CanonicalContextBlockIds = ["heading"] };
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Equal(7, entry.TargetPageStart);
        Assert.Equal("heading", entry.CanonicalSource?.TargetCanonicalBlockId);
        Assert.Null(entry.TargetChunkIndex);
        Assert.Null(entry.TargetAnchorIndex);
    }

    [Fact]
    public void Native_title_navigation_preserves_complete_surrogate_pair_offsets()
    {
        const string title = "1.0 Safe 🚀 valve operation";
        var (document, chunks, sections) = NativeFixture(.6, title);
        Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Contains(document.Pages[0].NativeTypographyObservations!.Runs![0].Glyphs,
            glyph => glyph.RunTextLengthUtf16 == 2);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("refused")]
    [InlineData("unavailable")]
    [InlineData("empty")]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("page")]
    [InlineData("frame")]
    [InlineData("rotation")]
    [InlineData("hash")]
    [InlineData("outside")]
    [InlineData("incomplete")]
    [InlineData("duplicate_native_title")]
    [InlineData("different_section_number")]
    [InlineData("unpublished_cell")]
    [InlineData("unsupported_span")]
    public void Native_title_navigation_refuses_missing_ambiguous_or_foreign_proof(string mutation)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        var set = document.Pages[0].NativeTypographyObservations!;
        switch (mutation)
        {
            case "absent": document.Pages[0].NativeTypographyObservations = null; break;
            case "refused": set.RunAvailability = "refused"; set.RunReason = "budget_refused"; set.Runs = null;
                set.ObservedGlyphCount = set.TotalGlyphCount = null; break;
            case "unavailable": set.RunAvailability = "unavailable"; set.RunReason = "source_unavailable"; set.Runs = null;
                set.ObservedGlyphCount = set.TotalGlyphCount = null; break;
            case "empty": set.Runs = []; set.ObservedGlyphCount = set.TotalGlyphCount = 0; break;
            case "document": set.DocumentId = Guid.NewGuid(); break;
            case "revision": set.RevisionId = Guid.NewGuid(); break;
            case "source": set.SourceSha256 = new string('b', 64); break;
            case "page": set.PhysicalPage = 7; break;
            case "frame": set.CoordinateFrame.Availability = "unavailable"; break;
            case "rotation": set.CoordinateFrame.ObservedRotationDegrees = 90; break;
            case "hash": set.Runs![0].TextSha256 = new string('0', 64); break;
            case "outside": set.Runs![0].Glyphs[0].StartBaselineX = 99; break;
            case "incomplete": AttachNative(document, "1.0 Safe valve"); break;
            case "duplicate_native_title": AttachNative(document, "1.0 Safe valve operation 1.0 Safe valve operation"); break;
            case "different_section_number": AttachNative(document, "2.0 Safe valve operation"); break;
            case "unpublished_cell": chunks = [chunks[0], chunks[2]]; break;
            case "unsupported_span": document.Pages[0].Tables[0].Cells[0].RowSpan = 2; break;
        }
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }

    [Fact]
    public void Native_title_navigation_target_ambiguity_after_anchor_cap_is_not_unique()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        document.Pages[1].Blocks.AddRange(Enumerable.Range(0, 601).Select(i => Heading($"other{i}", $"Other heading {i}")));
        document.Pages[1].Blocks.Add(Heading("duplicate", "1.0 Safe valve operation"));
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }

    [Fact]
    public void Native_title_navigation_ordinary_result_stays_byte_identical_when_observations_exist()
    {
        var (document, chunks, sections) = Fixture("3");
        var before = CanonicalContractJson.SerializeCanonical(Project(document, chunks, sections));
        AttachNative(document, "1.0 Safe valve operation");
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(Project(document, chunks, sections)));
    }

    [Fact]
    public void Native_title_navigation_api_exposes_only_the_additive_qualified_proof()
    {
        const string metadata = """
        {"printedPageLabel":"3","nativeTitleCorroboration":{"geometryWarning":"source_row_physical_overlap_observed","printedPageLabelCorroboration":"not_corroborated"}}
        """;
        var item = DocumentsEndpoints.AttachCanonicalNavigationMetadata(new JsonObject { ["label"] = "Title" }, metadata);
        Assert.Equal("not_corroborated", item["nativeTitleCorroboration"]?["printedPageLabelCorroboration"]?.GetValue<string>());
    }

    [Fact]
    public void Native_title_navigation_does_not_modify_primary_or_native_observations()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        var before = CanonicalContractJson.SerializeCanonical(document);
        Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Theory]
    [InlineData("rotation")]
    [InlineData("unit")]
    [InlineData("coordinate_space")]
    public void Native_title_navigation_refuses_unproven_common_canonical_frame(string field)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        if (field == "rotation") document.Pages[0].Geometry.RotationDegrees = 90;
        if (field == "unit") document.Pages[0].Geometry.Unit = "px";
        if (field == "coordinate_space") document.Pages[0].Geometry.CoordinateSpace = "foreign";
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }

    [Theory]
    [InlineData(".extra")]
    [InlineData("·extra")]
    [InlineData("•extra")]
    public void Native_title_navigation_refuses_title_ending_inside_a_punctuated_native_token(string suffix)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        AttachNative(document, "1.0 Safe valve operation" + suffix);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_literal_boundary_unproven"]);
    }

    [Theory]
    [InlineData("........ ")]
    [InlineData("···· ")]
    [InlineData("•••• ")]
    public void Native_title_navigation_accepts_complete_dot_leaders_after_the_literal_title(string suffix)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        AttachNative(document, "1.0 Safe valve operation" + suffix);
        Assert.Single(Project(document, chunks, sections).NavigationEntries);
    }

    [Theory]
    [InlineData(" extra")]
    [InlineData(" extra ....")]
    public void Native_title_navigation_refuses_extra_native_words_inside_the_title_cells(string suffix)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        AttachNative(document, "1.0 Safe valve operation" + suffix);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_cell_text_not_complete"]);
    }

    [Fact]
    public void Native_title_navigation_refuses_native_suffix_partly_crossing_title_cell_boundary()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        const string title = "1.0 Safe valve operation";
        AttachNative(document, title + " extra");
        var run = document.Pages[0].NativeTypographyObservations!.Runs![0];
        for (var i = title.Length + 1; i < run.Glyphs.Count; i++)
        {
            run.Glyphs[i].StartBaselineX = i == title.Length + 1 ? 69.8 : 71 + i;
            run.Glyphs[i].EndBaselineX = i == title.Length + 1 ? 70.2 : 71.4 + i;
        }
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }

    [Fact]
    public void Native_title_navigation_excludes_literal_owned_by_the_other_published_cell_of_the_same_row()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        AttachNative(document, "1.0 Safe valve operation 3");
        var run = document.Pages[0].NativeTypographyObservations!.Runs![0];
        run.Glyphs[^1].StartBaselineX = 69.8;
        run.Glyphs[^1].EndBaselineX = 70.2;
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Equal("not_corroborated", entry.CanonicalSource!.NativeTitleCorroboration!.PrintedPageLabelCorroboration);
    }

    [Fact]
    public void Native_title_navigation_refuses_extra_in_a_separate_native_run_even_with_another_cell_owner()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        const string title = "1.0 Safe valve operation";
        AttachNative(document, title + " extra");
        var set = document.Pages[0].NativeTypographyObservations!;
        var original = set.Runs![0];
        var extra = new CanonicalNativeTypographyRun
        {
            RunOrdinal = 1, Text = "extra", SourceLetterOrdinalStart = title.Length + 1,
            SourceLetterOrdinalEnd = original.SourceLetterOrdinalEnd, RenderingMode = "Fill", TextSequence = 2,
            BaselineStartY = 78.5, BaselineEndY = 78.5, GeometryMetricsAvailability = "unavailable",
            GeometryMetricsMethod = "unverified", Glyphs = original.Glyphs.Skip(title.Length + 1).ToList()
        };
        for (var i = 0; i < extra.Glyphs.Count; i++)
        {
            extra.Glyphs[i].RunTextOffsetUtf16 = i;
            extra.Glyphs[i].StartBaselineX = i == 0 ? 69.8 : 71 + i;
            extra.Glyphs[i].EndBaselineX = i == 0 ? 70.2 : 71.4 + i;
        }
        original.Text = title + " ";
        original.Glyphs = original.Glyphs.Take(title.Length + 1).ToList();
        original.SourceLetterOrdinalEnd = title.Length;
        foreach (var run in new[] { original, extra })
        {
            run.TextSha256 = CanonicalNativeTypographyIdentity.Hash(run.Text);
            run.RunId = CanonicalNativeTypographyIdentity.RunId(set.SourceSha256, 1, run);
        }
        set.Runs = [original, extra];
        CanonicalNativeTypographyValidator.ValidateOrThrow(set);
        Assert.Empty(Project(document, chunks, sections).NavigationEntries);
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("wrong_row")]
    [InlineData("duplicate_owner")]
    public void Native_title_navigation_refuses_partial_baseline_without_a_unique_same_row_owner(string mutation)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        AttachNative(document, "1.0 Safe valve operation 3");
        var glyph = document.Pages[0].NativeTypographyObservations!.Runs![0].Glyphs[^1];
        var duplicate = mutation == "duplicate_owner";
        glyph.StartBaselineX = duplicate ? 69.8 : 9.8;
        glyph.EndBaselineX = duplicate ? 70.2 : 10.2;
        if (mutation != "unassigned")
        {
            var table = document.Pages[0].Tables[0];
            table.RowCount = 2;
            table.Cells.Add(new() { CellId = "foreign-owner", RowIndex = 1, ColumnIndex = 0, Text = Text("3"),
                Polygon = duplicate ? Box(.695,.2,.705,.23) : Box(.095,.2,.105,.23) });
            chunks[0] = chunks[0] with { CanonicalTableCellIds = [.. chunks[0].CanonicalTableCellIds!, "foreign-owner"] };
        }
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_cell_text_not_complete"]);
    }

    [Fact]
    public void Native_title_navigation_rejects_slices_splitting_a_valid_surrogate_pair()
    {
        var (document, chunks, sections) = NativeFixture(.6, "1.0 Safe 🚀 valve operation");
        var set = document.Pages[0].NativeTypographyObservations!;
        var run = set.Runs![0];
        var index = run.Glyphs.FindIndex(glyph => glyph.RunTextLengthUtf16 == 2);
        var original = run.Glyphs[index];
        original.RunTextLengthUtf16 = 1;
        run.Glyphs.Insert(index + 1, new() { RunTextOffsetUtf16 = original.RunTextOffsetUtf16 + 1,
            RunTextLengthUtf16 = 1, StartBaselineX = original.StartBaselineX, EndBaselineX = original.EndBaselineX });
        for (var i = 0; i < run.Glyphs.Count; i++) run.Glyphs[i].NativeLetterOrdinal = i;
        run.SourceLetterOrdinalEnd = run.Glyphs.Count - 1;
        run.RunId = CanonicalNativeTypographyIdentity.RunId(set.SourceSha256, 1, run);
        set.ObservedGlyphCount = set.TotalGlyphCount = run.Glyphs.Count;
        CanonicalNativeTypographyValidator.ValidateOrThrow(set);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_split_unicode_scalar"]);
    }

    [Fact]
    public void Native_title_navigation_cannot_reuse_one_native_title_for_duplicate_source_rows()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        DuplicateNativeRows(document, ref chunks, 2);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.PagesValidated);
        Assert.Equal(2, result.CanonicalDiagnostics?.NativeTitleCorroboration?.Refused);
    }

    [Fact]
    public void Native_title_navigation_candidate_cap_is_cumulative_and_explicit()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        DuplicateNativeRows(document, ref chunks, 33);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(33, result.CanonicalDiagnostics?.NativeTitleCorroboration?.Candidates);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.PagesValidated);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_candidate_limit"]);
    }

    [Fact]
    public void Native_title_navigation_scan_budget_is_shared_across_candidates()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        DuplicateNativeRows(document, ref chunks, 2);
        var set = document.Pages[0].NativeTypographyObservations!;
        var run = set.Runs![0];
        const string filler = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx ";
        run.Text = string.Concat(Enumerable.Repeat(filler, 19_999)) + "1.0 Safe valve operation";
        run.Glyphs = Enumerable.Range(0, 20_000).Select(i => new CanonicalNativeTypographyGlyph
        {
            NativeLetterOrdinal = i, RunTextOffsetUtf16 = i * 32, RunTextLengthUtf16 = i == 19_999 ? 24 : 32,
            StartBaselineX = 12, EndBaselineX = 12.4
        }).ToList();
        run.SourceLetterOrdinalEnd = 19_999;
        run.TextSha256 = CanonicalNativeTypographyIdentity.Hash(run.Text);
        run.RunId = CanonicalNativeTypographyIdentity.RunId(set.SourceSha256, 1, run);
        set.ObservedGlyphCount = set.TotalGlyphCount = 20_000;
        CanonicalNativeTypographyValidator.ValidateOrThrow(set);
        var result = Project(document, chunks, sections);
        Assert.Empty(result.NavigationEntries);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.PagesValidated);
        Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_scan_limit"]);
        Assert.InRange(result.CanonicalDiagnostics!.NativeTitleCorroboration!.ScanUnitsReserved, 1, 2_000_000);
    }

    [Fact]
    public void Native_title_navigation_appends_after_ordinary_and_legacy_without_reindexing_them()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        var table = document.Pages[0].Tables[0];
        table.RowCount = 2;
        table.Cells.Add(new() { CellId = "other-title", RowIndex = 1, ColumnIndex = 0,
            Text = Text("2.0 Other operation ...."), Polygon = Box(.1,.4,.7,.43) });
        table.Cells.Add(new() { CellId = "other-page", RowIndex = 1, ColumnIndex = 1,
            Text = Text("8"), Polygon = Box(.8,.4,.9,.43) });
        document.Pages.Add(new() { PageNumber = 8, Blocks = [Heading("other-heading", "2.0 Other operation")] });
        var legacy = new ProjectedRetrievalChunk(6, null, null, 1, 1, "Release Validation Checklist 8", 8, [1], "navigation",
            ContentRole: RetrievalContentClassifier.NavigationRole, NavigationReason: "table_of_contents", NavigationScore: 1);
        chunks = [.. chunks, Navigation(3, "other-title"), Navigation(4, "other-page"),
            Body(5, 8, "2.0 Other operation\nRelease Validation Checklist. Confirm all documented conditions before completion.")
                with { CanonicalBlockIds = ["other-heading"] }, legacy];
        sections = [.. sections, new ExtractedDocumentSection(1, "Release Validation Checklist", 1, 8, 8, 1, null)];
        var native = document.Pages[0].NativeTypographyObservations;
        document.Pages[0].NativeTypographyObservations = null;
        var baseline = Project(document, chunks, sections).NavigationEntries;
        Assert.Equal(2, baseline.Count);
        document.Pages[0].NativeTypographyObservations = native;
        var current = Project(document, chunks, sections).NavigationEntries;
        Assert.Equal(3, current.Count);
        Assert.Equal(CanonicalContractJson.SerializeCanonical(baseline), CanonicalContractJson.SerializeCanonical(current.Take(2).ToArray()));
        Assert.Equal("canonical_title_native_corroborated", current[^1].ResolutionMethod);
    }

    [Fact]
    public void Native_title_navigation_keeps_multiple_runs_and_wrapped_baselines_as_literal_slices()
    {
        var (document, chunks, sections) = NativeFixture(.6);
        var set = document.Pages[0].NativeTypographyObservations!;
        var original = set.Runs![0];
        var ranges = new[] { (Start: 0, Length: 4), (Start: 4, Length: 5), (Start: 9, Length: 15) };
        set.Runs = ranges.Select((range, index) =>
        {
            var run = new CanonicalNativeTypographyRun
            {
                RunOrdinal = index, Text = original.Text.Substring(range.Start, range.Length),
                SourceLetterOrdinalStart = range.Start, SourceLetterOrdinalEnd = range.Start + range.Length - 1,
                RenderingMode = "Fill", TextSequence = index == 0 ? 1 : 2,
                BaselineStartY = index == 2 ? 77.5 : 78.5, BaselineEndY = index == 2 ? 77.5 : 78.5,
                GeometryMetricsAvailability = "unavailable", GeometryMetricsMethod = "unverified",
                Glyphs = original.Glyphs.Skip(range.Start).Take(range.Length).Select((glyph, offset) =>
                    new CanonicalNativeTypographyGlyph { NativeLetterOrdinal = glyph.NativeLetterOrdinal,
                        RunTextOffsetUtf16 = offset, RunTextLengthUtf16 = 1,
                        StartBaselineX = glyph.StartBaselineX, EndBaselineX = glyph.EndBaselineX }).ToList()
            };
            run.TextSha256 = CanonicalNativeTypographyIdentity.Hash(run.Text);
            run.RunId = CanonicalNativeTypographyIdentity.RunId(set.SourceSha256, 1, run);
            return run;
        }).ToList();
        CanonicalNativeTypographyValidator.ValidateOrThrow(set);
        var entry = Assert.Single(Project(document, chunks, sections).NavigationEntries);
        Assert.Equal(3, entry.CanonicalSource!.NativeTitleCorroboration!.RunSlices.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_title_navigation_measures_complete_metadata_with_utf8_and_existing_ids(bool longIds)
    {
        var (document, chunks, sections) = NativeFixture(.6);
        var table = document.Pages[0].Tables[0];
        var title = table.Cells[0];
        var label = table.Cells[1];
        table.ColumnCount = 64;
        table.Cells = Enumerable.Range(0, 62).Select(i => new CanonicalTableCell
        {
            CellId = $"{i:D2}" + (longIds ? new string('é', 198) : "empty"), ColumnIndex = i,
            Text = Text(""), Polygon = Box(.001 + i * .001, .2, .0015 + i * .001, .23)
        }).ToList();
        title.ColumnIndex = 62;
        label.ColumnIndex = 63;
        table.Cells.Add(title);
        table.Cells.Add(label);
        chunks = [chunks[0] with { CanonicalTableCellIds = table.Cells.Select(cell => cell.CellId).ToArray() }, chunks[2]];
        var result = Project(document, chunks, sections);
        if (longIds)
        {
            Assert.Empty(result.NavigationEntries);
            Assert.Equal(0, result.CanonicalDiagnostics?.NativeTitleCorroboration?.Links);
            Assert.Equal(1, result.CanonicalDiagnostics?.NativeTitleCorroboration?.ReasonCounts["native_title_metadata_byte_limit"]);
        }
        else
        {
            var entry = Assert.Single(result.NavigationEntries);
            Assert.True(Encoding.UTF8.GetByteCount(DocumentFoundationRepo.SerializeNavigationEntryMetadata(entry, int.MaxValue)) <= 16 * 1024);
        }
    }

    private static void DuplicateNativeRows(CanonicalDocument document, ref ProjectedRetrievalChunk[] chunks, int count)
    {
        var table = document.Pages[0].Tables[0];
        table.RowCount = count;
        table.Cells = Enumerable.Range(0, count).SelectMany(row => new[]
        {
            new CanonicalTableCell { CellId = $"title{row}", RowIndex = row, ColumnIndex = 0,
                Text = Text("1.0 Safe valve operation ........"), Polygon = Box(.1,.2,.7,.23) },
            new CanonicalTableCell { CellId = $"page{row}", RowIndex = row, ColumnIndex = 1,
                Text = Text("3"), Polygon = Box(.6,.2,.9,.23) }
        }).ToList();
        chunks = [chunks[0] with { CanonicalTableCellIds = table.Cells.Select(cell => cell.CellId).ToArray() }, chunks[2]];
    }

    internal static (CanonicalDocument Document, ProjectedRetrievalChunk[] Chunks, ExtractedDocumentSection[] Sections)
        NativeFixture(double pageLeft, string title = "1.0 Safe valve operation", string printedPage = "3")
    {
        var fixture = Fixture(printedPage);
        fixture.Document.Pages[0].Tables[0].Cells[0].Text = Text(title + " ........");
        fixture.Document.Pages[0].Tables[0].Cells[1].Polygon = Box(pageLeft, .2, .9, .23);
        fixture.Document.Pages[1].Blocks[0].Text = Text(title);
        AttachNative(fixture.Document, title);
        return fixture;
    }

    private static void AttachNative(CanonicalDocument document, string text)
    {
        document.Pages[0].Geometry.Width = document.Pages[0].Geometry.Height = 100;
        document.Pages[0].Geometry.Unit = "pt";
        var run = new CanonicalNativeTypographyRun
        {
            RunOrdinal = 0, Text = text, RenderingMode = "Fill", TextSequence = 1,
            BaselineStartY = 78.5, BaselineEndY = 78.5,
            GeometryMetricsAvailability = "unavailable", GeometryMetricsMethod = "unverified"
        };
        var offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            run.Glyphs.Add(new() { NativeLetterOrdinal = run.Glyphs.Count, RunTextOffsetUtf16 = offset,
                RunTextLengthUtf16 = rune.Utf16SequenceLength, StartBaselineX = 12 + .5 * run.Glyphs.Count,
                EndBaselineX = 12.4 + .5 * run.Glyphs.Count });
            offset += rune.Utf16SequenceLength;
        }
        run.SourceLetterOrdinalEnd = run.Glyphs.Count - 1;
        run.TextSha256 = CanonicalNativeTypographyIdentity.Hash(text);
        run.RunId = CanonicalNativeTypographyIdentity.RunId(document.Source.Sha256, 1, run);
        document.Pages[0].NativeTypographyObservations = new()
        {
            DocumentId = document.DocumentId, RevisionId = document.RevisionId, SourceSha256 = document.Source.Sha256,
            SourceHandleSha256Before = document.Source.Sha256, SourceHandleSha256After = document.Source.Sha256,
            PhysicalPage = 1, PdfPigAssemblyVersion = "0.1.13.0", PdfPigAssemblySha256 = new string('d', 64),
            ResourcesAvailability = "observed", Resources = [], RunAvailability = "observed", Runs = [run],
            ObservedGlyphCount = run.Glyphs.Count, TotalGlyphCount = run.Glyphs.Count,
            CoordinateFrame = new() { Availability = "available", PageWidthPoints = 100, PageHeightPoints = 100,
                ObservedRotationDegrees = 0, ObservedCropBoxLeft = 0, ObservedCropBoxBottom = 0,
                ObservedMediaBoxLeft = 0, ObservedMediaBoxBottom = 0 }
        };
        var observed = Assert.IsType<CanonicalNativeTypographySet>(document.Pages[0].NativeTypographyObservations);
        CanonicalNativeTypographyValidator.ValidateOrThrow(observed);
    }
}
