using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class RetrievalRuntimeSwitchTests
{

    [Fact]
    public void IsNearDuplicatePageOverlap_uses_stable_content_hash_across_different_paths()
    {
        var stableHash = new string('b', 64);
        var text = "This page contains a detailed shared operational procedure with enough text to qualify for near duplicate overlap checks across copied documents and import paths.";
        var first = new RagMatch(
            Score: 0.82,
            DocId: "doc-1",
            DocPath: "Knowledge/Guide.pdf",
            DocName: "Guide.pdf",
            PageStart: 4,
            PageEnd: 4,
            ChunkId: "first",
            ChunkIndex: 3,
            Text: text,
            IngestionVersion: 2,
            HashDoc: stableHash,
            EmbedText: text,
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 5,
            SectionTitle: "Procedure",
            HeadingPath: "Procedure",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var duplicatePath = first with
        {
            DocId = "doc-2",
            DocPath = "Knowledge/Imported/Guide.pdf",
            ChunkId = "second",
            Text = text + " Additional local footer.",
            EmbedText = text + " Additional local footer."
        };

        Assert.True(RetrievalSelection.IsNearDuplicatePageOverlap(first, duplicatePath));
    }

    [Fact]
    public void IsNearDuplicatePageOverlap_detects_overlapping_contained_chunks()
    {
        var broader = new RagMatch(
            0.91,
            "doc-recipe",
            "Cuisine/Classic.pdf",
            "Classic.pdf",
            3,
            5,
            "broader",
            1,
            "Intro generale. Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux.",
            1,
            "hash",
            "Intro generale. Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux.",
            "sparse_bm25_v1",
            1,
            1,
            "Classiques",
            "Classiques",
            "section_window_v1",
            null,
            null,
            null);
        var contained = broader with
        {
            PageStart = 5,
            PageEnd = 5,
            ChunkId = "contained",
            Text = "Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux.",
            EmbedText = "Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux."
        };

        Assert.True(RetrievalSelection.IsNearDuplicatePageOverlap(broader, contained));
    }

    [Fact]
    public void ContainsOrderedPhraseWindow_accepts_short_title_gaps_but_rejects_loose_topic_matches()
    {
        Assert.True(RetrievalCandidateSignals.ContainsOrderedPhraseWindow(
            "© Test\nSalade \nde lentilles\nIngrédients...",
            "salade lentilles",
            maxGapChars: 40));

        Assert.False(RetrievalCandidateSignals.ContainsOrderedPhraseWindow(
            "Cette salade peut être adaptée avec tomates, œufs, tofu, légumineuses et même des lentilles selon ce que vous avez.",
            "salade lentilles",
            maxGapChars: 40));
    }
}
