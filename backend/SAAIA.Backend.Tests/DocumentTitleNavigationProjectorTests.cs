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
    public void Project_does_not_create_title_anchor_from_unpaged_profile_cards()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Document profile with one unpaged hint and one page-scoped card.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Knowledge.pdf",
            "Knowledge.pdf",
            [
                new DocumentProfileContentCard("Global Deployment Overview", null, null, "profile_hint", ["deployment"]),
                new DocumentProfileContentCard("Rollback Decision Gate", 7, 7, "content_item", ["rollback", "decision"])
            ]);

        var index = DocumentTitleNavigationProjector.Project([], [], [], profile);

        Assert.DoesNotContain(index.TitleAnchors, anchor => anchor.Title == "Global Deployment Overview");
        Assert.Contains(index.TitleAnchors, anchor =>
            anchor.SourceKind == "content_card"
            && anchor.Title == "Rollback Decision Gate"
            && anchor.PageStart == 7);
    }

    [Fact]
    public void Project_does_not_create_chunk_lead_anchor_from_navigation_dominant_mixed_chunk()
    {
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                ChunkIndex: 0,
                SectionOrdinal: 0,
                UnitOrdinal: 0,
                PageStart: 2,
                PageEnd: 2,
                Text: "Release Validation Checklist 5",
                TokenCount: 4,
                Checksum: [2],
                ChunkType: "unit_exact_v1",
                ContentRole: RetrievalContentClassifier.MixedNavigationContentRole,
                NavigationReason: "inline_page_number_list",
                NavigationScore: 0.72,
                ContentDensityScore: 0.45)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Mixed navigation chunk without enough body evidence.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Release.pdf",
            "Release.pdf");

        var index = DocumentTitleNavigationProjector.Project([], [], chunks, profile);

        Assert.DoesNotContain(index.TitleAnchors, anchor => anchor.SourceKind == "chunk_lead");
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
            new ExtractedDocumentUnit(1, 1, 5, 5, "Release Validation Checklist\nConfirm logs, owners, rollout state, rollback criteria, validation evidence, support handoff, release notes, monitoring windows, escalation contacts and final signoff before release.", 202, 30, [2])
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
    public void Project_uses_navigation_only_chunks_as_map_sources_even_when_targets_are_publishable_only()
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
        var navigationChunk = new ProjectedRetrievalChunk(
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
            NavigationScore: 0.98);
        var contentChunk = new ProjectedRetrievalChunk(
            1,
            1,
            1,
            5,
            5,
            "Release Validation Checklist\nConfirm logs, owners, rollout state, rollback criteria, validation evidence, support handoff, release notes, monitoring windows, escalation contacts and final signoff before release.",
            30,
            [4],
            "section",
            ContentRole: RetrievalContentClassifier.ContentRole,
            ContentDensityScore: 0.90);
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

        var index = DocumentTitleNavigationProjector.Project(
            sections,
            units,
            [contentChunk],
            [navigationChunk, contentChunk],
            profile);

        var entry = Assert.Single(index.NavigationEntries);
        Assert.Equal("Release Validation Checklist", entry.Label);
        Assert.Equal(0, entry.SourceChunkIndex);
        Assert.Equal(1, entry.TargetChunkIndex);
        Assert.Equal("title_exact", entry.ResolutionMethod);
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

        Assert.Equal(2, index.NavigationEntries.Count);
        var unresolved = Assert.Single(index.NavigationEntries, entry => entry.Label == "Alpha Procedure");
        Assert.Equal(12, unresolved.TargetPageStart);
        Assert.Equal("page_unresolved", unresolved.ResolutionMethod);
        Assert.Null(unresolved.TargetChunkIndex);
        Assert.Null(unresolved.TargetAnchorIndex);

        var entry = Assert.Single(index.NavigationEntries, entry => entry.Label == "Beta Checklist");
        Assert.Equal(18, entry.TargetPageStart);
        Assert.Equal("page_content_chunk", entry.ResolutionMethod);
        Assert.Equal(1, entry.TargetChunkIndex);
        Assert.Null(entry.TargetAnchorIndex);
    }

    [Fact]
    public void Project_ResolvesMinorOcrTitleDifferencesFromStructuralNavigation()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                "Tarte au pommes",
                1,
                25,
                25,
                null,
                null),
            new ExtractedDocumentSection(
                1,
                "TarteTatin",
                1,
                33,
                33,
                null,
                null)
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                null,
                null,
                2,
                2,
                "Tarte aux pommes 25 Tarte Tatin 33",
                8,
                [1],
                RetrievalContentClassifier.NavigationChunkType,
                ContentRole:
                    RetrievalContentClassifier.NavigationRole,
                NavigationReason:
                    "docling_structural_navigation",
                NavigationScore: 1.0),
            new ProjectedRetrievalChunk(
                1,
                0,
                null,
                25,
                25,
                "Tarte au pommes\nIngredients and preparation details.",
                8,
                [2],
                "section",
                ContentRole:
                    RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90),
            new ProjectedRetrievalChunk(
                2,
                1,
                null,
                33,
                33,
                "TarteTatin\nIngredients and preparation details.",
                8,
                [3],
                "section",
                ContentRole:
                    RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "fr",
            "Document avec sommaire structurel.",
            [],
            [],
            [],
            [],
            [],
            "Cuisine/Recettes.pdf",
            "Recettes.pdf");

        var index = DocumentTitleNavigationProjector.Project(
            sections,
            [],
            chunks,
            profile);

        Assert.Contains(
            index.NavigationEntries,
            entry =>
                entry.Label == "Tarte au pommes"
                && entry.TargetPageStart == 25
                && entry.ResolutionMethod == "title_exact");
        Assert.Contains(
            index.NavigationEntries,
            entry =>
                entry.Label == "TarteTatin"
                && entry.TargetPageStart == 33
                && entry.ResolutionMethod == "title_exact");
    }

    [Fact]
    public void Project_keeps_strong_navigation_entry_unresolved_when_target_page_is_navigation_dominant()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Table of contents", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Table of contents\nAcceptance Criteria 7",
                39,
                6,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                7,
                7,
                "Acceptance Criteria 7\nRelated references 8\nChecklist 9",
                56,
                7,
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
                "Table of contents\nAcceptance Criteria 7",
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
                7,
                7,
                "Acceptance Criteria 7\nRelated references 8\nChecklist 9",
                7,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.MixedNavigationContentRole,
                NavigationReason: "inline_page_number_list",
                NavigationScore: 0.72,
                ContentDensityScore: 0.45)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Navigation-dominant mixed target page.",
            [],
            [],
            [],
            [],
            [],
            "Ops/MixedNavigation.pdf",
            "MixedNavigation.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        var entry = Assert.Single(index.NavigationEntries);
        Assert.Equal("Acceptance Criteria", entry.Label);
        Assert.Equal(7, entry.TargetPageStart);
        Assert.Null(entry.TargetChunkIndex);
        Assert.Null(entry.TargetAnchorIndex);
        Assert.Equal("page_unresolved", entry.ResolutionMethod);
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
                62,
                62,
                "Ingredients pommes de terre et oignons. Preparation cuire doucement.",
                67,
                8,
                [2]),
            new ExtractedDocumentUnit(
                2,
                0,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                78,
                10,
                [3])
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
                62,
                62,
                "Ingredients pommes de terre et oignons. Preparation cuire doucement.",
                8,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.95),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                10,
                [5],
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
                62,
                62,
                "Ingredients pommes de terre et oignons. Preparation cuire doucement.",
                67,
                8,
                [2]),
            new ExtractedDocumentUnit(
                2,
                0,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                78,
                10,
                [3])
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
                62,
                62,
                "Ingredients pommes de terre et oignons. Preparation cuire doucement.",
                8,
                [4],
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.95),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                66,
                66,
                "Ingredients fruits de saison, pate et huile. Preparation faire frire les fruits.",
                10,
                [5],
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
            && entry.TargetChunkIndex == 2
            && entry.ResolutionMethod == "nearby_page_content_chunk");
    }

    [Fact]
    public void Project_resolves_title_anchor_on_header_only_page_to_following_content_chunk()
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
                "Index\nAlpha Beta Procedure 12",
                29,
                5,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                12,
                12,
                "Alpha Beta Procedure\nCategory: operational checklist",
                52,
                6,
                [2]),
            new ExtractedDocumentUnit(
                2,
                0,
                13,
                13,
                "Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, remove the old component, install the replacement, test the assembly under load and record the result.",
                183,
                30,
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
                "Index\nAlpha Beta Procedure 12",
                5,
                [4],
                "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.98),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                12,
                12,
                "Alpha Beta Procedure\nCategory: operational checklist",
                6,
                [5],
                "unit_exact_v1",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.95),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                13,
                13,
                "Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, remove the old component, install the replacement, test the assembly under load and record the result.",
                30,
                [6],
                "unit_exact_v1",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.90)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Operational procedures.",
            [],
            [],
            [],
            [],
            [],
            "Ops/Procedure.pdf",
            "Procedure.pdf",
            [
                new DocumentProfileContentCard(
                    "Alpha Beta Procedure",
                    12,
                    12,
                    "content_item",
                    ["alpha", "beta", "procedure"])
            ]);

        var index = DocumentTitleNavigationProjector.Project(sections, units, chunks, profile);

        var entry = Assert.Single(index.NavigationEntries);
        Assert.Equal("Alpha Beta Procedure", entry.Label);
        Assert.Equal(13, entry.TargetPageStart);
        Assert.Equal(2, entry.TargetChunkIndex);
        Assert.NotNull(entry.TargetAnchorIndex);
        Assert.Equal("title_exact_forward_content_chunk", entry.ResolutionMethod);
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
    public void Project_ignores_decimal_section_numbers_in_technical_content()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Technical Requirements", 1, 1, 31, 1, null)
        };
        var sourceText =
            "Where a contactor is installed ahead of the 17.10* Parts List. " +
            "tests shall be performed to ensure compliance with Section 6.5.";
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                31,
                31,
                sourceText,
                16,
                [2],
                "unit_exact_v1",
                ContentRole: RetrievalContentClassifier.MixedNavigationContentRole,
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.69,
                ContentDensityScore: 0.70),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                17,
                17,
                "Equipment Grounding Conductor Terminal. Where capacitors are installed for motor power factor correction, conductors and terminals shall meet the listed requirements.",
                20,
                [3],
                "unit_exact_v1",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.70),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                6,
                6,
                "Specific provisions and compliance requirements are described for the applicable equipment.",
                11,
                [4],
                "unit_exact_v1",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.70)
        };
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Technical content with decimal section references.",
            [],
            [],
            [],
            [],
            [],
            "Standards/Technical.pdf",
            "Technical.pdf");

        var index = DocumentTitleNavigationProjector.Project(sections, [], chunks, profile);

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
    public void Project_parses_structural_table_of_contents_rows_with_column_separators()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                "1.0 INTRODUCTION",
                1,
                9,
                9,
                null,
                null),
            new ExtractedDocumentSection(
                1,
                "1.1 Purpose and Applicability",
                2,
                9,
                9,
                null,
                null),
            new ExtractedDocumentSection(
                2,
                "1.2 Target Audience",
                2,
                9,
                9,
                null,
                null),
            new ExtractedDocumentSection(
                3,
                "2.4 Role in the Certification and Accreditation Process",
                2,
                13,
                13,
                null,
                null),
            new ExtractedDocumentSection(
                4,
                "Certification",
                2,
                44,
                44,
                null,
                null),
            new ExtractedDocumentSection(
                5,
                "3.0 SECURITY CATEGORIZATION OF INFORMATION AND INFORMATION SYSTEMS",
                1,
                17,
                17,
                null,
                null),
            new ExtractedDocumentSection(
                6,
                "3.1.1 Security Categories",
                3,
                17,
                17,
                null,
                null),
            new ExtractedDocumentSection(
                7,
                "4.1.2 Identification of Management and Support Information",
                2,
                25,
                25,
                null,
                null),
            new ExtractedDocumentSection(
                8,
                "4.2.3 Examples of FIPS 199-Based Selection of Impact Levels",
                2,
                30,
                30,
                null,
                null),
            new ExtractedDocumentSection(
                9,
                "Volume I: Guide for Mapping Types of Information and Information Systems to Security Categories",
                1,
                5,
                5,
                null,
                null)
        };
        var navigationText = string.Join(
            Environment.NewLine,
            "1.0 | INTRODUCTION..................................................................................................................1",
            "1.1 | Purpose and Applicability ......................................................................................................1",
            "1.2 | Target Audience.....................................................................................................................1",
            "2.4 | Role in the Certification and Accreditation Process ..............................................................5",
            "3.0 | SECURITY CATEGORIZATION OF INFORMATION AND INFORMATION SYSTEMS.............................................................................................................................. | 9",
            "3.1.1 Security Categories........................................................................................................9",
            "4.1.2 Identification of Management and Support Information | .............................................16",
            "4.2.3 Examples of FIPS 199-Based Selection | of Impact Levels | ..........................................22");
        var navigationChunk = new ProjectedRetrievalChunk(
            0,
            null,
            null,
            5,
            5,
            navigationText,
            42,
            [1],
            RetrievalContentClassifier.NavigationChunkType,
            ContentRole: RetrievalContentClassifier.NavigationRole,
            NavigationReason: "docling_structural_navigation",
            NavigationScore: 1.0);
        var targetChunks = sections
            .Select((section, index) => new ProjectedRetrievalChunk(
                index + 1,
                section.Ordinal,
                null,
                section.PageStart,
                section.PageEnd,
                $"{section.Title}{Environment.NewLine}"
                + "Substantive body content with controls, context, "
                + "implementation details and validation evidence.",
                40,
                new byte[] { (byte)(index + 2) },
                "section",
                ContentRole: RetrievalContentClassifier.ContentRole,
                ContentDensityScore: 0.95))
            .ToArray();
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_canonical_v3",
            "en",
            "Structured table of contents fixture.",
            [],
            [],
            [],
            [],
            [],
            "Standards/Contents.pdf",
            "Contents.pdf");

        var index = DocumentTitleNavigationProjector.Project(
            sections,
            [],
            targetChunks,
            [navigationChunk, .. targetChunks],
            profile);

        Assert.All(
            index.NavigationEntries,
            static entry =>
            {
                Assert.NotNull(entry.TargetChunkIndex);
                Assert.NotNull(entry.TargetAnchorIndex);
                Assert.StartsWith("title_", entry.ResolutionMethod);
            });
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label == "1.0 INTRODUCTION"
                && entry.TargetPageStart == 9);
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label == "1.1 Purpose and Applicability"
                && entry.TargetPageStart == 9);
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label == "1.2 Target Audience"
                && entry.TargetPageStart == 9);
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label
                == "2.4 Role in the Certification and Accreditation Process"
                && entry.TargetPageStart == 13);
        Assert.DoesNotContain(
            index.NavigationEntries,
            static entry => entry.Label == "Certification");
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label
                == "3.0 SECURITY CATEGORIZATION OF INFORMATION AND INFORMATION SYSTEMS"
                && entry.TargetPageStart == 17);
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label == "3.1.1 Security Categories"
                && entry.TargetPageStart == 17);
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label
                == "4.1.2 Identification of Management and Support Information"
                && entry.TargetPageStart == 25);
        Assert.Contains(
            index.NavigationEntries,
            static entry =>
                entry.Label
                == "4.2.3 Examples of FIPS 199-Based Selection of Impact Levels"
                && entry.TargetPageStart == 30);
        Assert.Equal(8, index.NavigationEntries.Count);
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

    [Fact]
    public void Project_reconstructs_wrapped_navigation_entry_only_from_exact_document_title()
    {
        var title =
            "4.3 Step 3: Review Provisional Impact Levels and Adjust/Finalize Information Type Impact Levels";
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                title,
                2,
                31,
                31,
                null,
                null)
        };
        var navigationChunk = new ProjectedRetrievalChunk(
            0,
            null,
            null,
            6,
            6,
            string.Join(
                Environment.NewLine,
                "TABLE OF CONTENTS",
                "4.3 Step 3: Review Provisional Impact Levels and Adjust/Finalize Information Type Impact",
                "Levels........................................................................23"),
            24,
            [1],
            RetrievalContentClassifier.NavigationChunkType,
            ContentRole: RetrievalContentClassifier.NavigationRole,
            NavigationReason: "docling_structural_navigation",
            NavigationScore: 1.0);
        var targetChunk = new ProjectedRetrievalChunk(
            1,
            0,
            null,
            31,
            31,
            $"{title}{Environment.NewLine}"
            + "Substantive body content with implementation details and validation evidence.",
            40,
            [2],
            "section",
            ContentRole: RetrievalContentClassifier.ContentRole,
            ContentDensityScore: 0.95);
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_canonical_v3",
            "en",
            "Wrapped table of contents fixture.",
            [],
            [],
            [],
            [],
            [],
            "Standards/Contents.pdf",
            "Contents.pdf");

        var index = DocumentTitleNavigationProjector.Project(
            sections,
            [],
            [targetChunk],
            [navigationChunk, targetChunk],
            profile);

        var entry = Assert.Single(index.NavigationEntries);
        Assert.Equal(title, entry.Label);
        Assert.Equal(31, entry.TargetPageStart);
        Assert.Equal("title_exact", entry.ResolutionMethod);
        Assert.Equal(1, entry.TargetChunkIndex);
        Assert.Equal(0, entry.TargetAnchorIndex);
    }
}
