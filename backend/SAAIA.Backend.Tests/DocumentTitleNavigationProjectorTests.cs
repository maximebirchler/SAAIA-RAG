using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentTitleNavigationProjectorTests
{
    [Fact]
    public void Project_builds_title_anchors_from_sections_cards_and_chunk_leads()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Release Validation Checklist", 1, 3, 3, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 3, 3, "Release Validation Checklist\nConfirm logs, owners and rollback criteria.", 74, 9, [1])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                ChunkIndex: 0,
                SectionOrdinal: 0,
                UnitOrdinal: 0,
                PageStart: 3,
                PageEnd: 3,
                Text: "Release Validation Checklist\nConfirm logs, owners and rollback criteria.",
                TokenCount: 9,
                Checksum: [2],
                ChunkType: "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.92)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Release validation operational checklist.",
            ["release", "validation"],
            [],
            ["Release Validation Checklist"],
            [],
            [],
            "Ops/Release.pdf",
            "Release.pdf",
            [
                new DocumentProfileContentCard("Rollback Decision Gate", 4, 4, "content_item", ["rollback", "decision"])
            ]);

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Contains(index.TitleAnchors, anchor =>
            anchor.SourceKind == "section"
            && anchor.Title == "Release Validation Checklist"
            && anchor.PageStart == 3);
        Assert.Contains(index.TitleAnchors, anchor =>
            anchor.SourceKind == "content_card"
            && anchor.Title == "Rollback Decision Gate"
            && anchor.PageStart == 4);
        Assert.Contains(index.TitleAnchors, anchor =>
            anchor.TitleTokens.Contains("validation")
            && anchor.NormalizedTitle == "release validation checklist");
    }

    [Fact]
    public void Project_resolves_navigation_line_to_target_title_anchor()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Table of contents", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Release Validation Checklist", 1, 5, 5, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Table of contents\nRelease Validation Checklist 5", 55, 8, [1]),
            new ExtractedDocumentUnit(1, 1, 5, 5, "Release Validation Checklist\nConfirm logs, owners and rollback criteria.", 74, 9, [2])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Table of contents\nRelease Validation Checklist 5",
                8,
                [3],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "table_of_contents",
                NavigationScore: 0.98),
            new ProjectedRetrievalChunk(
                1,
                1,
                1,
                5,
                5,
                "Release Validation Checklist\nConfirm logs, owners and rollback criteria.",
                9,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Release validation operational checklist.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Release.pdf",
            "Release.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        var entry = Assert.Single(index.NavigationEntries);
        Assert.Equal("Release Validation Checklist", entry.Label);
        Assert.Equal("title_exact", entry.ResolutionMethod);
        Assert.Equal(5, entry.TargetPageStart);
        Assert.NotNull(entry.TargetAnchorIndex);
        Assert.NotEqual(0, entry.TargetChunkIndex.GetValueOrDefault(-1));
    }

    [Fact]
    public void Project_splits_dense_inline_navigation_using_document_anchors()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Table of contents", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Release Validation Checklist", 1, 5, 5, 1, null),
            new ExtractedDocumentSection(2, "Rollback Decision Gate", 1, 9, 9, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Table of contents Release Validation Checklist 5 Rollback Decision Gate 9",
                84,
                10,
                [1]),
            new ExtractedDocumentUnit(
                1,
                1,
                5,
                5,
                "Release Validation Checklist\nConfirm logs, owners and rollback criteria.",
                74,
                9,
                [2]),
            new ExtractedDocumentUnit(
                2,
                2,
                9,
                9,
                "Rollback Decision Gate\nDecide go, rollback, and owner handoff.",
                62,
                9,
                [3])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Table of contents Release Validation Checklist 5 Rollback Decision Gate 9",
                10,
                [4],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "table_of_contents",
                NavigationScore: 0.98),
            new ProjectedRetrievalChunk(
                1,
                1,
                1,
                5,
                5,
                "Release Validation Checklist\nConfirm logs, owners and rollback criteria.",
                9,
                [5],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90),
            new ProjectedRetrievalChunk(
                2,
                2,
                2,
                9,
                9,
                "Rollback Decision Gate\nDecide go, rollback, and owner handoff.",
                9,
                [6],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Release validation operational checklist.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Release.pdf",
            "Release.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Contains(index.NavigationEntries, entry =>
            entry.Label == "Release Validation Checklist"
            && entry.TargetPageStart == 5
            && entry.ResolutionMethod == "title_exact");
        Assert.Contains(index.NavigationEntries, entry =>
            entry.Label == "Rollback Decision Gate"
            && entry.TargetPageStart == 9
            && entry.ResolutionMethod == "title_exact");
        Assert.DoesNotContain(index.NavigationEntries, entry => entry.Label.Contains("Table of contents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_allows_unanchored_inline_navigation_from_explicit_indexes_only()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Index", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Alpha Procedure, 12 Beta Checklist, 18",
                38,
                6,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                18,
                18,
                "This page explains acceptance criteria, controls and handoff notes.",
                66,
                9,
                [2])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Alpha Procedure, 12 Beta Checklist, 18",
                6,
                [3],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.98),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                18,
                18,
                "This page explains acceptance criteria, controls and handoff notes.",
                9,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Explicit index with labels that are not repeated as section headings.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Index.pdf",
            "Index.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Single(index.NavigationEntries);
        var entry = Assert.Single(index.NavigationEntries, entry => entry.Label == "Beta Checklist");
        Assert.Equal(18, entry.TargetPageStart);
        Assert.Equal("page_content_chunk", entry.ResolutionMethod);
        Assert.Equal(1, entry.TargetChunkIndex);
        Assert.Null(entry.TargetAnchorIndex);
    }

    [Fact]
    public void Project_parses_comma_separated_inline_index_entries()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Index des recettes", 1, 67, 68, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                67,
                68,
                "Index des recettes F Filet mignon de porc enrobe de lard, 40 Fruits en beignets, 64 O Omelette aux pommes de terre, 60",
                122,
                18,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                78,
                10,
                [2])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                67,
                68,
                "Index des recettes F Filet mignon de porc enrobe de lard, 40 Fruits en beignets, 64 O Omelette aux pommes de terre, 60",
                18,
                [3],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.98),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                10,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "fr",
            "Index de recettes avec entree separee par virgule.",
            [],
            [],
            [],
            [],
            [],
            "Cuisine/Index.pdf",
            "Index.pdf",
            [
                new DocumentProfileContentCard("Fruits en beignets", 65, 65, "content_item", ["fruits", "beignets"])
            ]);

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Contains(index.NavigationEntries, entry =>
            entry.Label == "Fruits en beignets"
            && entry.TargetPageStart == 65
            && entry.ResolutionMethod == "title_exact");
    }

    [Fact]
    public void Project_resolves_index_entries_to_nearby_content_when_printed_page_is_offset()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Index des recettes", 1, 67, 68, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                67,
                68,
                "Index des recettes F Filet mignon, 40 Fruits en beignets, 64 O Omelette, 60",
                83,
                12,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                78,
                10,
                [2])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                67,
                68,
                "Index des recettes F Filet mignon, 40 Fruits en beignets, 64 O Omelette, 60",
                12,
                [3],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.98),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                10,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "fr",
            "Index de recettes sans ancre de titre exacte.",
            [],
            [],
            [],
            [],
            [],
            "Cuisine/Index.pdf",
            "Index.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Contains(index.NavigationEntries, entry =>
            entry.Label == "Fruits en beignets"
            && entry.TargetPageStart == 66
            && entry.TargetChunkIndex == 1
            && entry.ResolutionMethod == "nearby_page_content_chunk");
    }

    [Fact]
    public void Project_ignores_unanchored_direct_lines_from_weak_navigation_chunks()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations Notes", 1, 1, 18, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                12,
                12,
                "Dark ingredients and measurements are discussed in prose.",
                58,
                8,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                18,
                18,
                "Acceptance details and operational caveats are discussed in prose.",
                66,
                8,
                [2])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                21,
                21,
                "Dark Chocolate 12\nAcceptance Criteria 18",
                6,
                [3],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "inline_page_number_list",
                NavigationScore: 0.82),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                12,
                12,
                "Dark ingredients and measurements are discussed in prose.",
                8,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.86),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                18,
                18,
                "Acceptance details and operational caveats are discussed in prose.",
                8,
                [5],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.86)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Weak page-number list detected in normal content.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Weak.pdf",
            "Weak.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Empty(index.NavigationEntries);
    }

    [Fact]
    public void Project_ignores_weak_late_inline_number_lists_from_content()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Sugar Notes", 1, 21, 21, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                21,
                21,
                "Preparation: 50 min Rest: 1 h Bake: 12 to 15 min 200 g dark chocolate 60 g sugar notes 21",
                96,
                18,
                [1])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                21,
                21,
                "Preparation: 50 min Rest: 1 h Bake: 12 to 15 min 200 g dark chocolate 60 g sugar notes 21",
                18,
                [2],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "inline_page_number_list",
                NavigationScore: 0.82)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "A normal content page with quantities.",
            [],
            [],
            [],
            [],
            [],
            "Docs/Content.pdf",
            "Content.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        Assert.Empty(index.NavigationEntries);
    }

    [Fact]
    public void Project_deduplicates_navigation_entries_repeated_across_source_chunks()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Table of contents", 1, 1, 2, 1, null),
            new ExtractedDocumentSection(1, "Release Validation Checklist", 1, 5, 5, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Release Validation Checklist 5", 32, 4, [1]),
            new ExtractedDocumentUnit(1, 0, 2, 2, "Release Validation Checklist 5", 32, 4, [2]),
            new ExtractedDocumentUnit(2, 1, 5, 5, "Release Validation Checklist\nConfirm readiness.", 46, 5, [3])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Release Validation Checklist 5",
                4,
                [4],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "table_of_contents",
                NavigationScore: 0.95),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                2,
                2,
                "Release Validation Checklist 5",
                4,
                [5],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "table_of_contents",
                NavigationScore: 0.95),
            new ProjectedRetrievalChunk(
                2,
                1,
                2,
                5,
                5,
                "Release Validation Checklist\nConfirm readiness.",
                5,
                [6],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Repeated table of contents.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Release.pdf",
            "Release.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        var entry = Assert.Single(index.NavigationEntries);
        Assert.Equal("Release Validation Checklist", entry.Label);
        Assert.Equal(5, entry.TargetPageStart);
    }

    [Fact]
    public void Project_keeps_duplicate_titles_distinct_by_page()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety Checklist", 1, 2, 2, 1, null),
            new ExtractedDocumentSection(1, "Safety Checklist", 1, 9, 9, 1, null)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Two repeated sections.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Safety.pdf",
            "Safety.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, [], [], profile);

        var anchors = index.TitleAnchors
            .Where(static anchor => anchor.NormalizedTitle == "safety checklist")
            .OrderBy(static anchor => anchor.PageStart)
            .ToArray();
        Assert.Equal(2, anchors.Length);
        Assert.Equal(2, anchors[0].PageStart);
        Assert.Equal(9, anchors[1].PageStart);
    }
}
