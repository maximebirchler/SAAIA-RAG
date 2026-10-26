using System.Text.Json;
using System.Reflection;
using System.Xml.Linq;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisStructuredTableTests
{
    [Theory]
    [InlineData("revision")]
    [InlineData("source_hash")]
    public void Structured_table_prompt_failure_keeps_the_exact_source_key_of_its_own_revision(string changedField)
    {
        var first=Fragment("E1",[0,1],"one","a");
        var original=Fragment("E2",[0,2],"two","b");
        var old=original.Reference;
        var second=original with
        {
            Reference=new AdvancedAnalysisResultEvidence
            {
                EvidenceId=old.EvidenceId,DocId=old.DocId,DocPath=old.DocPath,
                RevisionId=changedField=="revision" ? Guid.NewGuid().ToString() : old.RevisionId,
                SourceHash=changedField=="source_hash" ? new string('b',64) : old.SourceHash,
                PageStart=old.PageStart,PageEnd=old.PageEnd,ChunkId=old.ChunkId,AnchorId=old.AnchorId
            },
            TableFragment=new(0,0,0,[],"structured_table_fragment_invalid")
        };
        var request=new AdvancedAnalysisProviderRequest(Guid.NewGuid(),Guid.NewGuid(),"fixture",
            new AdvancedAnalysisHandoffEnvelope
            {
                RequestText="Read the observed table.", Language="en",
                Load=new() { PlanKind="direct_answer",AnswerUnitCount=1 }
            },[first,second],[]);
        var provider=new OpenAiCompatibleAdvancedAnalysisProvider(new NoTablePromptHttp(),
            new() { Provider="customer-server",LlmLocation="internal",MaximumEvidencePromptCharacters=12_000 },null);
        var projected=typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildPromptEvidence",
            BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(provider,
                [request,request.Evidence,new Dictionary<string,HashSet<string>>(),null])!;
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var visible=JsonSerializer.SerializeToElement(projected,options).EnumerateArray().ToArray();
        var expected=visible.Single(item=>item.GetProperty("evidenceId").GetString()=="E2")
            .GetProperty("sourceKey").GetString();
        var views=typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildStructuredTablesForPrompt",
            BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(provider,[projected])!;
        var failure=JsonSerializer.SerializeToElement(views,options).EnumerateArray()
            .Single(item=>item.GetProperty("html").GetString()=="");
        Assert.Equal(expected,failure.GetProperty("sourceKey").GetString());
    }

    private sealed class NoTablePromptHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)=>throw new InvalidOperationException("No HTTP is permitted in this prompt-only test.");
    }

    [Fact]
    public void Structured_table_parser_keeps_escaped_literals_and_requires_published_cell_anchors()
    {
        const string html="""<table data-source-table-ordinal="0"><tr><th data-cell-role="column_header">Value</th></tr><tr><td>&lt;limit&gt; &amp; -5 °C</td></tr></table>""";
        var map=JsonSerializer.Serialize(new CanonicalTableCellMap(CanonicalTableCellMap.CurrentSchemaVersion,0,2,1,
        [new("head",0,0,1,1,"column_header",CanonicalTableCellMap.Hash("Value")),
            new("body",1,0,1,1,"data",CanonicalTableCellMap.Hash("<limit> & -5 °C"))]));
        var parsed=AdvancedAnalysisTableFragment.Parse(html,"[\"head\",\"body\"]",cellMapJson:map)!;
        Assert.Null(parsed.ErrorCode);
        Assert.Equal("<limit> & -5 °C",XElement.Parse(parsed.Rows[1].Xml).Element("td")!.Value);
        Assert.NotNull(AdvancedAnalysisTableFragment.Parse(html,"[]")!.ErrorCode);
        Assert.NotNull(AdvancedAnalysisTableFragment.Parse(html,"[\"same\",\"same\"]")!.ErrorCode);
        Assert.Equal("structured_table_cell_map_unavailable",
            AdvancedAnalysisTableFragment.Parse(html,"[\"head\",\"body\"]")!.ErrorCode);
        Assert.NotNull(AdvancedAnalysisTableFragment.Parse(html,"[\"head\",\"body\"]",cellMapJson:
            map.Replace(CanonicalTableCellMap.Hash("<limit> & -5 °C"),new string('0',64),StringComparison.Ordinal))!.ErrorCode);
        Assert.NotNull(AdvancedAnalysisTableFragment.Parse(html,"[\"head\",\"body\"]",cellMapJson:
            map.Replace("\"row\":1","\"row\":0",StringComparison.Ordinal))!.ErrorCode);
        Assert.Null(AdvancedAnalysisTableFragment.Parse("No grid here",null));
    }

    [Fact]
    public void Structured_table_assembler_rejects_conflicting_source_row_or_cell_identity_without_merging_text()
    {
        var first=Fragment("E1",[0,1],"old","a");
        var conflict=Fragment("E2",[0,1],"new","b");
        var view=Assert.Single(AdvancedAnalysisStructuredTableAssembler.Build([first,conflict],null,10_000));
        Assert.False(view.Complete);
        Assert.Equal("structured_table_row_conflict",view.Status);
        Assert.Empty(view.Html);
        Assert.Empty(view.EvidenceIds);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("document")]
    [InlineData("page")]
    public void Structured_table_assembler_never_joins_distinct_source_identities(string difference)
    {
        var first=Fragment("E1",[0,1],"one","a");
        var second=Fragment("E2",[0,2],"two","b");
        var old=second.Reference;
        var reference=new AdvancedAnalysisResultEvidence
        {
            EvidenceId=old.EvidenceId,DocId=difference=="document" ? Guid.NewGuid().ToString() : old.DocId,
            RevisionId=difference=="revision" ? Guid.NewGuid().ToString() : old.RevisionId,
            SourceHash=difference=="source" ? new string('b',64) : old.SourceHash,
            DocPath=old.DocPath,ChunkId=old.ChunkId,AnchorId=old.AnchorId,
            PageStart=difference=="page" ? 3 : old.PageStart,PageEnd=difference=="page" ? 3 : old.PageEnd
        };
        second=second with { Reference=reference };
        var views=AdvancedAnalysisStructuredTableAssembler.Build([first,second],null,10_000);
        Assert.Equal(2,views.Count);
        Assert.All(views,view=>Assert.False(view.Complete));
        Assert.All(views,view=>Assert.Single(view.EvidenceIds));
    }

    [Fact]
    public void Structured_table_assembler_reports_missing_rows_and_canonical_only_completeness_basis()
    {
        var view=Assert.Single(AdvancedAnalysisStructuredTableAssembler.Build([Fragment("E1",[0,2],"two","b")],null,10_000));
        Assert.False(view.Complete);
        Assert.Equal(new[] { 1 },view.MissingRowIndices);
        Assert.Equal("observed_published_canonical_grid_rows",view.CompletenessBasis);
        Assert.False(view.SourceFidelityValidated);
    }

    [Fact]
    public void Structured_table_assembler_paging_inside_rowspan_keeps_the_original_indivisible_group()
    {
        var rows=new[]
        {
            new AdvancedAnalysisTableRow(0,"<tr><th data-cell-role=\"column_header\">Header</th></tr>",["h"],true,1),
            new AdvancedAnalysisTableRow(1,"<tr><td rowspan=\"2\">shared</td><td>one</td></tr>",["s","a"],false,3),
            new AdvancedAnalysisTableRow(2,"<tr><td>two</td></tr>",["b"],false,3)
        };
        var evidence=Fragment("E1",[0,1],"unused","a") with { TableFragment=new(0,3,2,rows) };
        var request=new AdvancedAnalysisSearchRequest("page",Operation:"read_source",TableOrdinal:0,TableRowOffset:2,TableRowLimit:1);
        var view=Assert.Single(AdvancedAnalysisStructuredTableAssembler.Build([evidence],request,10_000));
        Assert.Equal(new[] { 0,1,2 },view.RowIndices);
        Assert.Contains("rowspan=\"2\"",view.Html);
        Assert.True(view.Complete);
    }

    [Fact]
    public void Structured_table_assembler_budget_never_slices_a_cell_or_reports_an_omitted_row_complete()
    {
        var huge=Fragment("E1",[0,1],new string('x',4_000),"a");
        var view=Assert.Single(AdvancedAnalysisStructuredTableAssembler.Build([huge],null,1_500));
        Assert.Equal("structured_table_view_budget_exceeded",view.Status);
        Assert.False(view.Complete);
        Assert.Empty(view.Html);
        Assert.Equal(1,view.NextRowOffset);
        Assert.True(JsonSerializer.Serialize(view,new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length<=1_500);
    }

    private static AdvancedAnalysisResolvedEvidence Fragment(string id,int[] indices,string value,string cellId)
    {
        var rows=indices.Select(index=>index==0
            ? new AdvancedAnalysisTableRow(0,"<tr><th data-cell-role=\"column_header\">Header</th></tr>",["header"],true,1)
            : new AdvancedAnalysisTableRow(index,new XElement("tr",new XElement("td",value)).ToString(SaveOptions.DisableFormatting),
                [cellId],false,index+1)).ToArray();
        return new(new AdvancedAnalysisResultEvidence
        {
            EvidenceId=id,DocId="22222222-2222-2222-2222-222222222222",
            RevisionId="33333333-3333-3333-3333-333333333333",SourceHash=new string('a',64),
            DocPath="test/table.pdf",PageStart=2,PageEnd=2,ChunkId=Guid.NewGuid().ToString(),AnchorId="anchor-"+id
        },"Published fragment unchanged",TableFragment:new(0,3,1,rows));
    }
}
