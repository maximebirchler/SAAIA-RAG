using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public async Task StructuredFlatWriter_RendersOnlyTheEvidenceAssignmentsAuthoredByTheLlm()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize the documented safety requirements.");
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"presentation":"bullets","claims":[{"text":"The guard prevents access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control remains readily accessible to the operator.","evidenceIds":["E3"]}]}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2", "E3" },
            "content_claim");

        Assert.True(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        Assert.Equal(
            "- The guard prevents access to the hazard zone [E2].\n"
            + "- The stop control remains readily accessible to the operator [E3].",
            completion.Content.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("claims", completion.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "UNITE_SELECTIONNEE",
            completion.Content,
            StringComparison.Ordinal);
        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal("source_backed_flat_writer_v1", contract.Name);
        var evidenceIdEnum = contract.Schema
            .GetProperty("properties")
            .GetProperty("claims")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("evidenceIds")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Equal(new[] { "E2", "E3" }, evidenceIdEnum);
    }

    [Fact]
    public async Task StructuredFlatWriter_RendersAMultiSourceClaimWithoutInventingAnotherId()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize the documented safety requirements.");
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"presentation":"paragraphs","claims":[{"text":"The safeguards cover both access prevention and an accessible stop control.","evidenceIds":["E2","E3"]}]}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E1", "E2", "E3" },
            "content_claim");

        Assert.True(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        Assert.Equal(
            "The safeguards cover both access prevention and an accessible stop control [E2][E3].",
            completion.Content);
        Assert.DoesNotContain("[E1]", completion.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredFlatWriter_RejectsAnEvidenceIdOutsideTheVisiblePool()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize the documented safety requirements.");
        const string raw =
            "{\"presentation\":\"paragraphs\",\"claims\":[{\"text\":\"The guard prevents access.\",\"evidenceIds\":[\"E99\"]}]}";
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion(raw)),
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2", "E3" },
            "content_claim");

        Assert.False(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        Assert.Equal(
            "structured_flat_writer_evidence_id_not_allowed",
            ReadPrivateProperty<string>(execution, "FailureReason"));
        Assert.Equal(raw, ReadPrivateProperty<string>(execution, "RawOutput"));
    }

    [Fact]
    public async Task StructuredFlatWriter_RejectsNonExtractiveNamedValueExpansion()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "List the two named requirements.");
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"presentation":"bullets","claims":[{"text":"The guard improves operational safety by reducing exposure.","evidenceIds":["E2"]},{"text":"The stop control supports rapid incident response.","evidenceIds":["E3"]}]}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with { StructuredFlatWriterEnabled = true });

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2", "E3" },
            "content_claim",
            requiredClaimCount: 2);

        Assert.False((bool)execution.GetType()
            .GetProperty("ProtocolValid")!.GetValue(execution)!);
        Assert.Equal(
            "structured_flat_writer_named_value_not_extractive",
            execution.GetType().GetProperty("FailureReason")!.GetValue(execution));
    }

    [Theory]
    [InlineData("UNITE_SELECTIONNEE 1 - The guard prevents access.")]
    [InlineData("The guard prevents access [E2].")]
    [InlineData(" ")]
    public async Task StructuredFlatWriter_RejectsControlTextOrModelWrittenCitations(
        string claimText)
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize one documented safety requirement.");
        var raw = JsonSerializer.Serialize(new
        {
            presentation = "paragraphs",
            claims = new[]
            {
                new { text = claimText, evidenceIds = new[] { "E2" } }
            }
        });
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion(raw)),
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2" },
            "named_item");

        Assert.False(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        Assert.StartsWith(
            "structured_flat_writer_claim_",
            ReadPrivateProperty<string>(execution, "FailureReason"),
            StringComparison.Ordinal);
    }
}
