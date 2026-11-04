using System.Text.Json.Nodes;
using SAAIA.Client.Core.AdvancedAnalysis;
using SAAIA.Client.Core.Presentation;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class AdvancedAnalysisCitationPresentationTests
{
    private static JsonObject Payload() => JsonNode.Parse("""
        {"intent":"advanced_analysis.answer","sources":[
          {"evidenceId":"E1","docId":"11111111-1111-1111-1111-111111111111","revisionId":"22222222-2222-2222-2222-222222222222",
           "docPath":"Guides/Procedure.pdf","docName":"Procedure.pdf","sourceHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
           "pageStart":4,"pageEnd":4,"chunkId":"chunk-one"},
          {"evidenceId":"E2","docId":"11111111-1111-1111-1111-111111111111","revisionId":"22222222-2222-2222-2222-222222222222",
           "docPath":"Guides/Procedure.pdf","docName":"Procedure.pdf","sourceHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
           "pageStart":4,"pageEnd":4,"chunkId":"chunk-two"}],
         "advancedAnalysis":{"schemaVersion":"saaia.advanced-analysis-client-state.v1","status":"succeeded",
          "originTraceId":"33333333333333333333333333333333","claims":[
           {"claimId":"C1","evidenceIds":["E1"]},{"claimId":"C20","evidenceIds":["E2"]}]}}
        """)!.AsObject();

    [Fact]
    public void Persisted_citations_keep_the_actual_proof_even_when_cards_share_a_page()
    {
        var map = AdvancedAnalysisCitationMap.Parse(Payload().ToJsonString());
        Assert.Equal("chunk-one", Assert.Single(map["C1"]).Locator.ChunkId);
        var last = Assert.Single(map["C20"]);
        Assert.Equal("chunk-two", last.Locator.ChunkId);
        Assert.Equal(4, last.Locator.Page);
        Assert.True(last.Locator.RequireExactSourceHash);
        Assert.Equal("22222222-2222-2222-2222-222222222222", last.Locator.RevisionId);
        Assert.Equal("33333333333333333333333333333333", last.Locator.OriginTraceId);
    }

    [Theory]
    [InlineData("unknown-proof")]
    [InlineData("duplicate-proof")]
    [InlineData("duplicate-claim")]
    [InlineData("unknown-version")]
    [InlineData("missing-mapping")]
    public void Invalid_or_legacy_mapping_never_guesses_a_source(string mutation)
    {
        var payload = Payload();
        var claims = payload["advancedAnalysis"]!["claims"]!.AsArray();
        if (mutation == "unknown-proof") claims[1]!["evidenceIds"]![0] = "missing";
        if (mutation == "duplicate-proof") payload["sources"]![1]!["evidenceId"] = "E1";
        if (mutation == "duplicate-claim") claims[1]!["claimId"] = "C1";
        if (mutation == "unknown-version") payload["sources"]![1]!["revisionId"] = "";
        if (mutation == "missing-mapping") payload["advancedAnalysis"]!.AsObject().Remove("claims");
        Assert.Empty(AdvancedAnalysisCitationMap.Parse(payload.ToJsonString()));
    }

    [Fact]
    public void One_claim_keeps_two_proof_locators_on_a_single_visible_page_card()
    {
        var payload = Payload();
        payload["advancedAnalysis"]!["citationSources"] = payload["sources"]!.DeepClone();
        payload["sources"]!.AsArray().RemoveAt(1);
        payload["advancedAnalysis"]!["claims"]![0]!["evidenceIds"]!.AsArray().Add("E2");
        var links = AdvancedAnalysisCitationMap.Parse(payload.ToJsonString())["C1"];
        Assert.Equal(new[] { "chunk-one", "chunk-two" }, links.Select(link => link.Locator.ChunkId));
        Assert.All(links, link => Assert.Equal(4, link.Locator.Page));
        payload["advancedAnalysis"]!["citationSources"]![1]!["sourceHash"] = new string('b', 64);
        Assert.Empty(AdvancedAnalysisCitationMap.Parse(payload.ToJsonString()));
    }

    [Fact]
    public void Multiple_cited_pages_remain_multiple_explicit_links_without_a_default_choice()
    {
        var payload = Payload();
        payload["sources"]![1]!["pageStart"] = 5;
        payload["sources"]![1]!["pageEnd"] = 6;
        payload["advancedAnalysis"]!["claims"]![0]!["evidenceIds"]!.AsArray().Add("E2");
        var links = AdvancedAnalysisCitationMap.Parse(payload.ToJsonString())["C1"];
        Assert.Equal(new int?[] { 4, 5 }, links.Select(link => link.Locator.Page));
        Assert.Equal(6, links[1].Locator.PageEnd);
    }

    [Fact]
    public void Generic_five_by_four_table_keeps_twenty_cells_and_claim_markers()
    {
        var lines = new[] { "Intro", "| Row | A | B | C | D |", "|---|---|---|---|---|" }
            .Concat(Enumerable.Range(0, 5).Select(row => $"| R{row + 1} | one [C{row * 4 + 1}] | two [C{row * 4 + 2}] | three [C{row * 4 + 3}] | four [C{row * 4 + 4}] |"))
            .Append("After").ToArray();
        Assert.True(MarkdownTableParser.TryParse(lines, 1, out var table));
        Assert.Equal(7, table!.ConsumedLineCount);
        Assert.Equal(5, table.Headers.Count);
        Assert.Equal(20, table.Rows.Sum(row => row.Count - 1));
        Assert.Contains("[C20]", table.Rows[4][4]);
    }

    [Fact]
    public void Escaped_pipes_and_owned_document_links_stay_inside_their_cell()
    {
        string[] lines = ["| Name | Proof |", "|---|---|", @"| A\|B | [[open|Guides/Procedure.pdf|4|Procedure]] |"];
        Assert.True(MarkdownTableParser.TryParse(lines, 0, out var table));
        Assert.Equal("A|B", table!.Rows[0][0]);
        Assert.Equal("[[open|Guides/Procedure.pdf|4|Procedure]]", table.Rows[0][1]);
        Assert.True(MarkdownTableParser.TryParse(["Name|Proof", "---|---", @"A|B\|"], 0, out var trailing));
        Assert.Equal("B|", trailing!.Rows[0][1]);
    }

    [Theory]
    [InlineData("````", "```")]
    [InlineData("```", "```text")]
    [InlineData("~~~~", "~~~")]
    public void Shorter_or_annotated_closing_fence_does_not_enable_table_rendering(string opening, string falseClosing)
    {
        string? fence = null;
        Assert.True(MarkdownTableParser.UpdateCodeFence(opening + "markdown", ref fence));
        Assert.False(MarkdownTableParser.UpdateCodeFence(falseClosing, ref fence));
        Assert.Equal(opening, fence);
        Assert.True(MarkdownTableParser.UpdateCodeFence(opening, ref fence));
        Assert.Null(fence);
        string[] lines = [opening + "markdown", falseClosing, "|A|B|", "|---|---|", "|one|two|", opening];
        Assert.Empty(MarkdownTableParser.ReadTables(lines));
    }

    [Fact]
    public void Excessive_table_rows_fall_back_without_publishing_a_truncated_grid()
    {
        var lines = new[] { "|A|B|", "|---|---|" }
            .Concat(Enumerable.Repeat("|one|two|", 81)).ToArray();
        Assert.False(MarkdownTableParser.TryParse(lines, 0, out _));
        Assert.Empty(MarkdownTableParser.ReadTables(lines));
    }

    [Theory]
    [InlineData("| A | B |\n|---|---|\n| only one |")]
    [InlineData("| A | B |\n|not separator|---|\n|one|two|")]
    [InlineData("| A | B |\n|---|---|")]
    public void Incomplete_or_malformed_table_is_left_as_plain_text(string text)
        => Assert.False(MarkdownTableParser.TryParse(text.Split('\n'), 0, out _));
}
